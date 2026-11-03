# US-135: Upsert by external ID and soft delete
**Status:** Ready for UAT · **Category:** development · **Points:** 5 · **Sprint:** not recorded in context

## Summary
Integrators can now send an account to the API by its external ID and re-sending the same record updates it instead of creating a duplicate. Accounts can also be soft-deleted: a deleted account returns 404 and disappears from lists, and its external ID can no longer be reused to revive it.

## What was built
- `Common/ExternalUpsert.cs`: `EntityBinding<T>` (field validation and apply), `MapExternalUpsert<T>` (`PUT /api/v1/{plural}/by-external-id/{system}/{value}`) and `MapSoftDelete<T>` (`DELETE /api/v1/{plural}/{id}`).
- `Permissions/EntityRegistry.cs`: `EntityInfo` (plural, entity key, permission key) and the `Accounts` binding (field validation and response shape).
- `Permissions/InterimRoleGate.cs`: temporary role check that reads the roles of the caller's active `app_user`. Marked `TODO(US-143/144)`; to be replaced by `RequirePermission`.
- `Data/ZephyrDbContext.cs`: EF mapping `Account` on the existing `account` table. No schema change; the model snapshot is unchanged by this story.
- `Common/Problems.cs`: error code `FORBIDDEN`.
- `Program.cs`: maps the upsert and delete routes for accounts.
- `Zephyr.Api.Tests/Common/UpsertTests.cs`: AC 1–7 tests, concurrency test and route wiring test.

## How it works
- **AC 1 (upsert twice):** The first `PUT` with a new key returns 201 with `Location` and `ETag`. A second `PUT` with the same key returns 200 and updates the fields. One `account` row and one `external_id` row exist.
- **AC 2 (soft delete):** `DELETE` returns 204 and sets `deleted_at`. Later reads return 404 and lists omit the row. A repeat `DELETE` returns 404. Interim rule: only Admin may delete (see Known limitations).
- **AC 3 (concurrent upsert):** Both calls succeed (one 201, one 200). The `external_id` insert uses `ON CONFLICT DO NOTHING`, so exactly one row results and no 500 is returned.
- **AC 4 (scope):** The key is `(system, entity type, external value)`. The same value under `dynamics` and `fortnox` creates two separate accounts.
- **AC 5 (deleted record):** An upsert whose mapped account is soft-deleted returns 409 with `RECORD_DELETED`. The transaction is rolled back and nothing is revived.
- **AC 6 (who may upsert):** Only Admin and Integration pass the interim gate. All other roles get 403 `FORBIDDEN` before any write. Interim gate, not the permission matrix (see Known limitations).
- **AC 7 (validation):** The request is checked before any write: role, body read (400), `WriteHooks` (422), `system` in `dynamics`/`fortnox`, value up to 200 characters, and account fields via `EntityBinding`. A validation failure leaves no `external_id` row. `If-Match` is ignored.

Account body (PUT replaces the record): `name` required text; `countryCode` optional, two capital letters; an absent `countryCode` clears it.

## How to test
**Checks:** `npm run typecheck --silent`, `npm run lint --silent`, `npm run test --silent`, `npm run build --silent`. The `UpsertTests` need PostgreSQL (`ZEPHYR_TEST_DB`, e.g. the Compose db service). Without it they are skipped locally; CI runs them with `ZEPHYR_REQUIRE_DB=1`.

Manual checks (credential of an Admin or Integration user as `{token}`):
- **AC 1:** `PUT /api/v1/accounts/by-external-id/dynamics/g1` with `{"name":"Acme"}` twice. **Verify:** first 201 with `Location` and `ETag`, second 200 with changed fields; one row in `account` and one in `external_id`.
- **AC 2:** `DELETE /api/v1/accounts/{id}` as Admin. **Verify:** 204 and `deleted_at` set; a repeat DELETE is 404; the row is absent from lists.
- **AC 3:** send the same PUT twice at the same time. **Verify:** one 201 and one 200, no 500, one `account` row.
- **AC 4:** PUT value `g1` under `dynamics` and `fortnox`. **Verify:** two separate accounts and two `external_id` rows.
- **AC 5:** PUT a key whose account was deleted. **Verify:** 409 problem with `code` = `RECORD_DELETED`; `deleted_at` still set.
- **AC 6:** PUT as Salesperson, BDR, PM, Consultant, Finance or Management. **Verify:** 403 `FORBIDDEN`, no rows created or changed. Admin and Integration succeed.
- **AC 7:** PUT `{}`. **Verify:** 422 with `errors[]` naming `name`; no `external_id` row exists for the key.

## Deployment notes
- **Migrations:** none. The `account` table (0002) and `external_id` (0001) already exist. No trigger or constraint was added, so the Respawn test reset is unaffected.
- **Configuration:** none new. Tests need `ZEPHYR_TEST_DB`; CI sets `ZEPHYR_REQUIRE_DB=1`.
- **Pre-deploy / post-deploy steps:** none.

## Known limitations
- **Interim permission rules (PO answers #132 and #133, accepted):**
  - Upsert uses `InterimRoleGate` (Admin or Integration), not the permission matrix (`create` + `update`, scope `org`). The permission engine (US-143/144) is not in the repository.
  - DELETE is Admin only, because no delete matrix exists yet. Integration and all other roles get 403.
  - Follow-up: US-144 gets an acceptance criterion to remove `InterimRoleGate` and use the permission matrix.
- **Test-only read routes:** account GET and list endpoints do not exist yet. The 404 and hidden-from-lists checks use test-only routes over the global soft-delete filter. Move them to the real endpoints when those land.
- **Only `account` is registered.** Other entities need one `EntityBinding<T>` in `EntityRegistry` and two map calls in `Program.cs` once their tables exist.
- **Hard delete** is out of scope (GDPR story).
- **DB-backed tests:** the `UpsertTests` run for the first time in CI with `ZEPHYR_REQUIRE_DB=1`.
