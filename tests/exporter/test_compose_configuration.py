from pathlib import Path


def test_e2e_compose_builds_app_with_server_configuration_keys():
    compose = Path("tests/HouseConsensus.Playwright/compose.e2e.yml").read_text()

    assert "context: ../.." in compose
    assert "ConnectionStrings__Database:" in compose
    assert "INITIAL_OWNER_EMAIL: owner@example.test" in compose
    assert "PublicOrigin: http://app:8080" in compose
    assert "ConnectionStrings__DefaultConnection:" not in compose


def test_shared_e2e_household_uses_bootstrap_owner_and_one_worker():
    helper = Path("tests/HouseConsensus.Playwright/helpers/household.ts").read_text()
    config = Path("tests/HouseConsensus.Playwright/playwright.config.ts").read_text()

    assert "owner@example.test" in helper
    assert "workers: 1" in config


def test_e2e_compose_enables_seed_data_and_test_rate_limit():
    compose = Path("tests/HouseConsensus.Playwright/compose.e2e.yml").read_text()

    assert 'E2E__SeedData: "true"' in compose
    assert 'Auth__MagicRequestPermitLimit: "100"' in compose
    assert 'Auth__MagicConsumePermitLimit: "100"' in compose


def test_runtime_image_uses_the_dotnet_builtin_non_root_user():
    dockerfile = Path("Dockerfile").read_text()

    assert "USER $APP_UID" in dockerfile
    assert "adduser" not in dockerfile
