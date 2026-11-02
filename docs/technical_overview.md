# Technical overview

## Database baseline (US-129)

Schema is managed only by EF Core migrations in `src/backend/Zephyr.Api/Data/Migrations` (Npgsql). `Seed/*.sql` files hold data only. Migrations run at API start (`Database.Migrate()`) and in `import` mode, and can be run with `dotnet ef database update`.

**Conventions (every table)**

| Column | Type | Notes |
|---|---|---|
| id | uuid | PK, `gen_random_uuid()` |
| created_at, updated_at | timestamptz | UTC, default `now()` |
| created_by, updated_by | uuid null | Reference to app_user; fixed System user `00000000-0000-0000-0000-000000000001` when no user |
| deleted_at | timestamptz null | Soft delete; global query filter `deleted_at IS NULL` |
| custom | jsonb | not null, default `'{}'` |

Naming is snake_case. Money columns (domain tables): `amount numeric(18,2)`, `currency char(3)`, `amount_sek numeric(18,2)`, `rate_date date`.

**Data model**

| Table | Purpose | Notes |
|---|---|---|
| external_id | Maps internal entities to external IDs | `entity_type`, `entity_id` uuid, `system` in ('dynamics','fortnox'), `value` text; unique (system, entity_type, value); index (entity_type, entity_id). External IDs live only here. |
| audit_log | Append-only audit trail | `user_id` uuid null, `action`, `entity_type`, `entity_id`, `at` timestamptz default now(), `detail` jsonb; index (entity_type, entity_id, at). Append-only trigger blocks DELETE. |

Extensions: `pg_trgm` and `unaccent` via `CREATE EXTENSION IF NOT EXISTS`.

**Components**
- `ZephyrDbContext`: `IEntity` base, soft-delete filter, naming, design-time factory.
- `AuditInterceptor`: sets created_by/updated_by/updated_at from `ICurrentUser` (null → System user); converts Delete into soft delete.
- `Program.cs`: `Database.Migrate()` at start; `import` mode migrates and exits; `/health`.

**Testing**
- `Zephyr.Api.Tests` is the only test project. `Support/DbFixture.cs` creates `zephyr_test_<guid>`, migrates once per run, resets with Respawn between tests (ignoring `__EFMigrationsHistory`, reference tables and `audit_log`), and drops the database at the end.
- Config: `ZEPHYR_TEST_DB` (falls back to `ZEPHYR_DB`); `ZEPHYR_REQUIRE_DB=1` makes a missing server fail the run (only `1`/`true` count). Without a server the DB tests are skipped.

**Rollback:** `Down()` drops `external_id` and `audit_log`; extensions remain.

**Open items:** model snapshot is hand-written; `dotnet ef migrations has-pending-model-changes` to be run in CI (US-132). DB tests first execute in CI with a postgres:16 service container (US-132).

## Local identity (Keycloak)

Keycloak stands in for Entra locally. It runs in dev mode on localhost only.

| Item | Value |
|---|---|
| Realm `zephyr` | Issuer `http://localhost:8180/realms/zephyr`; client `zephyr-web` (confidential, PKCE S256, standard flow) |
| Realm `zephyr-b` | Issuer `http://localhost:8180/realms/zephyr-b`; one user `user-b@example.com`, used for the switch-by-configuration test |
| Users | Seven in `zephyr`, one per role. Fixed UUIDs keep subjects stable across rebuilds. |
| Admin subject | `admin@example.com` has the `id` from `ZEPHYR_BOOTSTRAP_ADMIN_SUBJECT` |

- Templates: `infra/keycloak/zephyr-realm.template.json` and `zephyr-realm-b.template.json` (committed, placeholders only).
- Rendering: `scripts/render-realms.mjs` writes `infra/keycloak/generated/*.json` (gitignored). The `.sh` and `.ps1` wrappers call it. It fails if a variable is missing.
- Import: the keycloak service mounts `generated/` at `/opt/keycloak/data/import` and runs `start-dev --import-realm`.
- Zephyr stores no passwords. Roles are not mapped into tokens; they live in `app_user`.

## Local stack (Docker Compose)

| Service | Image | Ports (127.0.0.1) | Healthcheck |
|---|---|---|---|
| db | postgres:16 | 5432 | pg_isready |
| api | built from src/backend/Zephyr.Api/Dockerfile | 8080 | GET /health |
| web | built from src/frontend/Dockerfile | 3000 | HTTP GET / |
| keycloak | quay.io/keycloak/keycloak (pinned 26.0.7) | 8180 to 8080 | /health/ready on 9000 |
| mailpit | axllent/mailpit (pinned v1.21) | 1025 SMTP, 8025 UI | /livez |

- One Compose network and one named volume (`pgdata`).
- The API depends on a healthy db. Migrations run at API start (US-129).
- Configuration comes only from `.env` (created by `scripts/setup-env.mjs` from `.env.example`). `.env` and `generated/` are gitignored.
- `/health` is anonymous. It returns 200 when `SELECT 1` on the database succeeds, otherwise 503. It exposes no versions or secrets.
- Logs go to stdout as JSON lines.
- Compose uses no Docker-only features, so it also runs on Podman and Rancher Desktop.
- Status: code complete. The first real `docker compose up` is pending.

## Reference data and seeds (US-130)

**Migration `0002_ReferenceData`** (raw SQL, no model snapshot change) creates the reference tables. Every reference table has `key text unique`, `label text`, `sort_order int`, `active bool default true`.

| Table | Extra columns |
|---|---|
| stage | `is_terminal`, `exit_criteria jsonb null` |
| close_outcome | `maps_to_stage` (Won, Lost, NoDecision, Disqualified) |
| close_reason | `outcome_key` FK to close_outcome.key |
| call_outcome | `counts_as_contact` |
| time_type | `billable_default`, `requires_project`, `allows_future`, `category` (client, internal, absence) |
| country | `iso2 char(2) unique` (key = iso2) |
| currency | `iso3 char(3) unique`, `decimals int default 2` |
| meddpicc_element | none |
| account (minimal) | `name`, `country_code char(2) null`; unique index on `lower(name)` |

**Seed files** (applied in filename order by `scripts/seed.mjs`, one transaction per file):
- `Seed/0001_reference_data.sql`: reference rows, `ON CONFLICT (key) DO UPDATE`. Changes reach existing databases without a reset.
- `Seed/0002_demo_accounts.sql`: three demo accounts with fixed UUIDs, `ON CONFLICT (id) DO NOTHING`.
- No seed-tracking table is used.

**Command:** `npm run seed` (`node scripts/seed.mjs`). Connection from `DATABASE_URL` or `.env`. Dependencies: `pg`, `dotenv`.

**Validation:** `python3 scripts/validate_seeds.py [dir]` (stdlib only). Checks: sequential `NNNN_*.sql` names, no DELETE/DROP/TRUNCATE, ON CONFLICT on every INSERT, DO UPDATE for reference files and DO NOTHING for demo files, row counts per set (9/11/6/6/7/8), no email or phone outside `example.com`. Exit 1 prints `file:line reason`.

**Tests:** `SeedTests.cs` (AC 1–4) needs `ZEPHYR_TEST_DB`; CI sets `ZEPHYR_REQUIRE_DB=1`. `tests/seeds.test.mjs` covers AC 5 using `tests/seeds_broken/`.

**Not in this story:** exchange rates (US-134), volume seed set (US-131).
