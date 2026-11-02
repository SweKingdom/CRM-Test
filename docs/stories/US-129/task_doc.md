# US-129: Database baseline migrations: conventions, extensions, external_id, audit
**Status:** Ready for UAT · **Category:** development · **Points:** 5 · **Sprint:** not recorded

## Summary
Adds the first EF Core migration (`0001_Baseline`) with the shared table conventions for the CRM: UUID keys, audit columns, soft delete and a `custom` JSONB column. It also creates the `external_id` and `audit_log` tables and enables the `pg_trgm` and `unaccent` extensions. Users see no change; this is the foundation every later table builds on.

## What was built
- `ZephyrDbContext` (`Data/ZephyrDbContext.cs`): `IEntity` base (id, created_at, updated_at, created_by, updated_by, deleted_at, custom), global query filter `deleted_at IS NULL`, snake_case naming, design-time factory, `ExternalId` and `AuditLog` entities.
- `AuditInterceptor` (`Data/AuditInterceptor.cs`): SaveChanges interceptor that sets audit columns from `ICurrentUser` (falls back to the fixed System user) and turns deletes into soft deletes.
- `Baseline` migration (`Data/Migrations/Baseline.cs`, id `0001_Baseline`) and hand-written `ZephyrDbContextModelSnapshot.cs`.
- `Program.cs`: `Database.Migrate()` at API start, `import` mode migrates and exits without starting a web host, `/health` endpoint.
- Test project `Zephyr.Api.Tests`: `Support/DbFixture.cs` (creates `zephyr_test_<guid>`, migrates once per run, drops it at the end, Respawn reset between tests) and `BaselineTests.cs`.
- Root tooling: `package.json` (typecheck, lint, test, build scripts wrapping dotnet), `.editorconfig`, `.gitignore` (bin/, obj/, .env).

## How it works
- **AC 1 Extensions:** the migration runs `CREATE EXTENSION IF NOT EXISTS` for `pg_trgm` and `unaccent`. Both appear in `pg_extension` after migrating an empty database.
- **AC 2 Idempotent migrations:** EF Core records applied migrations in `__EFMigrationsHistory`. A second run applies nothing and leaves one row for `0001_Baseline`.
- **AC 3 external_id:** `system` is checked against `dynamics` and `fortnox`. The pair (system, entity_type, value) is unique. A duplicate or an unknown system is rejected by the database.
- **AC 4 Audit columns:** on insert `created_by` is set; on update `updated_at` (UTC) and `updated_by` are set. The value comes from the authenticated app user. With no user (seed, migration, system work) the fixed System user id `00000000-0000-0000-0000-000000000001` is used.
- **AC 5 Soft delete:** deleting through the data layer sets `deleted_at` instead of removing the row. Default queries exclude rows where `deleted_at` is not null.
- **Rework fix:** Respawn no longer tries to clear `audit_log`, because its append-only trigger blocks DELETE. `audit_log` is in `TablesToIgnore`. A regression test, `Reset_between_tests_works_while_audit_log_holds_rows`, writes an audit row, resets, and checks that `external_id` is cleared while `audit_log` keeps its rows.
- `DbRequired()` treats only `1` or `true` (case-insensitive) as "require the database". `0` no longer counts.

## How to test
Unit and project checks: `npm run typecheck`, `npm run lint`, `npm run test`, `npm run build`.

DB tests need PostgreSQL. Set `ZEPHYR_TEST_DB` (for example `Host=127.0.0.1;Port=5432;Username=...;Password=...`). Without it the 9 DB tests are reported as skipped. With `ZEPHYR_REQUIRE_DB=1` a missing server fails the run.

- AC 1: `Ac1_Extensions_...` passes, or `SELECT extname FROM pg_extension` lists pg_trgm and unaccent.
- AC 2: run `dotnet run --project src/backend/Zephyr.Api -- import` twice with `ZEPHYR_DB` set. The second run succeeds and `__EFMigrationsHistory` has one row.
- AC 3: `Ac3_*` tests pass; a duplicate (system, entity_type, value) and system `salesforce` throw DbUpdateException.
- AC 4: `Ac4_*` tests pass; `created_by`/`updated_by` equal the user id, or the System user id with no user.
- AC 5: `Ac5_*` test passes; the row has `deleted_at` set and is absent from default queries.

## Deployment notes
- **Configuration:** set `ZEPHYR_DB` (API connection string). Set `ZEPHYR_TEST_DB` for tests. CI must set `ZEPHYR_REQUIRE_DB=1`. `ZEPHYR_TEST_DB` with a localhost default is to be added to `.env.example` by US-127.
- **Migration:** applied automatically at API start and by `import` mode. It can also be run with `dotnet ef database update`.
- **Snapshot check:** the model snapshot was written by hand. Run `dotnet ef migrations has-pending-model-changes` to confirm there is no drift. This check is to be added to CI in US-132.
- **Dependencies:** US-127 (Compose stack) and US-132 (CI) are required. Reference tables come in `0002_ReferenceData` (US-130).

## Known limitations
- The 9 DB tests have not been run against a real PostgreSQL server in this environment. The first real run is in CI (US-132, `ZEPHYR_REQUIRE_DB=1`). Accepted by the product owner.
- The model snapshot is hand-written and not yet verified with `dotnet ef`. Accepted by the product owner; covered by the US-132 CI check.
- Domain tables (money columns, business entities) are out of scope and come in their own stories.
- Rollback: `Down()` drops `external_id` and `audit_log`. Extensions stay in place.
