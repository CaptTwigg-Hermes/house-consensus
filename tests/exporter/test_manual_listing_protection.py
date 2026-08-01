from pathlib import Path


def test_exporter_schema_and_lifecycle_preserve_manual_listings():
    schema = Path("exporter/src/consensus_exporter/schema.sql").read_text()
    postgres = Path("exporter/src/consensus_exporter/postgres.py").read_text()
    for column in ("CanonicalUrl", "NormalizedAddress", "IsManuallyAdded", "ManuallyAddedById", "ManuallyAddedAt", "ManualLifecycleProtected"):
        assert column in schema
    assert '"ManualLifecycleProtected"=false' in postgres
    assert '"NormalizedAddress"=%s' in postgres
    assert "LOCK TABLE listings IN SHARE ROW EXCLUSIVE MODE" in postgres
    assert 'case.source_url' in postgres
    assert 'effective_archive_reason' in postgres


def test_exporter_canonical_url_matches_manual_listing_normalization():
    from consensus_exporter.postgres import _canonical_listing_url
    assert _canonical_listing_url("https://Example.dk:443/home/?utm_source=x#photos") == "https://example.dk/home"
    assert _canonical_listing_url("https://example.dk/home?id=42&utm_medium=x") == "https://example.dk/home?id=42"
