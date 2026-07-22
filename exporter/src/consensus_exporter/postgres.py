"""Transactional PostgreSQL export with immutable provenance and evidence."""

from __future__ import annotations
import hashlib
import json
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable
import psycopg
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

    def score(key: str) -> float | None:
        value = breakdown.get(key)
        try:
            return float(value) if value is not None else None
        except (TypeError, ValueError):
            return None

    scores = tuple(score(key) for key in ("privacy", "kids_space", "garden", "shared_living", "practical"))
    if any(value is None for value in scores):
        return (None,) * 10

    weight_data = breakdown.get("weights")
    if isinstance(weight_data, dict):
        try:
            weights = tuple(float(weight_data[key]) for key in ("privacy", "kids_space", "garden", "shared_living", "practical"))
        except (KeyError, TypeError, ValueError):
            weights = ()
    else:
        weights = ()
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
    calculated_total = round(sum(value * weight / 100 for value, weight in zip(scores, weights)), 1)
    if expected_total is None or abs(calculated_total - float(expected_total)) > 0.11:
        return (None,) * 10
    return (*scores, *weights)


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
        fetched_at, cases = fetched_at or datetime.now(timezone.utc), list(cases)
        media_cached = media_errors = 0
        with psycopg.connect(self.database_url) as conn:
            # Production exporter credentials should only need DML. Schema DDL
            # is an explicit deployment/test operation, not a per-run side effect.
            if self.ensure_schema_on_export:
                ensure_schema(conn)
            conn.execute(
                "INSERT INTO export_runs(run_id,source_scope,fetched_at) VALUES (%s,%s,%s) ON CONFLICT(run_id) DO NOTHING",
                (run_id, self.source_scope, fetched_at),
            )
            for case in cases:
                _, payload_hash = _canonical(case.raw)
                evidence = case.ai_evidence or {}
                state = (
                    "ai_rejected"
                    if case.pipeline_decision == "ai_rejected"
                    else "active"
                )
                if case.pipeline_decision == "filter_rejected":
                    state = "filter_rejected"
                if case.archive_reason:
                    state = "archived"
                listing_id = conn.execute(
                    """INSERT INTO listings AS current
                    ("Id","ExternalId","Address","City","Price","FamilyFitScore","State","AiAssessed",
                     "AiConfidence","AiEvidence","ModelVersion","RuleVersion","SourceUrl","ImportedAt","ArchivedAt",
                     "PreviewImageUrl","LivingArea","LotArea","Rooms","YearBuilt","Bathrooms","Bedrooms","Floors",
                     "EnergyLabel","Quiet","BuildableHeadroom","GroundFloorBedroom","SeparateEntrance",
                     "SecondKitchen","PrivacyScore","FamilyPrivacyScore","KidsSpaceScore","GardenScore",
                     "SharedLivingScore","PracticalScore","FamilyPrivacyWeight","KidsSpaceWeight","GardenWeight",
                     "SharedLivingWeight","PracticalWeight")
                    VALUES (%s,%s,%s,%s,%s,%s,%s::listing_state,%s,%s,%s,%s,%s,%s,%s,%s,
                            %s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s)
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
                     "PracticalWeight"=excluded."PracticalWeight"
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
