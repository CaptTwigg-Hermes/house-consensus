from pathlib import Path


def test_e2e_compose_builds_app_with_server_configuration_keys():
    compose = Path("tests/HouseConsensus.Playwright/compose.e2e.yml").read_text()

    assert "context: ../.." in compose
    assert "ConnectionStrings__Database:" in compose
    assert "INITIAL_OWNER_EMAIL: owner@example.test" in compose
    assert "PublicOrigin: http://app:8080" in compose
    assert "ConnectionStrings__DefaultConnection:" not in compose
