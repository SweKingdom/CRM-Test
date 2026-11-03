# US-143: Permission model: roles, scopes and rules matrix (F06-S01)
**Status:** Ready for UAT · **Category:** security · **Points:** 5 · **Sprint:** not recorded

## Summary
The system now stores seven roles and a permission matrix that states, for every role, entity and action, which scope applies (none, own, team, country or org). The matrix is the single source for access rules and is seeded into the database at API start. The enforcement engine is not part of this story.

## What was built
- `permission-matrix.csv`: embedded resource with 665 rows (7 roles × 19 entities × 5 actions). Source of truth for the matrix.
- `PermissionSeeder.SeedAsync`: validates the CSV (unknown role, entity, action or scope, wrong header, duplicate row) and then upserts roles and rows in one transaction. Unchanged rows are not touched, and rows and roles no longer in the CSV are deleted.
- `0004_RolesPermissions` (EF migration): creates `role` and `role_permission`, and adds `app_user.manager_id`.
- `Program.cs`: runs the seeder after `Migrate()` at API start and in `import` mode. An invalid CSV stops the API.
- `docs/permissions.md`: model, team definition, project-scoped access, rules and the full matrix.
- `MatrixTests.cs`: tests for AC 1 to AC 5 and the Respawn reset regression.
- `DbFixture.cs` and `SeedTests.cs`: shared `RepoRoot()` and `RunSeedScript()` helpers.

## How it works
- **AC 1 Matrix:** the CSV holds one row per role × entity × action. `docs/permissions.md` explains it and its table equals the CSV.
- **AC 2 Stored model:** after migration and seed, `role` has 7 rows and `role_permission` holds all 665 rows, including `none` rows. A missing row also means `none`. A second seed changes no rows.
- **AC 3 Team definition:** team is the users with the same non-null `app_user.manager_id`, plus that manager. Project-scoped access (approvers, project team) is defined separately in `docs/permissions.md`. `manager_id` cannot point to the user itself.
- **AC 4 Integrity:** the seeder fails on any invalid or duplicate CSV line and names the bad line. Actions not listed default to `none`.
- **AC 5 Role keys:** every value in `app_user.roles` created by the seeds is an existing `role.key`. There is no foreign key because the column is `text[]`.

## How to test
**Checks:** `npm run typecheck --silent`, `npm run lint --silent`, `npm run test --silent`, `npm run build --silent`. DB tests need `ZEPHYR_TEST_DB`; CI sets `ZEPHYR_REQUIRE_DB=1`. Focused run: `dotnet test src/backend/Zephyr.Api.Tests --filter MatrixTests`.

- **AC 1 Matrix:** Verify: `MatrixTests.Ac1_*` confirms 665 rows. Check the PO rules and that the table in `docs/permissions.md` equals the CSV.
- **AC 2 Stored model:** Verify: after `PermissionSeeder.SeedAsync`, `select count(*) from role` returns 7 and `role_permission` equals the CSV row for row. A second seed leaves every `xmin` unchanged (`Ac2_*`).
- **AC 3 Team definition:** Verify: `docs/permissions.md` sections "Team" and "Project-scoped access". `Ac3_manager_id_*` shows the FK and the no-self-manager check.
- **AC 4 Integrity:** Verify: add an unknown role, entity, action or scope, or a duplicate row to the CSV and start the API. It stops and names each bad line (`Ac4_*`). The DB stays unchanged.
- **AC 5 Role keys:** Verify: `Ac5_Roles_on_users_created_by_the_seeds_are_existing_role_keys` passes.

## Deployment notes
- Pre-deploy: none recorded.
- Migration `0004_RolesPermissions` adds two tables and one column. The `Down` migration drops them.
- Configuration: none. The seed runs automatically at API start after migrations.
- Open for PO sign-off: the matrix cells not covered by PO answer #90 were set to `none` (see `docs/permissions.md`).

## Known limitations
- The permission engine (scope predicates, `RequirePermission`) is out of scope and not built.
- DB tests were skipped locally and reviewed by hand. The first real run is in CI.
- Uncovered matrix cells default to `none` and await PO sign-off.
- Team scope applies to BDR teams set by an admin and to project teams for time. The engine that applies it is not yet built.
