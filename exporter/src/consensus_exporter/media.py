"""Bounded, content-addressed local cache for listing media."""

from __future__ import annotations
import hashlib
import json
import mimetypes
import urllib.request
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Callable


@dataclass(frozen=True, slots=True)
class CachedMedia:
    kind: str
    source_url: str
    local_path: str
    content_type: str | None
    sha256: str
    byte_size: int


def _url(value):
    if isinstance(value, str):
        return value
    if isinstance(value, dict):
        return value.get("url") or value.get("src")
    return None


def discover_media(record: dict) -> list[tuple[str, str]]:
    found = []
    thumb = _url(
        record.get("preview_image") or record.get("thumbnail") or record.get("imageUrl")
    )
    if thumb:
        found.append(("thumbnail", thumb))
    for plan in record.get("floorPlanImages") or []:
        sources = plan.get("imageSources") if isinstance(plan, dict) else None
        if sources:
            best = max(sources, key=lambda source: source.get("width") or 0)
            url = _url(best)
        else:
            url = _url(plan)
        if url:
            found.append(("floorplan", url))
    direct = _url(record.get("caseUrlFloorPlan") or record.get("floor_plan_recovered"))
    if direct:
        found.append(("floorplan", direct))
    # Preserve order while avoiding duplicate source URLs.
    return list(dict.fromkeys(found))


def _fetch(url: str) -> tuple[bytes, str | None]:
    request = urllib.request.Request(
        url, headers={"User-Agent": "house-consensus-exporter/1"}
    )
    with urllib.request.urlopen(request, timeout=20) as response:
        return response.read(25 * 1024 * 1024 + 1), response.headers.get_content_type()


class MediaCache:
    def __init__(
        self,
        root: str | Path,
        fetcher: Callable = _fetch,
        max_bytes: int = 25 * 1024 * 1024,
    ):
        self.root = Path(root)
        self.root.mkdir(parents=True, exist_ok=True)
        self.fetcher, self.max_bytes = fetcher, max_bytes
        self.index_path = self.root / ".media-index.json"
        try:
            self.index = json.loads(self.index_path.read_text())
        except (FileNotFoundError, json.JSONDecodeError):
            self.index = {}

    def cache(self, kind: str, url: str) -> CachedMedia:
        existing = self.index.get(url)
        if existing and (self.root / existing["local_path"]).is_file():
            return CachedMedia(**existing)
        data, content_type = self.fetcher(url)
        if len(data) > self.max_bytes:
            raise ValueError(f"media exceeds {self.max_bytes} bytes")
        digest = hashlib.sha256(data).hexdigest()
        suffix = (
            mimetypes.guess_extension(content_type or "")
            or Path(url.split("?", 1)[0]).suffix
            or ".bin"
        )
        relative = str(Path(kind) / f"{digest}{suffix}")
        target = self.root / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        if not target.exists():
            target.write_bytes(data)
        result = CachedMedia(kind, url, relative, content_type, digest, len(data))
        self.index[url] = asdict(result)
        temporary = self.index_path.with_suffix(".tmp")
        temporary.write_text(json.dumps(self.index, sort_keys=True))
        temporary.replace(self.index_path)
        return result
