# Zephyr

Lean, headless CRM and project-operations system. This edition runs entirely on localhost with synthetic data and no cloud services.

## Run Zephyr locally

Prerequisites: Docker Desktop (or Rancher Desktop / Podman with compose) and Node.js 20+. The Compose file uses plain `docker compose` features only.

```sh
node scripts/setup-env.mjs   # creates .env with random passwords (never overwrites it) and renders the Keycloak realms
docker compose up --build
```

| Service | URL |
|---|---|
| API | http://localhost:8080 (health: http://localhost:8080/health) |
| Web | http://localhost:3000 |
| Keycloak | http://localhost:8180 |
| Mailpit (caught mail) | http://localhost:8025 (SMTP on localhost:1025) |
| PostgreSQL | localhost:5432 |

All ports are bound to 127.0.0.1. Generated passwords are in `.env`, which is gitignored. `.env.example` lists every variable with placeholders only.

### Keycloak

Keycloak imports two realms from `infra/keycloak/*.template.json` (placeholders only), rendered to `infra/keycloak/generated/` (gitignored) from `.env`:

- `zephyr`: seven users, one per role (`admin`, `salesperson`, `bdr`, `pm`, `consultant`, `finance`, `management` `@example.com`). `admin@example.com` has the subject in `ZEPHYR_BOOTSTRAP_ADMIN_SUBJECT`.
- `zephyr-b`: one user `user-b@example.com`, a different issuer for the switch-by-configuration test.

Passwords are in `.env`:

| Variable | Role |
|---|---|
| `KC_USER_ADMIN_PASSWORD` | Admin |
| `KC_USER_SALESPERSON_PASSWORD` | Salesperson / opportunity owner |
| `KC_USER_BDR_PASSWORD` | Business development rep |
| `KC_USER_PROJECT_MANAGER_PASSWORD` | Project manager / approver |
| `KC_USER_CONSULTANT_PASSWORD` | Consultant |
| `KC_USER_FINANCE_PASSWORD` | Finance |
| `KC_USER_MANAGEMENT_PASSWORD` | Management / leadership |
| `KC_USER_B_PASSWORD` | `user-b@example.com` in realm `zephyr-b` |

After changing a password in `.env`, run `node scripts/render-realms.mjs` and recreate the Keycloak container; realms are only imported when they don't exist yet.

Check that it works:

```sh
curl http://localhost:8080/health   # {"status":"Healthy"}
docker compose logs api             # JSON log lines on stdout
```

## Checks

Run the quality gate before you open a PR. GitHub Actions runs the same script on every pull request and deploys nothing.

```sh
scripts/check          # Linux, macOS, WSL, Git Bash (or: bash scripts/check)
scripts/check.ps1      # Windows PowerShell
```

It runs eight steps in a fixed order, always all of them, prints `PASS`, `FAIL` or `SKIP` for each and a summary, and exits 1 if any step failed:

| # | Step | What it runs |
|---|---|---|
| 1 | `lint` | `npm run typecheck` (`dotnet build -warnaserror`) and `npm run lint` (`dotnet format --verify-no-changes`); the frontend `lint` and `typecheck` scripts when it has them |
| 2 | `tests` | `npm test`: `dotnet test` with the integration tests on PostgreSQL, then the Node tests |
| 3 | `seeds` | `python3 scripts/validate_seeds.py` |
| 4 | `openapi-regenerate` | `node tools/openapi-from-metadata/generate.mjs docs/api/openapi.json`; fails if the committed file differs |
| 5 | `spectral` | Spectral 6.15.0 (`.spectral.yaml`) on `docs/api/openapi.json` |
| 6 | `oasdiff` | `oasdiff breaking` against the copy on `main` (needs `oasdiff` installed) |
| 7 | `schemathesis` | Schemathesis 4 against the API started on 127.0.0.1:18080 with the database `zephyr_check` (needs `schemathesis` installed) |
| 8 | `vulnerabilities` | `dotnet list package --vulnerable --include-transitive` and `npm audit --audit-level=high` |

- **Database:** locally the script starts the Compose `db` service, waits until it is healthy and leaves it running. To use another server, set `ZEPHYR_TEST_DB` to its connection string. In CI (`CI=true`) the workflow's PostgreSQL 16 service container is used and Compose is not called. Each run creates and drops its own database.
- **SKIP:** a step whose target does not exist yet prints `SKIP (not yet applicable: ...)` and is listed in the summary; steps 4 to 7 skip until the OpenAPI generator exists. `SKIP (needs Keycloak)` means the API schema needs sign-in and `ZEPHYR_CHECK_TOKEN` (a bearer token) is not set; Keycloak is not started in CI, so run `docker compose up -d keycloak` locally for those checks. With `REQUIRE_ALL=1` (set in CI) a step that is skipped because its tool is missing, although its target exists, fails.
- The individual commands still work alone: `npm run typecheck && npm run lint && npm test && npm run build`.
