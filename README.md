# Zephyr

Lean, headless CRM and project-operations system. This edition runs entirely on localhost with synthetic data and no cloud services.

## Run Zephyr locally

Prerequisites:

- Docker Desktop (or Rancher Desktop / Podman with compose). The Compose file uses plain `docker compose` features only.
- Node.js 20 or newer (the seed script and setup scripts).
- .NET SDK 8 and Python 3, only for step 8 (running the checks).

Run every step in order, from the repository root. Replace `<repository-url>` with the URL of this repository.

1. Clone and install the Node dependencies:

```sh
git clone <repository-url> zephyr
cd zephyr
npm ci
```

2. Create `.env` (random passwords, never overwrites an existing `.env`) and render the Keycloak realms:

```sh
node scripts/setup-env.mjs
```

3. Build and start the stack. `--wait` returns when every service is healthy; the first build takes several minutes:

```sh
docker compose up --build -d --wait
```

4. Give the seed script a database URL on localhost. It reuses the database password already in `.env`; run the line for your shell, once:

```sh
# bash / zsh
echo "DATABASE_URL=postgres://zephyr:$(grep '^POSTGRES_PASSWORD=' .env | cut -d= -f2-)@localhost:5432/zephyr" >> .env
```

```powershell
# PowerShell
$pw = (Select-String -Path .env -Pattern '^POSTGRES_PASSWORD=(.*)$').Matches[0].Groups[1].Value
Add-Content .env "DATABASE_URL=postgres://zephyr:$pw@localhost:5432/zephyr"
```

5. Load the reference data and the three demo accounts (safe to repeat; the API already applied the migrations when it started):

```sh
npm run seed
```

6. Check that the API is up:

```sh
curl http://localhost:8080/health
```

   Expected: `{"status":"Healthy"}`.

7. Sign in as a seeded user. Open the Keycloak account page for realm `zephyr`, <http://localhost:8180/realms/zephyr/account>, and sign in as `salesperson@example.com`. The password is the value of `KC_USER_SALESPERSON_PASSWORD` in `.env`; print it with:

```sh
# bash / zsh
grep '^KC_USER_SALESPERSON_PASSWORD=' .env
```

```powershell
# PowerShell
Select-String -Path .env -Pattern '^KC_USER_SALESPERSON_PASSWORD='
```

   The web app at <http://localhost:3000> is a placeholder page until the sign-in story (US-140) is merged; until then Keycloak is where you sign in.

8. Run the project checks (needs the .NET SDK and Python 3; database tests are skipped without PostgreSQL):

```sh
npm run typecheck && npm run lint && npm test && npm run build
```

9. Stop the stack. Data is kept in the `pgdata` volume:

```sh
docker compose down
```

To reset to an empty system, remove the volume and repeat from step 3 (`.env` is kept):

```sh
docker compose down -v
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

Useful while it runs:

```sh
docker compose logs api   # JSON log lines on stdout
```

## Synthetic volume seed

Dynamics-scale test data for performance measurements: about 56,000 searchable records (6,000 accounts, 18,000 contacts, 12,000 leads, 9,000 opportunities, 11,000 projects) and about 105,000 time entries, 18,495 of them in the 12 months up to the anchor date. All of it is synthetic and every row has `custom->>'synthetic' = 'true'`. About 2% of the names are typo variants (the correct spelling is in `custom->>'typo_of'`), and names include åäöéü, Malmö, Göteborg and Västerås.

```sh
npm run seed -- --volume                     # load (needs DATABASE_URL, see .env.example; the stack must be migrated)
npm run seed -- --volume --reset             # remove the synthetic rows first, then load again
npm run seed -- --volume --seed 7 --anchor 2026-12-11
```

- Entities whose table does not exist yet print `SKIP <entity>: table missing`; the rest are loaded. Run the seed again once more tables exist.
- Running again without `--reset` refuses. `--reset` only deletes rows with the synthetic marker.
- The same `--seed` (default 42) and `--anchor` (default: today in Europe/Stockholm) always give the same ids and names.
- Targets are in `tools/search-benchmark/generate/targets.json`; one SQL file per entity next to it; name lists in `tools/search-benchmark/data/`.

## API description (OpenAPI)

`docs/api/openapi.json` is the OpenAPI 3.1 description of the API (base `/api/v1`). It is generated and never edited by hand; JSON has no comments, so `info.description` carries the "generated, do not edit" notice. The source is the field metadata in `tools/openapi-from-metadata/field-metadata.json`: each field gives its type, nullability, enum, max length and whether a client may send it, and becomes a schema property, a `required` entry and a validation constraint.

The file is written with `JSON.stringify`: two-space indent, LF line ends, `openapi` and `info` first and every other key sorted, so the same metadata always gives the same bytes (no timestamps).

```sh
node tools/openapi-from-metadata/generate.mjs                  # rewrite docs/api/openapi.json
node tools/openapi-from-metadata/generate.mjs --out api.json   # write somewhere else (add a metadata path first to use another metadata file)
```

Change the metadata, run the generator and commit both. The quality gate then checks that the committed file is exactly what the generator writes (step `openapi-regenerate`, failure message "run generate"), lints it with Spectral (`.spectral.yaml`) and compares it with the copy on `main` with `oasdiff breaking`. A removed field, a narrowed type or a new required property fails the gate; a deliberate break needs the PR label and a changelog note (a review policy, not a script). The test `OpenApiContractTests` fails when the API registers a route the file does not document, or the file documents a route the API lacks.

## Quality gate

Run the quality gate before you open a PR. GitHub Actions runs the same script on every pull request and deploys nothing.

```sh
scripts/check          # Linux, macOS, WSL, Git Bash (or: bash scripts/check)
scripts/check.ps1      # Windows PowerShell
```

It runs eight steps in a fixed order, always all of them, prints `PASS`, `FAIL` or `SKIP` for each and a summary, and exits 1 if any step failed:

| # | Step | What it runs |
|---|---|---|
| 1 | `lint` | `npm run typecheck` (`dotnet build -warnaserror`) and `npm run lint` (`dotnet format --verify-no-changes`); the frontend `lint` and `typecheck` scripts when it has them |
| 2 | `tests` | `npm test`: `dotnet test` with the integration tests on PostgreSQL (the permission tests included), then the Node tests |
| 3 | `seeds` | `python3 scripts/validate_seeds.py` |
| 4 | `openapi-regenerate` | `node tools/openapi-from-metadata/generate.mjs --out <temporary file>`; fails with "run generate" if it differs from the committed `docs/api/openapi.json` |
| 5 | `spectral` | Spectral 6.15.0 (`.spectral.yaml`) on `docs/api/openapi.json` |
| 6 | `oasdiff` | `oasdiff breaking` against `origin/main:docs/api/openapi.json` (needs `oasdiff` installed; skipped when `main` has no such file yet) |
| 7 | `schemathesis` | Schemathesis 4 against the API started on 127.0.0.1:18080 with the database `zephyr_check` (needs `schemathesis` installed) |
| 8 | `vulnerabilities` | `dotnet list package --vulnerable --include-transitive` and `npm audit --audit-level=high` |

- **Database:** locally the script starts the Compose `db` service, waits until it is healthy and leaves it running. To use another server, set `ZEPHYR_TEST_DB` to its connection string. In CI (`CI=true`) the workflow's PostgreSQL 16 service container is used and Compose is not called. Each run creates and drops its own database.
- **SKIP:** a step whose target does not exist yet prints `SKIP (not yet applicable: ...)` and is listed in the summary. `SKIP (needs Keycloak)` means the API schema needs sign-in and `ZEPHYR_CHECK_TOKEN` (a bearer token) is not set; Keycloak is not started in CI, so run `docker compose up -d keycloak` locally for those checks. With `REQUIRE_ALL=1` (set in CI) a step that is skipped because its tool is missing, although its target exists, fails. Locally `oasdiff` is optional and its step shows `SKIP (oasdiff is not installed)`; CI installs a pinned release and requires it.
- **`--ui`:** `scripts/check --ui` (PowerShell: `scripts/check.ps1 -Ui`) adds a ninth step, `ui`, the Playwright permission suite in `src/frontend/e2e` against the running stack. It is not part of the eight steps and not run by CI. Until that suite exists the step fails with a message instead of passing silently.
- The individual commands still work alone: `npm run typecheck && npm run lint && npm test && npm run build`.

### Permission tests

`src/backend/Zephyr.Api.Tests/Permissions/MatrixCoverageTests.cs` (US-146) runs in step 2. It has one test case for every row of `permission-matrix.csv` (role x entity x action, 665 in all). Each case checks that the stored matrix equals the row and, for an entity that has an API route, that the real route answers as the row says: scope `none` is exactly 403, any other scope is not 401, 403 or 5xx. The tests use one seeded user per role and a test sign-in handler that only the test project registers; they need PostgreSQL like the other integration tests.

- An entity without an API route yet is listed in `Pending` in that file. A new `/api/v1` route fails the route test until its entity has a probe (and is removed from `Pending`), or until it is listed in `OutsideMatrix` with the reason it is not a cell of the matrix.
- `OutsideMatrix` lists the routes that are not compared with the matrix: the by-external-id upsert (an integration route for import and migration, Admin and Integration only, see US-135), `GET /api/v1/me/permissions`, and the approver and delegation routes (US-145). An anonymous call to each must still be 401.
- Account `create` and `update` for Salesperson and BDR stay in the matrix and have no probe until the normal POST and PATCH account routes exist (FT-113). Admin has `delete` on `account` at scope `org`, tested against `DELETE /api/v1/accounts/{id}`.
- Not covered yet, because the code they test does not exist: time-entry privacy, the search leak test and the Playwright suite (follow-up stories).

## Build log and decisions

- `BUILD.md`: one section per merged feature (goal, prompts used, decisions, files, how to verify, reviewer sign-off). The template is `build/prompts/feature-section.md`.
- `docs/adr/`: architecture decision records and `docs/adr/README.md`, the approval index.
