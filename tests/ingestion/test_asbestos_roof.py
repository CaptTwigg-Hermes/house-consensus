from house_consensus_ingestion.asbestos import assess_asbestos_roof


def test_image_locator_metadata_without_evaluated_content_is_unknown():
    result = assess_asbestos_roof({
        "images": [{
            "url": "https://example.test/asbestos-roof.jpg",
            "filename": "asbestos-roof.jpg",
            "contentType": "image/jpeg",
        }]
    })

    assert result.status == "unknown"
    assert result.primary_source is None


def test_image_locator_change_updates_source_fingerprint():
    first = assess_asbestos_roof({"images": [{"url": "https://example.test/roof-1.jpg"}]})
    second = assess_asbestos_roof({"images": [{"url": "https://example.test/roof-2.jpg"}]})

    assert first.source_fingerprint != second.source_fingerprint