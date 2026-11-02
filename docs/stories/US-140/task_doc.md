# US-140: OIDC sign-in with user matching and bootstrap admin
**Status:** Ready for UAT · **Category:** security · **Points:** 5 · **Sprint:** (not recorded in context)

## Summary
Staff sign in through the organisation's OpenID Connect identity provider (Keycloak locally, Entra later by configuration only), so Zephyr never holds a password. Users must be pre-provisioned; unknown users are refused, and a bootstrap admin can be set once while no admin exists. Cookie-authenticated writes require the `X-Zephyr-Csrf: 1` header, and the SPA runs same-origin with no CORS.

## What was built
- `Auth/AuthModule.cs`: OIDC and cookie registration per provider (`AddZephyrAuth()`), user matching in `OnTokenValidated`, bootstrap admin, CSRF check, `/auth/login`, `/auth/logout`, `/auth/me`, `/auth/denied`.
- `Data/ZephyrDbContext.cs`: `AppUser` entity and `AppUsers` set; `AuditLog` mapping.
- `Data/Migrations/0003_AppUser.cs` and `ZephyrDbContextModelSnapshot.cs`: `app_user` table. The down migration drops it.
- `Program.cs`: `AddZephyrAuth()` / `UseZephyrAuth()`, `/health` anonymous, default-deny fallback policy (authenticated).
- `src/frontend/vite.config.ts`: dev proxy for `/api`, `/auth` and `/signin-oidc-*`.
- `.env.example`, `scripts/setup-env.mjs`: `ZEPHYR_AUTH__PROVIDERS__n__*` and `ZEPHYR_BOOTSTRAP_ADMIN_*` variables.
- Tests: `Zephyr.Api.Tests/Auth/OidcSignInTests.cs`, `tests/auth_env.test.mjs`, `scripts/stack.test.mjs`.

## How it works
- **AC 1 Sign-in:** the user signs in through the provider (authorisation code + PKCE) and is redirected back. `app_user` has no password or secret column.
- **AC 2 Unverified email:** first sign-in links an existing user by email only when the provider has `EmailTrusted=true` and the claim `email_verified=true`. Otherwise access is refused.
- **AC 3 Unknown user:** a valid provider user with no matching `app_user` row is refused (403 at `/auth/denied`). No row is created.
- **AC 4 Bootstrap:** while no active admin exists, the subject set in `ZEPHYR_BOOTSTRAP_ADMIN_ISSUER` / `ZEPHYR_BOOTSTRAP_ADMIN_SUBJECT` is created as an active Admin. A Warning `bootstrap admin used` is logged and an `audit_log` row `bootstrap_admin_used` is written. Once an admin exists, the variables are ignored.
- **AC 5 Second realm:** a second provider (index 1, realm `zephyr-b`) is configured only through environment values.
- **AC 6 CSRF:** cookie-authenticated POST, PUT, PATCH and DELETE to `/api/v1/*` without `X-Zephyr-Csrf: 1`, or with a foreign `Origin`, return 403 `CSRF_REJECTED` and change nothing. GET and bearer-token calls are not affected.
- **AC 7 No CORS:** no `Access-Control-Allow-*` headers are sent. The SPA reaches the API through the Vite proxy.
- **AC 8 Anonymous set:** only `/health`, `/auth/login` and `/auth/denied` answer without a session. Everything else returns 401.

## How to test
**Checks:** `npm run typecheck --silent && npm run lint --silent && npm run test --silent && npm run build --silent`

DB tests need `ZEPHYR_TEST_DB` (Compose db). Keycloak tests also need `ZEPHYR_TEST_KEYCLOAK=http://localhost:8180` and the `.env` values loaded. CI sets `ZEPHYR_REQUIRE_DB=1`.

- **AC 1:** sign in as salesperson@example.com at `/auth/login?provider=zephyr`. **Verify:** `/auth/me` returns 200.
- **AC 2:** set `ZEPHYR_AUTH__PROVIDERS__0__EMAILTRUSTED=false` and sign in as an unlinked user. **Verify:** 403 at `/auth/denied`; issuer and subject stay null.
- **AC 3:** sign in as a Keycloak user with no `app_user` row. **Verify:** 403 and no row created.
- **AC 4:** with no admin, sign in as admin@example.com. **Verify:** Admin role, Warning logged, `audit_log` row `bootstrap_admin_used` exists.
- **AC 5:** enable provider 1 in `.env` and sign in at `/auth/login?provider=zephyr-b`. **Verify:** sign-in works with no code change.
- **AC 6:** POST with a session cookie and no header, or with a foreign `Origin`. **Verify:** 403 `CSRF_REJECTED`; with the header and same origin, the normal result.
- **AC 7:** `curl -X OPTIONS -H "Origin: https://evil.example" ...`. **Verify:** no `Access-Control-Allow-*` headers.
- **AC 8:** call `/api/v1/*` and `/auth/me` without a session. **Verify:** 401.

## Deployment notes
- **Pre-deploy:** run `node scripts/setup-env.mjs` to create `.env` with a random provider secret equal to `OIDC_CLIENT_SECRET`.
- **Second realm (optional):** uncomment the provider 1 block in `.env` and copy `OIDC_CLIENT_SECRET` into its CLIENTSECRET.
- **Migration:** `0003_AppUser` creates `app_user`.
- **Run:** API on the host (`dotnet run`) while the authority is `http://localhost:8180`.
- **Configuration:** `ZEPHYR_AUTH__PROVIDERS__n__NAME / AUTHORITY / CLIENTID / CLIENTSECRET / EMAILTRUSTED`; `ZEPHYR_BOOTSTRAP_ADMIN_ISSUER / SUBJECT` (optional).

## Known limitations
- Bootstrap does not apply if the bootstrap email already exists as a pre-provisioned user (the user is matched by email instead; see matching rules).
- The unique (issuer, subject) index ignores `deleted_at`, so a soft-deleted user keeps its link.
- Keycloak and DB-backed tests were skipped locally. Their first real run is in CI with `ZEPHYR_REQUIRE_DB=1`; this run must be watched.
- Entra, password storage and social login are out of scope.
