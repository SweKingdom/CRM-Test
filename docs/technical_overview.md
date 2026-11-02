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
