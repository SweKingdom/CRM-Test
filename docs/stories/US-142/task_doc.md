# US-142: Personal API tokens
**Status:** Ready for UAT · **Category:** security · **Points:** 3 · **Sprint:** not recorded

## Summary
Users can now create personal API tokens so that scripts and their AI assistant can act as them. A token has the owner's permissions, expires after 90 days by default (at most 365), and can be revoked at any time. The plaintext token is shown once; only a SHA-256 hash is stored.

## What was built
- `ApiTokens.cs`: token generation and hashing; `POST /api/v1/me/tokens` (create), `GET /api/v1/me/tokens` (list), `DELETE /api/v1/me/tokens/{id}` (revoke); `ApiTokenHandler` (auth scheme `ApiToken`); scheme selector.
- `AuthModule.cs`: default scheme `Zephyr`, a policy scheme that sends `Authorization: Bearer zph_…` to `ApiToken` and every other request to the session cookie.
- `ZephyrDbContext.cs`: `ApiToken` entity and mapping (table `api_token`).
- `Migrations/0004_ApiToken.cs` and `ZephyrDbContextModelSnapshot.cs`: creates `api_token`; the down migration drops it.
- `Problems.cs`: error codes `OUT_OF_RANGE` and `TOKEN_LIMIT`.
- `Program.cs`: `MapApiTokenEndpoints()`.
- `ApiTokenTests.cs`: tests for AC 1–5, including a Respawn regression test (`api_token` and `app_user` reset together).
- `docs/api-tokens.md`: usage, storage, secret-scanning pattern.

## How it works
- **AC 1 Create:** the token is `zph_` followed by 43 base64url characters (32 random bytes). The create response returns the plaintext once with `Cache-Control: no-store`. Reads return `prefix` and `expiresAt` only.
- **AC 2 Limit:** a creation is serialised per user with a PostgreSQL advisory transaction lock. Active tokens are those not expired and not revoked. The 11th active token returns 422 `TOKEN_LIMIT`. Expired or revoked tokens do not count.
- **AC 3 Expiry:** `expiresInDays` must be an integer from 1 to 365; otherwise 422 `OUT_OF_RANGE` (field `expiresInDays`). Omitted, it defaults to 90 days. A token past `expiresAt` gets 401 with `WWW-Authenticate: Bearer`.
- **AC 4 Revoke:** `DELETE` returns 204. Every bearer token is looked up in the database on each request, so a revoked token is refused immediately.
- **AC 5 Storage:** `api_token` stores `token_hash` (SHA-256, 64 hex characters), `hash_algorithm` (`sha256`, default) and `prefix`. The plaintext is never stored. Hashes are compared with `CryptographicOperations.FixedTimeEquals` after the indexed lookup. A row is accepted only when its `hash_algorithm` equals the current algorithm (`ApiTokens.HashAlgorithm`).
- **Authentication:** a token whose owner is deactivated or deleted is refused with 401. A create or revoke call made with a token (not a session cookie) gets 403, so tokens cannot mint other tokens.
- **Audit:** create and revoke write `audit_log` rows (`api_token_created`, `api_token_revoked`) containing the prefix only.
- **Last use:** `last_used_at` is updated with a direct UPDATE (no `updated_at` change) at most once per minute.

## How to test
Run the project checks: `npm run typecheck --silent`, `npm run lint --silent`, `npm run test --silent`, `npm run build --silent`.

DB-backed tests need `ZEPHYR_TEST_DB` (CI sets `ZEPHYR_REQUIRE_DB=1`): `dotnet test src/backend/Zephyr.Api.Tests --filter ApiTokenTests`.

- **AC 1 Create:** `POST /api/v1/me/tokens {"name":"test"}` returns 201 with `token`. **Verify:** `GET /api/v1/me/tokens` shows `prefix` and `expiresAt` but no `token`.
- **AC 2 Limit:** create 10 tokens, then an 11th. **Verify:** 422 `TOKEN_LIMIT`; revoking one lets a new token be created.
- **AC 3 Expiry:** `{"name":"x","expiresInDays":366}` → **Verify:** 422 `OUT_OF_RANGE`. Without `expiresInDays`, `expiresAt` is 90 days ahead. A token past `expiresAt` gets 401 with `WWW-Authenticate: Bearer`.
- **AC 4 Revoke:** `DELETE /api/v1/me/tokens/{id}` → 204. **Verify:** `curl -H "Authorization: Bearer zph_…" http://localhost:8080/auth/me` returns 401.
- **AC 5 Storage:** `select token_hash, hash_algorithm, prefix from api_token;` → **Verify:** `token_hash` is 64 hex characters equal to `sha256(token)`, `hash_algorithm` = `sha256`, and no column holds the plaintext.

## Deployment notes
**Pre-deploy:** none.

**Migration:** `0004_ApiToken` creates table `api_token` with a foreign key to `app_user`. It must run before the new endpoints are used.

**Post-deploy (manual step, repository admin):** add a GitHub custom secret-scanning pattern for the `zph_` prefix. Repository Settings → Advanced Security → Secret scanning → Custom patterns, name `Zephyr personal API token`, regex `zph_[A-Za-z0-9_-]{43}`. Steps are in `docs/api-tokens.md`. This cannot be done from the repository.

**Configuration:** none new.

## Known limitations
- Scoped (reduced-permission) tokens are out of scope; every token has the owner's full permissions.
- Changing the hash algorithm is not supported in place. Rows whose `hash_algorithm` is not the current one are refused, so existing tokens stop working. A dual-algorithm lookup must be added before any such change.
- The DB-backed C# tests were not run locally (no PostgreSQL). Their first real run is in CI with `ZEPHYR_REQUIRE_DB=1`.
- The GitHub secret-scanning pattern is not verified by the repository checks; it is a post-deploy manual step.
