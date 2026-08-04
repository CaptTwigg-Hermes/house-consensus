"""Transactional PostgreSQL export with immutable provenance and evidence."""

from __future__ import annotations

import hashlib
import json
import math
import re
import socket
import uuid
from collections.abc import Iterable
from dataclasses import asdict, dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path
from urllib.parse import parse_qsl, quote, urlencode, urlsplit, urlunsplit
import ipaddress

import psycopg
from psycopg import sql
from psycopg.types.json import Jsonb

from .media import MediaCache, discover_media
from .models import ExportCase

_SCHEMA = Path(__file__).with_name("schema.sql")


_PERCENT_ESCAPE = re.compile(r"%([0-9A-Fa-f]{2})")
_UNRESERVED = frozenset("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~")


def _canonical_url_path(path: str) -> str:
    def normalize_escape(match: re.Match[str]) -> str:
        value = int(match.group(1), 16)
        character = chr(value)
        if character in _UNRESERVED:
            return character
        return f"%{value:02X}" if value >= 128 else match.group(0)

    escaped = _PERCENT_ESCAPE.sub(normalize_escape, path)
    escaped = re.sub(r"%(?![0-9A-Fa-f]{2})", "%25", escaped)
    escaped = quote(escaped, safe="/%:@-._~!$&'()*+,;=")
    segments: list[str] = []
    for segment in escaped.split("/"):
        if segment == ".":
            continue
        if segment == "..":
            if len(segments) > 1:
                segments.pop()
            continue
        segments.append(segment)
    return "/".join(segments)


def _canonical_listing_url(value: str | None) -> str | None:
    if not value:
        return None
    parsed = urlsplit(value.strip())
    if parsed.scheme.lower() != "https" or not parsed.hostname or parsed.username is not None or parsed.password is not None:
        return None
    try:
        address = ipaddress.ip_address(parsed.hostname)
        host = f"[{address.compressed}]" if address.version == 6 else address.compressed
        port = parsed.port
    except ValueError:
        try:
            numeric_host = parsed.hostname.lower()
            if re.fullmatch(r"(?:0x[0-9a-f]+|[0-9]+)(?:\.(?:0x[0-9a-f]+|[0-9]+)){0,3}", numeric_host):
                try:
                    host = socket.inet_ntoa(socket.inet_aton(numeric_host))
                except OSError:
                    host = parsed.hostname.encode("idna").decode("ascii").lower()
            else:
                host = parsed.hostname.encode("idna").decode("ascii").lower()
            port = parsed.port
        except (UnicodeError, ValueError):
            return None
    netloc = host if port in (None, 443) else f"{host}:{port}"
    path = _canonical_url_path(parsed.path or "/")
    if path != "/":
        path = path.rstrip("/")
    tracking = {"utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "gclid", "fbclid"}
    query_pairs: list[tuple[str, str]] = []
    query_key_casing: dict[str, str] = {}
    for key, query_value in parse_qsl(parsed.query, keep_blank_values=True):
        lowered_key = key.lower()
        if lowered_key in tracking:
            continue
        query_pairs.append((query_key_casing.setdefault(lowered_key, key), query_value))
    query = urlencode(query_pairs)
    query = _PERCENT_ESCAPE.sub(lambda match: match.group(0).lower() if int(match.group(1), 16) < 128 else match.group(0), query)
    canonical = urlunsplit(("https", netloc, path, query, ""))
    return canonical if len(canonical) <= 2048 else None


def _normalize_listing_address(value: str) -> str | None:
    normalized = " ".join(value.strip().lower().split()).replace(" ,", ",")
    return normalized if len(normalized) <= 500 else None


def ensure_schema(conn) -> None:
    conn.execute(_SCHEMA.read_text())


def tombstone_listing(
    database_url: str,
    *,
    external_id: str,
    source_url: str | None = None,
    verification_method: str = "http_404",
    verified_at: datetime | None = None,
) -> None:
    external_id = external_id.strip()
    if not external_id:
        raise ValueError("external_id is required")
    verified_at = verified_at or datetime.now(timezone.utc)
    with psycopg.connect(database_url) as conn:
        conn.execute(
            "select pg_advisory_xact_lock(hashtextextended(%s, 0))",
            (external_id,),
        )
        conn.execute(
            """insert into delisted_listings
            (external_id,source_url,verified_at,verification_method)
            values (%s,%s,%s,%s)
            on conflict (external_id) do update set
            source_url=excluded.source_url,
            verified_at=excluded.verified_at,
            verification_method=excluded.verification_method""",
            (external_id, source_url, verified_at, verification_method),
        )
        conn.execute(
            """update listings set "State"='archived'::listing_state,
            "ArchivedAt"=coalesce("ArchivedAt",%s)
            where "ExternalId"=%s""",
            (verified_at, external_id),
        )


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


def _privacy_rating(data: dict) -> int | None:
    value = _first(data, "vision_privacy_score")
    if isinstance(value, bool) or not isinstance(value, int):
        return None
    return value if 1 <= value <= 5 else None


def _number(data: dict, *keys: str) -> float | None:
    value = _first(data, *keys)
    if isinstance(value, bool):
        return None
    try:
        number = float(value) if value is not None else None
        return number if number is None or math.isfinite(number) else None
    except (TypeError, ValueError, OverflowError):
        return None


def _boolean(data: dict, *keys: str) -> bool | None:
    value = _first(data, *keys)
    if isinstance(value, bool):
        return value
    if isinstance(value, str) and value.lower() in {"true", "false"}:
        return value.lower() == "true"
    return None


def _score_breakdown(
    data: dict, expected_total: float | None
) -> tuple[float | str | bool | None, ...]:
    """Validate and preserve the producer-owned score contract."""
    invalid = (None,) * 14
    breakdown = data.get("family_score_breakdown")
    if not isinstance(breakdown, dict):
        return invalid

    keys = ("privacy", "kids_space", "garden", "shared_living", "practical")

    def number(value) -> float | None:
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            return None
        try:
            parsed = float(value)
        except (TypeError, ValueError, OverflowError):
            return None
        return parsed if math.isfinite(parsed) else None

    privacy_available = breakdown.get("privacy_available")
    if not isinstance(privacy_available, bool):
        return invalid

    scores = tuple(number(breakdown.get(key)) for key in keys)
    privacy, *required_scores = scores
    if privacy_available:
        if privacy is None or not 0 <= privacy <= 100:
            return invalid
    elif breakdown.get("privacy") is not None:
        return invalid
    if any(value is None or not 0 <= value <= 100 for value in required_scores):
        return invalid

    weight_data = breakdown.get("weights")
    if not isinstance(weight_data, dict):
        return invalid
    try:
        weights = tuple(number(weight_data[key]) for key in keys)
    except KeyError:
        return invalid
    if any(weight is None or weight < 0 for weight in weights):
        return invalid
    if not math.isclose(sum(weights), 100.0, abs_tol=0.01):
        return invalid

    version = breakdown.get("score_version")
    if not isinstance(version, str) or not version.strip() or len(version) > 100:
        return invalid
    version = version.strip()

    coverage = number(breakdown.get("score_coverage_pct"))
    if coverage is None or not 0 <= coverage <= 100:
        return invalid
    expected_coverage = sum(
        weight for score, weight in zip(scores, weights) if score is not None
    )
    if not math.isclose(coverage, expected_coverage, abs_tol=0.01):
        return invalid

    notes = breakdown.get("notes")
    if not isinstance(notes, dict):
        return invalid
    normalized_notes: dict[str, list[str]] = {}
    for key in keys:
        values = notes.get(key)
        if not isinstance(values, list) or len(values) > 50:
            return invalid
        if any(not isinstance(value, str) or len(value) > 500 for value in values):
            return invalid
        normalized_notes[key] = values
    notes_json = json.dumps(
        normalized_notes, ensure_ascii=False, sort_keys=True, separators=(",", ":")
    )

    expected = number(expected_total)
    if expected is None or not 0 <= expected <= 100:
        return invalid
    calculated_total = round(
        sum((value or 0.0) * weight / 100 for value, weight in zip(scores, weights)),
        1,
    )
    if abs(calculated_total - expected) > 0.11:
        return invalid
    return (*scores, *weights, version, coverage, privacy_available, notes_json)


def _table_exists(conn, table: str) -> bool:
    return conn.execute(
        "select to_regclass(%s) is not null", (f"public.{table}",)
    ).fetchone()[0]


def _purge_legacy_hard_rejects(conn, external_ids: Iterable[str] = ()) -> int:
    """Hide audited hard rejects; delete only records without human or audit history."""
    external_ids = list(external_ids)
    ids = {
        row[0]
        for row in conn.execute(
            """select distinct l."Id" from listings l
            left join listing_export_state s on s.listing_id=l."Id"
            where l."State"::text='filter_rejected'
               or s.pipeline_decision='filter_rejected'
               or l."ExternalId" = any(%s)""",
            (external_ids,),
        ).fetchall()
    }
    if not ids:
        return 0
    conn.execute(
        'select 1 from listings where "Id" = any(%s) order by "Id" for update',
        (list(ids),),
    ).fetchall()
    protected: set[uuid.UUID] = set()
    history_tables = (
        ("votes", '"ListingId"'),
        ("comments", '"ListingId"'),
        ("feedback", '"ListingId"'),
        ("listing_overrides", '"ListingId"'),
        ("listing_imports", "listing_id"),
        ("ai_evidence", "listing_id"),
        ("listing_export_state", "listing_id"),
        ("ai_rule_applications", '"ListingId"'),
    )
    for table, column in history_tables:
        if _table_exists(conn, table):
            query = sql.SQL("select distinct {} from {} where {} = any(%s)").format(
                sql.SQL(column), sql.Identifier(table), sql.SQL(column)
            )
            protected.update(
                row[0] for row in conn.execute(query, (list(ids),)).fetchall()
            )
    if protected:
        conn.execute(
            """update listings set "State"='filter_rejected'::listing_state
            where "Id"=any(%s) and "ArchivedAt" is null""",
            (list(protected),),
        )
    deletable = list(ids - protected)
    if not deletable:
        return 0
    if _table_exists(conn, "listing_media"):
        conn.execute(
            "delete from listing_media where listing_id = any(%s)", (deletable,)
        )
    conn.execute('delete from listings where "Id" = any(%s)', (deletable,))
    return len(deletable)


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
    if (
        not isinstance(rule, dict)
        or not isinstance(rule.get("conditions"), list)
        or not rule["conditions"]
    ):
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
    if (
        actual is None
        or isinstance(actual, bool) != isinstance(expected, bool)
        and (isinstance(actual, bool) or isinstance(expected, bool))
    ):
        return False
    if isinstance(actual, str) and isinstance(expected, str):
        left, right = actual.casefold(), expected.casefold()
        return {
            "eq": left == right,
            "neq": left != right,
            "contains": right in left,
        }.get(operator, False)
    if (
        isinstance(actual, (int, float))
        and not isinstance(actual, bool)
        and isinstance(expected, (int, float))
        and not isinstance(expected, bool)
    ):
        return {
            "eq": actual == expected,
            "neq": actual != expected,
            "lt": actual < expected,
            "lte": actual <= expected,
            "gt": actual > expected,
            "gte": actual >= expected,
        }.get(operator, False)
    if isinstance(actual, bool) and isinstance(expected, bool):
        return {"eq": actual == expected, "neq": actual != expected}.get(
            operator, False
        )
    return False


def _matches_learning_rule(case: ExportCase, rule: dict) -> bool:
    conditions = rule.get("conditions") or []
    results = [
        _learning_condition(case, condition)
        for condition in conditions
        if isinstance(condition, dict)
    ]
    if not results:
        return False
    return (
        any(results)
        if str(rule.get("combinator", "all")).lower() == "any"
        else all(results)
    )


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


def _source_first_seen(case: ExportCase, fetched_at: datetime) -> tuple[datetime, bool]:
    value = case.raw.get("_source_first_seen_at")
    first_seen = fetched_at
    if isinstance(value, str):
        try:
            first_seen = datetime.fromisoformat(value.replace("Z", "+00:00"))
            if first_seen.tzinfo is None:
                first_seen = first_seen.replace(tzinfo=timezone.utc)
        except ValueError:
            first_seen = fetched_at
    age = fetched_at - first_seen
    return first_seen, timedelta(0) <= age < timedelta(hours=120)


_NOISE_STATUSES = frozenset({"covered", "no_contour", "unavailable", "stale", "error"})


@dataclass(frozen=True, slots=True)
class NoiseFacts:
    quiet: bool | None
    road_lden_db: float | None
    road_lden_status: str
    road_lnight_db: float | None
    road_lnight_status: str
    rail_lden_db: float | None
    rail_lden_status: str
    rail_lnight_db: float | None
    rail_lnight_status: str
    air_lden_db: float | None
    air_lden_status: str
    air_lnight_db: float | None
    air_lnight_status: str


def _noise_observation(source: object, indicator: str) -> tuple[float | None, str]:
    if not isinstance(source, dict):
        return None, "unavailable"
    observation = source.get(indicator)
    if not isinstance(observation, dict):
        return None, "unavailable"
    status = str(observation.get("status") or "").strip().lower()
    if status not in _NOISE_STATUSES:
        status = "unavailable"
    value = _number(observation, "db_value")
    if status not in {"covered", "stale"}:
        value = None
    elif value is None:
        status = "unavailable"
    return value, status


def _noise_facts(raw: dict) -> NoiseFacts:
    sources = raw.get("noise_sources")
    if isinstance(sources, dict):
        road_lden_db, road_lden_status = _noise_observation(sources.get("ROAD"), "Lden")
        road_lnight_db, road_lnight_status = _noise_observation(sources.get("ROAD"), "Lnight")
        rail_lden_db, rail_lden_status = _noise_observation(sources.get("RAIL"), "Lden")
        rail_lnight_db, rail_lnight_status = _noise_observation(sources.get("RAIL"), "Lnight")
        air_lden_db, air_lden_status = _noise_observation(sources.get("AIR"), "Lden")
        air_lnight_db, air_lnight_status = _noise_observation(sources.get("AIR"), "Lnight")
        quiet = (
            road_lden_db < 50
            if road_lden_status == "covered" and road_lden_db is not None
            else None
        )
        return NoiseFacts(
            quiet,
            road_lden_db, road_lden_status, road_lnight_db, road_lnight_status,
            rail_lden_db, rail_lden_status, rail_lnight_db, rail_lnight_status,
            air_lden_db, air_lden_status, air_lnight_db, air_lnight_status,
        )

    def legacy(*keys: str) -> tuple[float | None, str]:
        value = _number(raw, *keys)
        return value, "covered" if value is not None else "unavailable"

    road_lden_db, road_lden_status = legacy("road_noise_db", "noise_road_db")
    rail_lden_db, rail_lden_status = legacy("rail_noise_db", "noise_rail_db")
    air_lden_db, air_lden_status = legacy("air_noise_db", "noise_air_db")
    quiet = road_lden_db < 50 if road_lden_status == "covered" else None
    return NoiseFacts(
        quiet,
        road_lden_db, road_lden_status, None, "unavailable",
        rail_lden_db, rail_lden_status, None, "unavailable",
        air_lden_db, air_lden_status, None, "unavailable",
    )


def _card_facts(case: ExportCase, fetched_at: datetime) -> tuple:
    raw = case.raw
    first_seen, is_new = _source_first_seen(case, fetched_at)
    energy = _first(raw, "energy_label", "energyLabel")
    noise = _noise_facts(raw)
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
        noise.quiet,
        _integer(raw, "buildable_headroom_m2"),
        _boolean(raw, "vision_ground_floor_bedroom"),
        _boolean(raw, "vision_separate_entrance"),
        _boolean(raw, "vision_second_kitchen"),
        _privacy_rating(raw),
        *_score_breakdown(raw, case.family_score),
        case.latitude,
        case.longitude,
        _integer(raw, "monthly_expense", "monthlyExpense"),
        _integer(raw, "days_on_market", "daysOnMarket"),
        _commute_minutes(raw),
        json.dumps(
            raw.get("commute"),
            ensure_ascii=False,
            sort_keys=True,
            separators=(",", ":"),
        )
        if isinstance(raw.get("commute"), dict)
        else None,
        _first(raw, "buildable_status"),
        _first(raw, "vision_condition"),
        _first(raw, "vision_garden_orientation"),
        _first(raw, "vision_multigen_layout"),
        case.postal_code,
        _boolean(raw, "preferred"),
        is_new,
        first_seen,
        _first(raw, "family_units"),
        noise.road_lden_db,
        noise.rail_lden_db,
        noise.air_lden_db,
        noise.road_lden_status,
        noise.road_lnight_db,
        noise.road_lnight_status,
        noise.rail_lden_status,
        noise.rail_lnight_db,
        noise.rail_lnight_status,
        noise.air_lden_status,
        noise.air_lnight_db,
        noise.air_lnight_status,
    )


@dataclass(frozen=True, slots=True)
class ExportResult:
    exported: int
    archived: int
    media_cached: int
    media_errors: int
    archival_blocked: int = 0
    inserted: int = 0
    updated: int = 0
    reactivated: int = 0
    active_total: int = 0
    geometry_covered: int = 0


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
        source_config_sha256: str | None = None,
        dry_run: bool = False,
    ) -> ExportResult:
        fetched_at = fetched_at or datetime.now(timezone.utc)
        if source_config_sha256 is not None and re.fullmatch(r"[0-9a-f]{64}", source_config_sha256) is None:
            raise ValueError("source_config_sha256 must be a canonical lowercase SHA-256")
        if self.source_scope == "tofamiliehus" and source_config_sha256 is None:
            raise ValueError("tofamiliehus exports require source_config_sha256")
        all_cases = list(cases)
        source_ids = [case.source_id for case in all_cases]
        if any(not source_id for source_id in source_ids) or len(
            set(source_ids)
        ) != len(source_ids):
            raise RuntimeError("export snapshot contains blank or duplicate source IDs")
        manifest_payload = [
            asdict(case) for case in sorted(all_cases, key=lambda item: item.source_id)
        ]
        manifest_sha256 = hashlib.sha256(
            json.dumps(
                manifest_payload, sort_keys=True, separators=(",", ":"), default=str
            ).encode()
        ).hexdigest()
        snapshot_count = len(all_cases)
        hard_rejected_ids = [
            case.source_id
            for case in all_cases
            if case.pipeline_decision == "filter_rejected"
        ]
        cases = [
            case for case in all_cases if case.pipeline_decision != "filter_rejected"
        ]
        media_cached = media_errors = 0
        with psycopg.connect(self.database_url) as conn:
            # Production exporter credentials should only need DML. Schema DDL
            # is an explicit deployment/test operation, not a per-run side effect.
            if self.ensure_schema_on_export:
                ensure_schema(conn)
            conn.execute(
                "select pg_advisory_xact_lock(hashtextextended(%s, 1))", (run_id,)
            )
            existing_run = conn.execute(
                "select source_scope,fetched_at,snapshot_count,manifest_sha256,source_config_sha256 "
                "from export_runs where run_id=%s",
                (run_id,),
            ).fetchone()
            run_identity = (
                self.source_scope,
                fetched_at,
                snapshot_count,
                manifest_sha256,
                source_config_sha256,
            )
            if existing_run is not None and tuple(existing_run) != run_identity:
                raise RuntimeError(
                    f"run ID {run_id!r} already belongs to a different immutable snapshot"
                )
            if existing_run is None:
                conn.execute(
                    "INSERT INTO export_runs"
                    "(run_id,source_scope,fetched_at,snapshot_count,manifest_sha256,source_config_sha256) "
                    "VALUES (%s,%s,%s,%s,%s,%s)",
                    (run_id, *run_identity),
                )
            # Validate immutable run identity before touching listings. Then serialize
            # every listings mutation to avoid lock upgrades and keep importer/manual
            # deduplication checks atomic.
            conn.execute("LOCK TABLE listings IN SHARE ROW EXCLUSIVE MODE")
            _purge_legacy_hard_rejects(conn, hard_rejected_ids)
            learning_rule = _active_learning_rule(conn)
            votes_table_exists = _table_exists(conn, "votes")
            tombstone_table_exists = _table_exists(conn, "delisted_listings")
            if tombstone_table_exists:
                delisted_ids = {
                    row[0]
                    for row in conn.execute(
                        "select external_id from delisted_listings"
                    ).fetchall()
                }
                cases = [case for case in cases if case.source_id not in delisted_ids]
            exported = 0
            inserted = updated = reactivated = 0
            for case in cases:
                if tombstone_table_exists:
                    conn.execute(
                        "select pg_advisory_xact_lock(hashtextextended(%s, 0))",
                        (case.source_id,),
                    )
                    if conn.execute(
                        "select exists(select 1 from delisted_listings where external_id=%s)",
                        (case.source_id,),
                    ).fetchone()[0]:
                        continue
                _, payload_hash = _canonical(case.raw)
                evidence = case.ai_evidence or {}
                state = (
                    "ai_rejected"
                    if case.pipeline_decision == "ai_rejected"
                    else "active"
                )
                baseline_state = state
                learning_version = None
                learning_applied = False
                canonical_source_url = _canonical_listing_url(case.source_url)
                normalized_address = _normalize_listing_address(case.address or case.source_id)
                identity_matches = conn.execute(
                    'select "Id","State"::text,"LearningRuleVersion","ArchivedAt","ManualLifecycleProtected","ExternalId","SourceUrl" from listings where "ExternalId"=%s or "CanonicalUrl"=%s or "NormalizedAddress"=%s for update',
                    (case.source_id, canonical_source_url, normalized_address),
                ).fetchall()
                legacy_matches = conn.execute(
                    'select "Id","State"::text,"LearningRuleVersion","ArchivedAt","ManualLifecycleProtected","ExternalId","SourceUrl" from listings where "CanonicalUrl" is null and "SourceUrl" is not null for update'
                ).fetchall()
                matched_ids = {row[0] for row in identity_matches}
                for legacy in legacy_matches:
                    if canonical_source_url is not None and legacy[0] not in matched_ids and _canonical_listing_url(legacy[6]) == canonical_source_url:
                        identity_matches.append(legacy)
                        matched_ids.add(legacy[0])
                if len(identity_matches) > 1:
                    raise ValueError("Listing URL and address resolve to different existing listings.")
                existing = identity_matches[0] if identity_matches else None
                external_id = existing[5] if existing else case.source_id
                effective_archive_reason = None if existing and existing[4] else case.archive_reason
                if existing is None:
                    inserted += 1
                elif existing[3] is not None and not case.archive_reason:
                    reactivated += 1
                else:
                    updated += 1
                protected_existing = False
                if existing and not effective_archive_reason:
                    has_vote = (
                        votes_table_exists
                        and conn.execute(
                            'select exists(select 1 from votes where "ListingId"=%s)',
                            (existing[0],),
                        ).fetchone()[0]
                    )
                    has_override = (
                        _table_exists(conn, "listing_overrides")
                        and conn.execute(
                            'select exists(select 1 from listing_overrides where "ListingId"=%s)',
                            (existing[0],),
                        ).fetchone()[0]
                    )
                    protected_existing = bool(existing[4]) or has_vote or has_override
                    if protected_existing:
                        learning_version = existing[2]
                        ordinary_reappearance = (
                            existing[3] is not None
                            and existing[1] == "archived"
                            and not has_override
                        )
                        if existing[4] or not ordinary_reappearance:
                            state = existing[1]
                if (
                    not protected_existing
                    and learning_rule
                    and case.ai_status != "not_assessed"
                    and case.ai_confidence == "high"
                ):
                    learning_applied = True
                    learning_version = learning_rule[1]
                    learned_reject = _matches_learning_rule(case, learning_rule[2])
                    state = "ai_rejected" if learned_reject else "active"
                    if learned_reject:
                        evidence = {
                            "decision": "reject",
                            "confidence": "high",
                            "model_version": evidence.get(
                                "model_version", "approved-feedback-rule"
                            ),
                            "rule_version": learning_version,
                            "evidence": {
                                "approved_rule": learning_rule[2],
                                "source_evidence": evidence.get("evidence", {}),
                            },
                        }
                if effective_archive_reason:
                    state = "archived"
                    learning_version = None
                listing_id = conn.execute(
                    """INSERT INTO listings AS current
                    ("Id","ExternalId","Address","City","Price","FamilyFitScore","State","AiAssessed",
                     "AiConfidence","AiEvidence","ModelVersion","RuleVersion","SourceUrl","CanonicalUrl","NormalizedAddress","ImportedAt","ArchivedAt",
                     "PreviewImageUrl","LivingArea","LotArea","Rooms","YearBuilt","Bathrooms","Bedrooms","Floors",
                     "EnergyLabel","Quiet","BuildableHeadroom","GroundFloorBedroom","SeparateEntrance",
                     "SecondKitchen","PrivacyScore","FamilyPrivacyScore","KidsSpaceScore","GardenScore",
                     "SharedLivingScore","PracticalScore","FamilyPrivacyWeight","KidsSpaceWeight","GardenWeight",
                     "SharedLivingWeight","PracticalWeight","ScoreRuleVersion","ScoreCoveragePct",
                     "FamilyPrivacyAvailable","ScoreNotesJson","Latitude","Longitude","MonthlyExpense",
                     "DaysOnMarket","CommuteMinutes","CommuteJson","BuildableStatus","Condition","GardenOrientation","MultigenFit","PostalCode","Preferred","IsNew","FirstSeenAt","FamilyUnits","RoadNoiseDb","RailNoiseDb","AirNoiseDb","RoadNoiseStatus","RoadNoiseLnightDb","RoadNoiseLnightStatus","RailNoiseStatus","RailNoiseLnightDb","RailNoiseLnightStatus","AirNoiseStatus","AirNoiseLnightDb","AirNoiseLnightStatus","LearningRuleVersion")
                    VALUES (%s,%s,%s,%s,%s,%s,%s::listing_state,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,
                            %s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,
                            %s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s)
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
                     "CanonicalUrl"=excluded."CanonicalUrl","NormalizedAddress"=excluded."NormalizedAddress",
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
                     "PracticalWeight"=excluded."PracticalWeight",
                     "ScoreRuleVersion"=excluded."ScoreRuleVersion","ScoreCoveragePct"=excluded."ScoreCoveragePct",
                     "FamilyPrivacyAvailable"=excluded."FamilyPrivacyAvailable","ScoreNotesJson"=excluded."ScoreNotesJson",
                     "Latitude"=excluded."Latitude",
                     "Longitude"=excluded."Longitude","MonthlyExpense"=excluded."MonthlyExpense",
                     "DaysOnMarket"=excluded."DaysOnMarket","CommuteMinutes"=excluded."CommuteMinutes",
                     "CommuteJson"=excluded."CommuteJson","BuildableStatus"=excluded."BuildableStatus","Condition"=excluded."Condition",
                     "GardenOrientation"=excluded."GardenOrientation","MultigenFit"=excluded."MultigenFit",
                     "PostalCode"=excluded."PostalCode","Preferred"=excluded."Preferred",
                     "FirstSeenAt"=LEAST(COALESCE(current."FirstSeenAt",excluded."FirstSeenAt"),excluded."FirstSeenAt"),
                     "IsNew"=(LEAST(COALESCE(current."FirstSeenAt",excluded."FirstSeenAt"),excluded."FirstSeenAt") > excluded."ImportedAt" - interval '120 hours'),
                     "FamilyUnits"=excluded."FamilyUnits",
                     "RoadNoiseDb"=excluded."RoadNoiseDb","RailNoiseDb"=excluded."RailNoiseDb",
                     "AirNoiseDb"=excluded."AirNoiseDb","RoadNoiseStatus"=excluded."RoadNoiseStatus",
                     "RoadNoiseLnightDb"=excluded."RoadNoiseLnightDb","RoadNoiseLnightStatus"=excluded."RoadNoiseLnightStatus",
                     "RailNoiseStatus"=excluded."RailNoiseStatus","RailNoiseLnightDb"=excluded."RailNoiseLnightDb",
                     "RailNoiseLnightStatus"=excluded."RailNoiseLnightStatus","AirNoiseStatus"=excluded."AirNoiseStatus",
                     "AirNoiseLnightDb"=excluded."AirNoiseLnightDb","AirNoiseLnightStatus"=excluded."AirNoiseLnightStatus",
                     "LearningRuleVersion"=CASE WHEN EXISTS (SELECT 1 FROM listing_overrides o WHERE o."ListingId"=current."Id") THEN current."LearningRuleVersion" ELSE excluded."LearningRuleVersion" END
                    RETURNING "Id"
                    """,
                    (
                        uuid.uuid4(),
                        external_id,
                        case.address or case.source_id,
                        case.municipality,
                        case.price_dkk,
                        case.family_score,
                        state,
                        case.ai_status != "not_assessed",
                        _confidence(case.ai_confidence),
                        json.dumps(evidence.get("evidence"), ensure_ascii=False)
                        if evidence
                        else None,
                        evidence.get("model_version"),
                        evidence.get("rule_version"),
                        case.source_url,
                        canonical_source_url,
                        normalized_address,
                        fetched_at,
                        existing[3] if existing and existing[4] and existing[1] == "archived" else (fetched_at if effective_archive_reason else None),
                        *_card_facts(case, fetched_at),
                        learning_version,
                    ),
                ).fetchone()[0]
                if (
                    _table_exists(conn, "spatial_ref_sys")
                    and conn.execute(
                        "select exists(select 1 from information_schema.columns where table_name='listings' and column_name='Location')"
                    ).fetchone()[0]
                ):
                    conn.execute(
                        """UPDATE listings SET "Location"=CASE
                        WHEN "Latitude" between -90 and 90 AND "Longitude" between -180 and 180
                        THEN ST_SetSRID(ST_MakePoint("Longitude","Latitude"),4326)
                        ELSE NULL END WHERE "Id"=%s""",
                        (listing_id,),
                    )
                conn.execute(
                    """INSERT INTO listing_export_state
                    (listing_id,source_scope,first_seen_at,last_seen_at,last_seen_run_id,non_ai_passed,pipeline_decision,archive_reason,raw_payload)
                    VALUES (%s,%s,%s,%s,%s,%s,%s,%s,%s)
                    ON CONFLICT(listing_id) DO UPDATE SET source_scope=excluded.source_scope,last_seen_at=excluded.last_seen_at,
                    last_seen_run_id=excluded.last_seen_run_id,non_ai_passed=excluded.non_ai_passed,
                    pipeline_decision=excluded.pipeline_decision,archive_reason=excluded.archive_reason,
                    raw_payload=excluded.raw_payload,missing_complete_snapshots=0,last_missing_snapshot_date=NULL""",
                    (
                        listing_id,
                        self.source_scope,
                        fetched_at,
                        fetched_at,
                        run_id,
                        case.non_ai_passed,
                        case.pipeline_decision,
                        effective_archive_reason,
                        Jsonb(case.raw),
                    ),
                )
                if (
                    learning_applied
                    and learning_rule
                    and _table_exists(conn, "ai_rule_applications")
                ):
                    previous_state = existing[1] if existing else baseline_state
                    previous_version = existing[2] if existing else None
                    conn.execute(
                        """INSERT INTO ai_rule_applications
                        ("ProposalId","ListingId","ListingExternalId","PreviousState","PreviousLearningRuleVersion","AppliedState","AppliedAt")
                        VALUES (%s,%s,%s,%s::listing_state,%s,%s::listing_state,%s)
                        ON CONFLICT ("ProposalId","ListingId") DO NOTHING""",
                        (
                            learning_rule[0],
                            listing_id,
                            case.source_id,
                            previous_state,
                            previous_version,
                            state,
                            fetched_at,
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
                exported += 1
            conn.execute(
                """UPDATE listing_export_state SET
                missing_complete_snapshots=missing_complete_snapshots + CASE
                    WHEN extract(isodow from %s::timestamptz) between 1 and 5
                     AND last_missing_snapshot_date IS DISTINCT FROM (%s::timestamptz)::date THEN 1 ELSE 0 END,
                last_missing_snapshot_date=(%s::timestamptz)::date
                WHERE source_scope=%s AND last_seen_run_id<>%s""",
                (fetched_at, fetched_at, fetched_at, self.source_scope, run_id),
            )
            candidates = conn.execute(
                """SELECT count(*) FROM listings l JOIN listing_export_state s ON s.listing_id=l."Id"
                WHERE s.source_scope=%s AND s.last_seen_run_id<>%s
                AND s.missing_complete_snapshots>=2 AND l."ArchivedAt" IS NULL AND l."ManualLifecycleProtected"=false""",
                (self.source_scope, run_id),
            ).fetchone()[0]
            retained = conn.execute(
                """SELECT count(*) FROM listings l JOIN listing_export_state s ON s.listing_id=l."Id"
                WHERE s.source_scope=%s AND l."ArchivedAt" IS NULL""",
                (self.source_scope,),
            ).fetchone()[0]
            archival_blocked = (
                candidates if candidates and candidates / max(retained, 1) > 0.20 else 0
            )
            archived = 0
            if not archival_blocked:
                archived = conn.execute(
                    """UPDATE listings l SET "State"='archived',"ArchivedAt"=%s
                    FROM listing_export_state s WHERE s.listing_id=l."Id" AND s.source_scope=%s
                    AND s.last_seen_run_id<>%s AND s.missing_complete_snapshots>=2
                    AND l."ArchivedAt" IS NULL AND l."ManualLifecycleProtected"=false""",
                    (fetched_at, self.source_scope, run_id),
                ).rowcount
                conn.execute(
                    """UPDATE listing_export_state s SET archive_reason='missing_from_two_complete_snapshots'
                    WHERE source_scope=%s AND last_seen_run_id<>%s
                    AND missing_complete_snapshots>=2 AND archive_reason IS NULL
                    AND EXISTS (SELECT 1 FROM listings l WHERE l."Id"=s.listing_id AND l."ManualLifecycleProtected"=false)""",
                    (self.source_scope, run_id),
                )
            conn.execute(
                "UPDATE export_runs SET completed_at=%s WHERE run_id=%s",
                (datetime.now(timezone.utc), run_id),
            )
            active_total = conn.execute(
                'select count(*) from listings where "ArchivedAt" is null'
            ).fetchone()[0]
            geometry_covered = 0
            if (
                _table_exists(conn, "spatial_ref_sys")
                and conn.execute(
                    "select exists(select 1 from information_schema.columns where table_name='listings' and column_name='Location')"
                ).fetchone()[0]
            ):
                geometry_covered = conn.execute(
                    'select count(*) from listings where "ArchivedAt" is null and "Location" is not null'
                ).fetchone()[0]
            if dry_run:
                conn.rollback()
        return ExportResult(
            exported,
            archived,
            media_cached,
            media_errors,
            archival_blocked,
            inserted,
            updated,
            reactivated,
            active_total,
            geometry_covered,
        )
