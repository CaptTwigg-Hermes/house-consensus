"""Transactional PostgreSQL export with immutable provenance and evidence."""

from __future__ import annotations
import hashlib
import json
import math
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable
import psycopg
from psycopg import sql
from psycopg.types.json import Jsonb
from .media import MediaCache, discover_media
from .models import ExportCase

_SCHEMA = Path(__file__).with_name("schema.sql")


def ensure_schema(conn) -> None:
    conn.execute(_SCHEMA.read_text())


def _canonical(value) -> tuple[str, str]:
    text = json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    return text, hashlib.sha256(text.encode()).hexdigest()


def _confidence(value: str | None) -> float | None:
    return {"high": 1.0, "medium": 0.66, "low": 0.33}.get(value or "")


def _first(data: dict, *keys: str):
    return next((data[key] for key in keys if data.get(key) not in (None, "")), None)


def _integer(data: dict, *keys: str) -> int | None:
    value = _first(data, *keys)
    try:
        return int(round(float(value))) if value is not None else None
    except (TypeError, ValueError):
        return None


def _boolean(data: dict, *keys: str) -> bool | None:
    value = _first(data, *keys)
    if isinstance(value, bool):
        return value
    if isinstance(value, str) and value.lower() in {"true", "false"}:
        return value.lower() == "true"
    return None


def _score_breakdown(data: dict, expected_total: float | None) -> tuple[float | None, ...]:
    breakdown = data.get("family_score_breakdown")
    if not isinstance(breakdown, dict):
        return (None,) * 10

    def number(value) -> float | None:
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            return None
        try:
            return float(value)
        except (TypeError, ValueError, OverflowError):
            return None

    scores = tuple(number(breakdown.get(key)) for key in ("privacy", "kids_space", "garden", "shared_living", "practical"))
    if any(value is None or not math.isfinite(value) or not 0 <= value <= 100 for value in scores):
        return (None,) * 10

    if "weights" not in breakdown:
        weights = ()
    else:
        weight_data = breakdown["weights"]
        if not isinstance(weight_data, dict):
            return (None,) * 10
        try:
            raw_weights = tuple(weight_data[key] for key in ("privacy", "kids_space", "garden", "shared_living", "practical"))
        except KeyError:
            return (None,) * 10
        parsed_weights = tuple(number(value) for value in raw_weights)
        if any(value is None for value in parsed_weights):
            return (None,) * 10
        weights = parsed_weights
    if not weights:
        vision_keys = (
            "vision_separate_entrance", "vision_second_kitchen", "vision_internal_connection",
            "vision_split_type", "vision_en_suite_count", "vision_privacy_score",
            "vision_two_dwellings", "vision_two_family_fit", "vision_dwelling_evidence",
            "vision_bathrooms",
        )
        weights = (30.0, 20.0, 20.0, 15.0, 15.0) if any(data.get(key) is not None for key in vision_keys) else (
            0.0, 20.0 / 0.7, 20.0 / 0.7, 15.0 / 0.7, 15.0 / 0.7,
        )
    if any(not math.isfinite(weight) or weight < 0 for weight in weights):
        return (None,) * 10
    if not math.isclose(sum(weights), 100.0, abs_tol=0.01):
        return (None,) * 10
    expected = number(expected_total)
    if expected is None or not math.isfinite(expected) or not 0 <= expected <= 100:
        return (None,) * 10
    calculated_total = round(sum(value * weight / 100 for value, weight in zip(scores, weights)), 1)
    if abs(calculated_total - expected) > 0.11:
        return (None,) * 10
    return (*scores, *weights)


def _table_exists(conn, table: str) -> bool:
    return conn.execute("select to_regclass(%s) is not null", (f"public.{table}",)).fetchone()[0]


def _purge_legacy_hard_rejects(conn, external_ids: Iterable[str] = ()) -> int:
    external_ids = list(external_ids)
    ids = [
        row[0]
        for row in conn.execute(
            """select distinct l."Id" from listings l
            left join listing_export_state s on s.listing_id=l."Id"
            where l."State"::text='filter_rejected'
               or s.pipeline_decision='filter_rejected'
               or l."ExternalId" = any(%s)""",
            (external_ids,),
        ).fetchall()
    ]
    if not ids:
        return 0
    conn.execute('select 1 from listings where "Id" = any(%s) order by "Id" for update', (ids,)).fetchall()
    for table in ("votes", "comments", "feedback", "listing_overrides"):
        if _table_exists(conn, table) and conn.execute(
            sql.SQL('select exists(select 1 from {} where "ListingId" = any(%s))').format(sql.Identifier(table)),
            (ids,),
        ).fetchone()[0]:
            raise RuntimeError(
                f"Cannot purge hard-filter rejects: user history exists in {table}."
            )
    for table in ("listing_media", "ai_evidence", "listing_imports"):
        if _table_exists(conn, table):
            conn.execute(sql.SQL("delete from {} where listing_id = any(%s)").format(sql.Identifier(table)), (ids,))
    if _table_exists(conn, "listing_export_state"):
        conn.execute("delete from listing_export_state where listing_id = any(%s)", (ids,))
    conn.execute('delete from listings where "Id" = any(%s)', (ids,))
    return len(ids)


def _active_learning_rule(conn) -> tuple[uuid.UUID, str, dict] | None:
    if not _table_exists(conn, "ai_rule_proposals"):
        return None
    row = conn.execute(
        'select "Id","Version","RuleJson" from ai_rule_proposals where "IsActive"=true order by "Version" desc limit 1'
    ).fetchone()
    if not row:
        return None
    try:
        rule = json.loads(row[2])
    except (TypeError, ValueError, json.JSONDecodeError):
        return None
    if not isinstance(rule, dict) or not isinstance(rule.get("conditions"), list) or not rule["conditions"]:
        return None
    return row[0], f"feedback-v{row[1]}", rule


def _learning_field(case: ExportCase, field: str):
    raw = case.raw
    return {
        "condition": raw.get("vision_condition"),
        "multigenfit": raw.get("vision_multigen_layout"),
        "multigen_fit": raw.get("vision_multigen_layout"),
        "buildablestatus": raw.get("buildable_status"),
        "buildable_status": raw.get("buildable_status"),
        "gardenorientation": raw.get("vision_garden_orientation"),
        "garden_orientation": raw.get("vision_garden_orientation"),
        "energylabel": _first(raw, "energy_label", "energyLabel"),
        "energy_label": _first(raw, "energy_label", "energyLabel"),
        "privacyscore": _integer(raw, "vision_privacy_score"),
        "privacy_score": _integer(raw, "vision_privacy_score"),
        "familyscore": case.family_score,
        "family_score": case.family_score,
        "separateentrance": _boolean(raw, "vision_separate_entrance"),
        "separate_entrance": _boolean(raw, "vision_separate_entrance"),
        "secondkitchen": _boolean(raw, "vision_second_kitchen"),
        "second_kitchen": _boolean(raw, "vision_second_kitchen"),
        "groundfloorbedroom": _boolean(raw, "vision_ground_floor_bedroom"),
        "ground_floor_bedroom": _boolean(raw, "vision_ground_floor_bedroom"),
    }.get(field.lower())


def _learning_condition(case: ExportCase, condition: dict) -> bool:
    actual = _learning_field(case, str(condition.get("field", "")))
    expected = condition.get("value")
    operator = str(condition.get("operator", "")).lower()
    if actual is None or isinstance(actual, bool) != isinstance(expected, bool) and (isinstance(actual, bool) or isinstance(expected, bool)):
        return False
    if isinstance(actual, str) and isinstance(expected, str):
        left, right = actual.casefold(), expected.casefold()
        return {"eq": left == right, "neq": left != right, "contains": right in left}.get(operator, False)
    if isinstance(actual, (int, float)) and not isinstance(actual, bool) and isinstance(expected, (int, float)) and not isinstance(expected, bool):
        return {"eq": actual == expected, "neq": actual != expected, "lt": actual < expected, "lte": actual <= expected, "gt": actual > expected, "gte": actual >= expected}.get(operator, False)
    if isinstance(actual, bool) and isinstance(expected, bool):
        return {"eq": actual == expected, "neq": actual != expected}.get(operator, False)
    return False


def _matches_learning_rule(case: ExportCase, rule: dict) -> bool:
    conditions = rule.get("conditions") or []
    results = [_learning_condition(case, condition) for condition in conditions if isinstance(condition, dict)]
    if not results:
        return False
    return any(results) if str(rule.get("combinator", "all")).lower() == "any" else all(results)


def _commute_minutes(data: dict) -> int | None:
    commute = data.get("commute")
    if not isinstance(commute, dict):
        return None
    minutes = []
    for destination in (commute.get("destinations") or {}).values():
        car = (destination or {}).get("car") or {}
        value = car.get("min") if isinstance(car, dict) else None
        if isinstance(value, (int, float)) and not isinstance(value, bool):
            minutes.append(int(round(value)))
    return min(minutes) if minutes else None


def _card_facts(case: ExportCase) -> tuple:
    raw = case.raw
    energy = _first(raw, "energy_label", "energyLabel")
    noise = _first(raw, "noise_status")
    return (
        _first(raw, "preview_image"),
        _integer(raw, "housing_area_m2", "housingArea"),
        _integer(raw, "garden_size_m2", "lotArea"),
        _integer(raw, "rooms", "numberOfRooms"),
        _integer(raw, "year_built", "yearBuilt"),
        _integer(raw, "numberOfBathrooms", "vision_bathroom_count"),
        _integer(raw, "vision_bedroom_count"),
        _integer(raw, "number_of_floors", "numberOfFloors"),
        str(energy).upper() if energy else None,
        noise == "quiet" if noise is not None else None,
        _integer(raw, "buildable_headroom_m2"),
        _boolean(raw, "vision_ground_floor_bedroom"),
        _boolean(raw, "vision_separate_entrance"),
        _boolean(raw, "vision_second_kitchen"),
        _integer(raw, "vision_privacy_score"),
        *_score_breakdown(raw, case.family_score),
        case.latitude,
        case.longitude,
        _integer(raw, "monthly_expense", "monthlyExpense"),
        _integer(raw, "days_on_market", "daysOnMarket"),
        _commute_minutes(raw),
        _first(raw, "buildable_status"),
        _first(raw, "vision_condition"),
        _first(raw, "vision_garden_orientation"),
        _first(raw, "vision_multigen_layout"),
        case.postal_code,
        _boolean(raw, "preferred"),
        _boolean(raw, "new"),
        _first(raw, "family_units"),
    )


@dataclass(frozen=True, slots=True)
class ExportResult:
    exported: int
    archived: int
    media_cached: int
    media_errors: int


class PostgresExporter:
    def __init__(
        self,
        database_url: str,
        *,
        source_scope: str = "default",
        media_cache: MediaCache | None = None,
        ensure_schema_on_export: bool = False,
    ):
        self.database_url, self.source_scope, self.media_cache = (
            database_url,
            source_scope,
            media_cache,
        )
        self.ensure_schema_on_export = ensure_schema_on_export

    def export(
        self,
        cases: Iterable[ExportCase],
        *,
        run_id: str,
        fetched_at: datetime | None = None,
    ) -> ExportResult:
        fetched_at = fetched_at or datetime.now(timezone.utc)
        all_cases = list(cases)
        hard_rejected_ids = [case.source_id for case in all_cases if case.pipeline_decision == "filter_rejected"]
        cases = [case for case in all_cases if case.pipeline_decision != "filter_rejected"]
        media_cached = media_errors = 0
        with psycopg.connect(self.database_url) as conn:
            # Production exporter credentials should only need DML. Schema DDL
            # is an explicit deployment/test operation, not a per-run side effect.
            if self.ensure_schema_on_export:
                ensure_schema(conn)
            _purge_legacy_hard_rejects(conn, hard_rejected_ids)
            learning_rule = _active_learning_rule(conn)
            votes_table_exists = _table_exists(conn, "votes")
            conn.execute(
                "INSERT INTO export_runs(run_id,source_scope,fetched_at) VALUES (%s,%s,%s) ON CONFLICT(run_id) DO NOTHING",
                (run_id, self.source_scope, fetched_at),
            )
            for case in cases:
                _, payload_hash = _canonical(case.raw)
                evidence = case.ai_evidence or {}
                state = "ai_rejected" if case.pipeline_decision == "ai_rejected" else "active"
                baseline_state = state
                learning_version = None
                learning_applied = False
                existing = conn.execute('select "Id","State"::text,"LearningRuleVersion" from listings where "ExternalId"=%s for update', (case.source_id,)).fetchone()
                protected_existing = False
                if existing and not case.archive_reason:
                    has_vote = votes_table_exists and conn.execute(
                        'select exists(select 1 from votes where "ListingId"=%s)',
                        (existing[0],),
                    ).fetchone()[0]
                    has_override = _table_exists(conn, "listing_overrides") and conn.execute(
                        'select exists(select 1 from listing_overrides where "ListingId"=%s)',
                        (existing[0],),
                    ).fetchone()[0]
                    protected_existing = has_vote or has_override
                    if protected_existing:
                        state, learning_version = existing[1], existing[2]
                if not protected_existing and learning_rule and case.ai_status != "not_assessed" and case.ai_confidence == "high":
                    learning_applied = True
                    learning_version = learning_rule[1]
                    learned_reject = _matches_learning_rule(case, learning_rule[2])
                    state = "ai_rejected" if learned_reject else "active"
                    if learned_reject:
                        evidence = {
                            "decision": "reject",
                            "confidence": "high",
                            "model_version": evidence.get("model_version", "approved-feedback-rule"),
                            "rule_version": learning_version,
                            "evidence": {"approved_rule": learning_rule[2], "source_evidence": evidence.get("evidence", {})},
                        }
                if case.archive_reason:
                    state = "archived"
                    learning_version = None
                listing_id = conn.execute(
                    """INSERT INTO listings AS current
                    ("Id","ExternalId","Address","City","Price","FamilyFitScore","State","AiAssessed",
                     "AiConfidence","AiEvidence","ModelVersion","RuleVersion","SourceUrl","ImportedAt","ArchivedAt",
                     "PreviewImageUrl","LivingArea","LotArea","Rooms","YearBuilt","Bathrooms","Bedrooms","Floors",
                     "EnergyLabel","Quiet","BuildableHeadroom","GroundFloorBedroom","SeparateEntrance",
                     "SecondKitchen","PrivacyScore","FamilyPrivacyScore","KidsSpaceScore","GardenScore",
                     "SharedLivingScore","PracticalScore","FamilyPrivacyWeight","KidsSpaceWeight","GardenWeight",
                     "SharedLivingWeight","PracticalWeight","Latitude","Longitude","MonthlyExpense",
                     "DaysOnMarket","CommuteMinutes","BuildableStatus","Condition","GardenOrientation","MultigenFit","PostalCode","Preferred","IsNew","FamilyUnits","LearningRuleVersion")
                    VALUES (%s,%s,%s,%s,%s,%s,%s::listing_state,%s,%s,%s,%s,%s,%s,%s,%s,
                            %s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,
                            %s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s)
                    ON CONFLICT("ExternalId") DO UPDATE SET
                     "Address"=excluded."Address","City"=excluded."City","Price"=excluded."Price",
                     "FamilyFitScore"=excluded."FamilyFitScore",
                     "State"=CASE WHEN excluded."ArchivedAt" IS NOT NULL THEN 'archived'::listing_state
                       WHEN EXISTS (SELECT 1 FROM listing_overrides o WHERE o."ListingId"=current."Id")
                       THEN (SELECT CASE WHEN o."Action"::text='restore' THEN 'restored'::listing_state
                                         ELSE 'manually_rejected'::listing_state END
                             FROM listing_overrides o WHERE o."ListingId"=current."Id"
                             ORDER BY o."CreatedAt" DESC,o."Id" DESC LIMIT 1)
                       ELSE excluded."State" END,
                     "AiAssessed"=excluded."AiAssessed","AiConfidence"=excluded."AiConfidence",
                     "AiEvidence"=excluded."AiEvidence","ModelVersion"=excluded."ModelVersion",
                     "RuleVersion"=excluded."RuleVersion","SourceUrl"=excluded."SourceUrl",
                     "ImportedAt"=excluded."ImportedAt","ArchivedAt"=excluded."ArchivedAt",
                     "PreviewImageUrl"=excluded."PreviewImageUrl","LivingArea"=excluded."LivingArea",
                     "LotArea"=excluded."LotArea","Rooms"=excluded."Rooms","YearBuilt"=excluded."YearBuilt",
                     "Bathrooms"=excluded."Bathrooms","Bedrooms"=excluded."Bedrooms","Floors"=excluded."Floors",
                     "EnergyLabel"=excluded."EnergyLabel","Quiet"=excluded."Quiet",
                     "BuildableHeadroom"=excluded."BuildableHeadroom",
                     "GroundFloorBedroom"=excluded."GroundFloorBedroom",
                     "SeparateEntrance"=excluded."SeparateEntrance","SecondKitchen"=excluded."SecondKitchen",
                     "PrivacyScore"=excluded."PrivacyScore",
                     "FamilyPrivacyScore"=excluded."FamilyPrivacyScore","KidsSpaceScore"=excluded."KidsSpaceScore",
                     "GardenScore"=excluded."GardenScore","SharedLivingScore"=excluded."SharedLivingScore",
                     "PracticalScore"=excluded."PracticalScore",
                     "FamilyPrivacyWeight"=excluded."FamilyPrivacyWeight","KidsSpaceWeight"=excluded."KidsSpaceWeight",
                     "GardenWeight"=excluded."GardenWeight","SharedLivingWeight"=excluded."SharedLivingWeight",
                     "PracticalWeight"=excluded."PracticalWeight","Latitude"=excluded."Latitude",
                     "Longitude"=excluded."Longitude","MonthlyExpense"=excluded."MonthlyExpense",
                     "DaysOnMarket"=excluded."DaysOnMarket","CommuteMinutes"=excluded."CommuteMinutes",
                     "BuildableStatus"=excluded."BuildableStatus","Condition"=excluded."Condition",
                     "GardenOrientation"=excluded."GardenOrientation","MultigenFit"=excluded."MultigenFit",
                     "PostalCode"=excluded."PostalCode","Preferred"=excluded."Preferred","IsNew"=excluded."IsNew","FamilyUnits"=excluded."FamilyUnits",
                     "LearningRuleVersion"=CASE WHEN EXISTS (SELECT 1 FROM listing_overrides o WHERE o."ListingId"=current."Id") THEN current."LearningRuleVersion" ELSE excluded."LearningRuleVersion" END
                    RETURNING "Id"
                    """,
                    (
                        uuid.uuid4(),
                        case.source_id,
                        case.address or case.source_id,
                        case.municipality,
                        case.price_dkk,
                        case.family_score or 0,
                        state,
                        case.ai_status != "not_assessed",
                        _confidence(case.ai_confidence),
                        json.dumps(evidence.get("evidence"), ensure_ascii=False)
                        if evidence
                        else None,
                        evidence.get("model_version"),
                        evidence.get("rule_version"),
                        case.source_url,
                        fetched_at,
                        fetched_at if case.archive_reason else None,
                        *_card_facts(case),
                        learning_version,
                    ),
                ).fetchone()[0]
                conn.execute(
                    """INSERT INTO listing_export_state
                    (listing_id,source_scope,first_seen_at,last_seen_at,last_seen_run_id,non_ai_passed,pipeline_decision,archive_reason,raw_payload)
                    VALUES (%s,%s,%s,%s,%s,%s,%s,%s,%s)
                    ON CONFLICT(listing_id) DO UPDATE SET source_scope=excluded.source_scope,last_seen_at=excluded.last_seen_at,
                    last_seen_run_id=excluded.last_seen_run_id,non_ai_passed=excluded.non_ai_passed,
                    pipeline_decision=excluded.pipeline_decision,archive_reason=excluded.archive_reason,raw_payload=excluded.raw_payload""",
                    (
                        listing_id,
                        self.source_scope,
                        fetched_at,
                        fetched_at,
                        run_id,
                        case.non_ai_passed,
                        case.pipeline_decision,
                        case.archive_reason,
                        Jsonb(case.raw),
                    ),
                )
                if learning_applied and learning_rule and _table_exists(conn, "ai_rule_applications"):
                    previous_state = existing[1] if existing else baseline_state
                    previous_version = existing[2] if existing else None
                    conn.execute(
                        """INSERT INTO ai_rule_applications
                        ("ProposalId","ListingId","PreviousState","PreviousLearningRuleVersion","AppliedState","AppliedAt")
                        VALUES (%s,%s,%s::listing_state,%s,%s::listing_state,%s)
                        ON CONFLICT ("ProposalId","ListingId") DO NOTHING""",
                        (learning_rule[0], listing_id, previous_state, previous_version, state, fetched_at),
                    )
                conn.execute(
                    """INSERT INTO listing_imports
                    (listing_id,run_id,imported_at,payload_sha256,raw_payload,non_ai_passed,pipeline_decision)
                    VALUES (%s,%s,%s,%s,%s,%s,%s) ON CONFLICT(listing_id,run_id) DO NOTHING""",
                    (
                        listing_id,
                        run_id,
                        fetched_at,
                        payload_hash,
                        Jsonb(case.raw),
                        case.non_ai_passed,
                        case.pipeline_decision,
                    ),
                )
                if evidence:
                    _, evidence_hash = _canonical(evidence)
                    conn.execute(
                        """INSERT INTO ai_evidence
                        (listing_id,run_id,decision,confidence,model_version,rule_version,evidence,evidence_sha256,created_at)
                        VALUES (%s,%s,%s,%s,%s,%s,%s,%s,%s) ON CONFLICT(listing_id,run_id) DO NOTHING""",
                        (
                            listing_id,
                            run_id,
                            evidence["decision"],
                            evidence.get("confidence"),
                            evidence["model_version"],
                            evidence["rule_version"],
                            Jsonb(evidence.get("evidence") or {}),
                            evidence_hash,
                            fetched_at,
                        ),
                    )
                if self.media_cache:
                    for kind, url in discover_media(case.raw):
                        try:
                            media = self.media_cache.cache(kind, url)
                            conn.execute(
                                """INSERT INTO listing_media
                                (listing_id,kind,source_url,local_path,content_type,content_sha256,byte_size,cached_at)
                                VALUES (%s,%s,%s,%s,%s,%s,%s,%s) ON CONFLICT(listing_id,kind,source_url) DO UPDATE SET
                                local_path=excluded.local_path,content_type=excluded.content_type,
                                content_sha256=excluded.content_sha256,byte_size=excluded.byte_size,cached_at=excluded.cached_at""",
                                (
                                    listing_id,
                                    media.kind,
                                    media.source_url,
                                    media.local_path,
                                    media.content_type,
                                    media.sha256,
                                    media.byte_size,
                                    fetched_at,
                                ),
                            )
                            media_cached += 1
                        except Exception:
                            media_errors += 1
            archived = conn.execute(
                """UPDATE listings l SET "State"='archived',"ArchivedAt"=%s
                FROM listing_export_state s WHERE s.listing_id=l."Id" AND s.source_scope=%s
                AND s.last_seen_run_id<>%s AND l."ArchivedAt" IS NULL""",
                (fetched_at, self.source_scope, run_id),
            ).rowcount
            conn.execute(
                """UPDATE listing_export_state SET archive_reason='not_in_current_fetch'
                WHERE source_scope=%s AND last_seen_run_id<>%s AND archive_reason IS NULL""",
                (self.source_scope, run_id),
            )
            conn.execute(
                "UPDATE export_runs SET completed_at=%s WHERE run_id=%s",
                (datetime.now(timezone.utc), run_id),
            )
        return ExportResult(len(cases), archived, media_cached, media_errors)
