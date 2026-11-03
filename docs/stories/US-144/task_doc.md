# US-144: Permission engine for API and UI routes
**Status:** Ready for UAT · **Category:** security · **Points:** 5 · **Sprint:** —

## Summary
One permission engine now decides access for both the API and the UI, so both enforce the same rules. Callers see only the rows their scope allows, out-of-scope records are hidden (404), and actions the role may not perform are refused (403). Personal tokens have exactly their owner's permissions.

## What was built
- `PermissionService` (`Permissions/PermissionService.cs`): `Scope` (widest scope across roles), `Filter<T>` (scope predicate as parameterised expression tree), `Can<T>`, `FindAsync<T>` (404/403 rules), `Grants`, in-memory matrix from `role_permission`, `Columns` map per entity.
- `PermissionFilter` (`Permissions/PermissionFilter.cs`): `RequirePermission(entity, action)`, `RequireRoles(...)`, `AsMeEndpoint()`, `CurrentUserAsync`.
- `PermissionEndpoints` (`Permissions/PermissionEndpoints.cs`): `GET /api/v1/me/permissions` (`Cache-Control: no-store`) returning `{roles, permissions, routes}`.
- `AnonymousAllowList` (`Permissions/AnonymousAllowList.cs`): the single list of anonymous endpoints.
- `UserAccess` and `IUserStatus.GetAsync` (`Auth/SessionRules.cs`): roles, home country and team from the cached user status.
- Frontend route guard and access-denied page (`src/frontend/src/permissions.tsx`, `main.tsx`, `strings.ts`); `apiFetch` reloads permissions once per load and after a 401 or 403.
- Removed the interim role-claim gate (`Permissions/InterimRoleGate.cs`, emptied; to be deleted with `git rm`).

## How it works
- **AC 1 Own scope:** an `own` scope lists only rows where the owner column equals the caller. Another user's record gives 404 when the caller cannot read it, and 403 when the caller can see the entity but may not update or delete it.
- **AC 2 Country scope:** a `country` scope matches the entity's country column against the caller's home country. A caller without a home country matches nothing. Out-of-scope ids return 404.
- **AC 3 Organisation scope:** reads return every record. POST, PATCH and DELETE are refused with 403 and an RFC 9457 problem body when the role lacks the action.
- **AC 4 UI parity:** `/api/v1/me/permissions` is built from the same engine. Any route not in `routes` (other than `/` and `/access-denied`) shows the access-denied page.
- **AC 5 Admin changes:** roles are read from `app_user` through the cached status, never from claims. A role change applies within `USER_STATUS_RECHECK_SECONDS` (300 s).
- **AC 6 Search:** the scope predicate is applied before the limit and paging, so totals exclude out-of-scope rows.
- **AC 7 Tokens:** a personal token gets its owner's roles from the cached status. Claims cannot grant more.
- **AC 8 Default deny:** a test enumerates all endpoints. Each one must declare a permission, use `RequireRoles`, be marked `me`, or be on the anonymous allow-list. Anything else fails the test.

## How to test
**Automated:** `npm run typecheck --silent && npm run lint --silent && npm run test --silent && npm run build --silent` (or `run_checks`). Tests marked `DbFact` need PostgreSQL (`ZEPHYR_TEST_DB`; CI sets `ZEPHYR_REQUIRE_DB=1`). Tests live in `Permissions/EngineTests.cs` and are named after the ACs.

**Manual checks (stack running, signed in):**
- **AC 1 Own scope:** an entity route using `Filter`/`FindAsync` with `own` lists only the caller's rows. **Verify:** another user's id gives 404 `NOT_FOUND`; a visible record the role may not update gives 403 `FORBIDDEN`.
- **AC 2 Country scope:** **Verify:** a user with `home_country = SE` and a `country` scope sees only SE rows; a NO row gives 404.
- **AC 3 Organisation scope:** sign in as Management. **Verify:** reads return all rows; POST, PATCH and DELETE return 403 with `application/problem+json`.
- **AC 4 UI parity:** **Verify:** `GET /api/v1/me/permissions` returns `{roles, permissions, routes}` with `Cache-Control: no-store`; `/admin/users` as a non-admin shows the access-denied page; a role without account create gets 403 on `POST` to an account route.
- **AC 5 Admin changes:** change `app_user.roles` in the database. **Verify:** the new roles apply after at most 300 s, for cookie and token calls.
- **AC 6 Search:** **Verify:** a list filtered with `Filter` returns a total and a page that exclude out-of-scope rows.
- **AC 7 Tokens:** **Verify:** a Consultant's token returns Consultant permissions even when its claims name Admin.
- **AC 8 Default deny:** **Verify:** `EngineTests.Ac8_*` passes; a route without a declaration makes it fail.

## Deployment notes
- **Pre-deploy:** none.
- **Post-deploy:** none. The permission matrix is seeded at start (US-143).
- **Cleanup (before merge):** `git rm src/backend/Zephyr.Api/Permissions/InterimRoleGate.cs`. The agent tooling cannot delete files.
- **Configuration:** `USER_STATUS_RECHECK_SECONDS` (default 300).

## Known limitations
- No entity list, detail or search routes exist yet. The engine is proven through test-only account routes in `EngineTests`. Entity stories (FT-113 to FT-119) must use `Filter` and `FindAsync` and declare columns in `PermissionService.Columns` (PO decision #148).
- Token tests use a stand-in sign-in handler that sets the same `zephyr_user_id` claim a token handler will set. Re-run AC 5 and AC 7 with the real token handler after US-142 merges (PO decision #150).
- No automated frontend tests. Frontend tests come with the e2e follow-up, US-146 AC 2 (PO decision #149).
- Field-level security is out of scope.
