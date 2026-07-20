from pathlib import Path
from consensus_exporter.media import MediaCache, discover_media


def test_discovers_thumbnail_and_all_floorplans():
    record = {
        "preview_image": "https://example/hero.webp",
        "floorPlanImages": [
            {
                "imageSources": [
                    {"url": "https://example/small.webp", "width": 200},
                    {"url": "https://example/large.webp", "width": 1200},
                ]
            }
        ],
        "caseUrlFloorPlan": "https://example/plan.pdf",
    }
    assert discover_media(record) == [
        ("thumbnail", "https://example/hero.webp"),
        ("floorplan", "https://example/large.webp"),
        ("floorplan", "https://example/plan.pdf"),
    ]


def test_cache_is_content_addressed_and_does_not_download_existing_url_twice(
    tmp_path: Path,
):
    calls = []

    def fetch(url: str):
        calls.append(url)
        return b"image bytes", "image/webp"

    cache = MediaCache(tmp_path, fetcher=fetch)
    first = cache.cache("thumbnail", "https://example/image.webp")
    second = cache.cache("thumbnail", "https://example/image.webp")
    assert first.local_path == second.local_path
    assert (tmp_path / first.local_path).read_bytes() == b"image bytes"
    assert calls == ["https://example/image.webp"]
