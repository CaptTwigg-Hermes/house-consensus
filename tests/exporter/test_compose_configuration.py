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


def test_dev_compose_provides_an_explicit_real_house_importer():
    compose = Path("docker-compose.dev.yml").read_text()
    dockerfile = Path("exporter/Dockerfile").read_text() if Path("exporter/Dockerfile").exists() else ""

    assert "importer:" in compose
    assert "../houseshopping/state/house.db" in compose
    assert "CONSENSUS_DATABASE_URL:" in compose
    assert '"--ensure-schema"' in compose
    assert '"--skip-media"' in compose
    assert "house-consensus-export" in dockerfile
    assert dockerfile.index("COPY src ./src") < dockerfile.index("RUN uv sync")


def test_windows_import_script_stages_unc_sqlite_on_local_disk():
    script_path = Path("scripts/import-houses.ps1")
    script = script_path.read_text() if script_path.exists() else ""

    assert "$env:LOCALAPPDATA" in script
    assert "Copy-Item" in script
    assert "$env:HOUSESHOPPING_DB" in script
    assert "--profile tools" in script


def test_main_compose_uses_external_postgres_5433_for_app_and_importer():
    base = Path("docker-compose.yml").read_text()
    dev = Path("docker-compose.dev.yml").read_text()
    expected = "Host=${POSTGRES_HOST:-192.168.50.2};Port=${POSTGRES_PORT:-5433};Database=${POSTGRES_DB:-house_consensus};Username=${POSTGRES_USER:-house_consensus}"

    assert expected in base
    assert "host=${POSTGRES_HOST:-192.168.50.2} port=${POSTGRES_PORT:-5433}" in dev
    assert "dbname=${POSTGRES_DB:-house_consensus} user=${POSTGRES_USER:-house_consensus}" in dev
    assert "  postgres:\n" not in base
    assert "postgres-data" not in base
