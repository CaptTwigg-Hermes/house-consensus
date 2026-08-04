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


def test_e2e_artifacts_do_not_bind_mount_unc_checkout_paths():
    compose = Path("tests/HouseConsensus.Playwright/compose.e2e.yml").read_text()

    assert "./test-results:/tests/test-results" not in compose
    assert "./playwright-report:/tests/playwright-report" not in compose
    assert "playwright-test-results:/tests/test-results" in compose
    assert "playwright-report:/tests/playwright-report" in compose


def test_e2e_browser_allows_internal_http_and_proxies_listing_images():
    compose = Path("tests/HouseConsensus.Playwright/compose.e2e.yml").read_text()
    dockerfile = Path("tests/HouseConsensus.Playwright/Dockerfile").read_text()
    runner = Path("tests/HouseConsensus.Playwright/scripts/run-e2e.sh").read_text()
    browse = Path("tests/HouseConsensus.Playwright/specs/browse-map.spec.ts").read_text()
    auth = Path("tests/HouseConsensus.Playwright/specs/auth-cloudflare.spec.ts").read_text()
    members = Path("tests/HouseConsensus.Playwright/specs/owner-members.spec.ts").read_text()

    assert "E2E_BASE_URL: http://app:8080" in compose
    assert "context: ../.." in compose
    assert "dockerfile: tests/HouseConsensus.Playwright/Dockerfile" in compose
    assert "COPY tests/HouseConsensus.Playwright/package.json" in dockerfile
    assert "COPY src/Client/wwwroot/css/app.css /src/Client/wwwroot/css/app.css" in dockerfile
    assert "getent ahostsv4 app" in runner
    assert 'export E2E_BASE_URL="$BASE_URL"' in runner
    assert "card-image img" in browse
    assert "api\\/listings\\/" in browse
    assert "\\/image$/" in browse
    assert "memberPage.goto('/')" in auth
    assert "memberPage.goto('/')" in members
    assert "mock-ollama:" in compose
    assert "AiLearning__BaseUrl: http://mock-ollama:11434" in compose
    assert "AiLearning__Model: deterministic-e2e" in compose



def test_runtime_image_uses_the_dotnet_builtin_non_root_user():
    dockerfile = Path("Dockerfile").read_text()

    assert "USER $APP_UID" in dockerfile
    assert "adduser" not in dockerfile


def test_dev_compose_supports_safe_debug_auto_login_from_dotenv():
    compose = Path("docker-compose.dev.yml").read_text()

    assert "ASPNETCORE_ENVIRONMENT: Development" in compose
    assert "Debug__AutoLogin: ${DEBUG_AUTO_LOGIN:-false}" in compose
    assert "Debug__AutoLogin" not in Path("docker-compose.yml").read_text()


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


def test_main_compose_configures_reachable_private_ai_learning_endpoint():
    compose = Path("docker-compose.yml").read_text()

    assert "AiLearning__BaseUrl: ${AI_LEARNING_BASE_URL:-http://192.168.50.227:11434}" in compose
    assert "AiLearning__Model: ${AI_LEARNING_MODEL:-gemma4:12b}" in compose
    assert "AiLearning__AllowInsecureHttp: ${AI_LEARNING_ALLOW_INSECURE_HTTP:-true}" in compose
    assert "AiLearning__InsecureHttpAllowedHosts: ${AI_LEARNING_INSECURE_HTTP_ALLOWED_HOSTS:-192.168.50.227}" in compose



def test_production_compose_uses_existing_cloudflare_tunnel_and_requires_access_validation():
    compose = Path("docker-compose.production.yml").read_text()
    env_example = Path(".env.production.example").read_text() if Path(".env.production.example").exists() else ""
    readme = Path("README.md").read_text()

    assert "cloudflared:" not in compose
    assert "TUNNEL_TOKEN" not in compose
    assert 'CloudflareAccess__Enabled: "true"' in compose
    assert "CloudflareAccess__TeamDomain: ${CLOUDFLARE_ACCESS_TEAM_DOMAIN:?" in compose
    assert "CloudflareAccess__Audience: ${CLOUDFLARE_ACCESS_AUDIENCE:?" in compose
    assert "${APP_BIND_IP:-127.0.0.1}:${APP_PORT:-9000}:8080" in compose
    assert "existing TrueNAS Cloudflare Tunnel" in readme
    assert "CLOUDFLARE_ACCESS_TEAM_DOMAIN=" in env_example
    assert "CLOUDFLARE_ACCESS_AUDIENCE=" in env_example
    assert "mailpit:" not in compose
    assert "Email__SmtpHost" not in compose


def test_source_config_identity_schema_and_additive_migration_agree():
    schema = Path("exporter/src/consensus_exporter/schema.sql").read_text()
    migration_path = Path("src/Server/Data/Migrations/202608030002_AddSourceConfigIdentity.cs")
    assert migration_path.exists()
    migration = migration_path.read_text()

    for text in (schema, migration):
        assert "source_config_sha256" in text
        assert "^[0-9a-f]{64}$" in text
        assert "conrelid = 'export_runs'::regclass" in text
    assert "ADD COLUMN IF NOT EXISTS source_config_sha256 text" in migration
