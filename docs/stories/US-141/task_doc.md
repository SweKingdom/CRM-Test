# US-141: Session limits and deactivation recheck
**Status:** Ready for UAT · **Category:** security · **Points:** 3 · **Sprint:** n/a

## Summary
Sessions now end after 4 hours without activity or 12 hours after sign-in, whichever comes first. A user who an admin deactivates loses access within 5 minutes, because the user status is rechecked on that interval. The limits and the recheck interval can be set by environment variables.

## What was built
- `Auth/SessionRules.cs` (new): `SessionSettings` (environment configuration), `IUserStatus` / `UserStatus` (cached active check with `Forget(id)`), `SessionRules` (stamps `issued_utc` at sign-in and applies the idle and absolute rules in `OnValidatePrincipal`).
- `Auth/AuthModule.cs`: cookie `ExpireTimeSpan` set to the idle limit; cookie options use the injected `TimeProvider`; `issued_utc` stamped at OIDC sign-in; services registered.
- `Auth/UsersEndpoints.cs` (new): `PATCH /api/v1/users/{id}` to activate or deactivate a user (Admin only).
- `Common/Problems.cs`: error codes `NOT_PATCHABLE` and `LAST_ADMIN` added.
- `Program.cs`: `MapUsersEndpoints()` registered.
- Tests: `Zephyr.Api.Tests/Auth/SessionTests.cs` (new, uses `FakeTimeProvider`), `OidcSignInTests.cs` (session cookie helper stamps `issued_utc`; user status stubbed), `Support/DbFixture.cs` (`DbTheory`), `Zephyr.Api.Tests.csproj` (`Microsoft.Extensions.TimeProvider.Testing`).
- `.env.example` and `tests/auth_env.test.mjs`: `SESSION_IDLE_SECONDS`, `SESSION_ABSOLUTE_SECONDS`, `USER_STATUS_RECHECK_SECONDS`.

## How it works
- **AC 1 Idle:** Each valid request renews the idle window, so the window counts from the last request. After 4 hours (14400 s) without a request, the next request gets 401 and the user must sign in again.
- **AC 2 Absolute:** The `issued_utc` stamp is set at sign-in. Once 12 hours (43200 s) have passed since then, the session is refused even if it is active, and the cookie is cleared. A cookie without `issued_utc` is refused.
- **AC 3 Deactivation:** `UserStatus` caches both active and inactive answers for the recheck window (300 s). `PATCH /api/v1/users/{id}` with `{"active":false}` calls `Forget(id)`, so the change applies on the next request. Otherwise the user is refused no later than 5 minutes after deactivation.
  - Requires `Admin` role, `If-Match` header, and `X-Zephyr-Csrf: 1`.
  - Errors: 403 for non-admins, 428/412 for ETag problems, 404 for unknown user, 422 `NOT_PATCHABLE` or `LAST_ADMIN`.
  - Deactivating an admin takes a transaction-scoped advisory lock so two admins cannot deactivate each other at the same time.
  - A patch with the same value as the stored one returns 200 with no audit row and no change to `updated_at`.
- **AC 4 Testable clock:** Tests use `FakeTimeProvider`; no real waiting.

## How to test
**Checks:** `npm run typecheck --silent`, `npm run lint --silent`, `npm run test --silent`, `npm run build --silent`.
`dotnet test src/backend/Zephyr.Api.Tests --filter FullyQualifiedName~SessionTests`. DB-backed tests need `ZEPHYR_TEST_DB`; CI sets `ZEPHYR_REQUIRE_DB=1`.

- **AC 1 Idle:** Run `Ac1_*` in SessionTests. Verify: no request for 4 h + 1 s gives 401; a request within 4 h of the previous one keeps the session; `SESSION_IDLE_SECONDS=60` changes the limit.
- **AC 2 Absolute:** Run `Ac2_*`. Verify: requests every 3 h or less keep the session until 12 h; at 12 h + 1 s the response is 401 and the cookie is cleared; a cookie without `issued_utc` is refused; `SESSION_ABSOLUTE_SECONDS` changes the limit.
- **AC 3 Deactivation:** Run `Ac3_*` and `Patch_*`. Verify: a request 5 min + 1 s after deactivation gets 401; within the window the cached status is used; `PATCH /api/v1/users/{id}` with `{"active":false}` refuses the user on the next request; non-admin gets 403; missing or stale ETag gets 428/412; unknown id gets 404; `NOT_PATCHABLE` / `LAST_ADMIN` give 422.
- **AC 4 Testable clock:** Verify: `grep -R "Thread.Sleep\|Task.Delay" src/backend/Zephyr.Api.Tests/Auth/SessionTests.cs` finds nothing.

## Deployment notes
- **Optional configuration** in `.env` (defaults shown):
  - `SESSION_IDLE_SECONDS` = 14400
  - `SESSION_ABSOLUTE_SECONDS` = 43200
  - `USER_STATUS_RECHECK_SECONDS` = 300
- **Manual step:** users with a session cookie from before this release must sign in once more, because their cookie has no `issued_utc`.
- No database migration in this story.

## Known limitations
- **Token refusal (AC 3, partial):** The API-token handler does not exist yet. It belongs to US-142, which must call `IUserStatus.IsActiveAsync` for the token's user. Per product owner answer #134, the shared check is built and tested at service level (including the 5 min + 1 s refusal). The end-to-end `zph_` token test moves to US-142.
- **DB-backed tests:** Not run locally (no PostgreSQL available). The first real run is in CI with `ZEPHYR_REQUIRE_DB=1`.
- **Out of scope:** Admin UI for user CRUD beyond the activate/deactivate endpoint.
