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

Reference data and seeds (US-130)

Volume seed (US-131)

- Entry point: `npm run seed -- --volume [--seed N] [--anchor YYYY-MM-DD] [--reset]` (`scripts/seed.mjs`).
- SQL generators: `tools/search-benchmark/generate/01_account.sql` to `06_time_entry.sql`, with shared pg_temp helpers in `_helpers.sql`. Parameters arrive as transaction settings: `zephyr.seed`, `zephyr.anchor`, `zephyr.data`, `zephyr.targets`.
- Targets: `generate/targets.json` (56,000 searchable records; 105,000 time entries; 18,495 in the trailing 12 months).
- Input lists: `tools/search-benchmark/data/` (`names.json`, `cities.json`, `typos.json`, `time_types.json`).
- Determinism: each value is `md5(seed || entity || n)`. No `setseed`/`random()`.
- Loader behaviour: one transaction per entity; missing tables are skipped with a warning; ANALYZE after load; a second run without `--reset` is refused; `--reset` deletes only rows where `custom->>'synthetic' = 'true'`.
- Marker: every generated row has `custom->>'synthetic' = 'true'`. Typo rows store the original spelling in `custom->>'typo_of'`.
- Time entries: first 18,495 spread over [anchor − 12 months, anchor]; the remainder over the 1,460 days before that window. Columns filled: `project_id`, `work_date`, `duration_minutes`, `notes`, `user_id`, `time_type`.
- Assumed schema (owned by later stories or US-150): `time_entry.time_type` holds `time_type.key`; `time_entry.user_id` is a plain uuid with no foreign key.
- Tests: `tests/volume.test.mjs` (targets, file layout, columns, keys, option validation); `VolumeSeedTests.cs` (DB-backed AC 1 to AC 3; checksum per table, extended as tables are created). DB tests need `ZEPHYR_TEST_DB` and run in CI with `ZEPHYR_REQUIRE_DB=1`.

## API conventions (US-133)

All endpoints live under `/api/v1`, use plural camelCase nouns, and are wired with `services.AddApiConventions()` and `app.UseApiConventions()`.

**Paging.** `limit` (default 50, max 200) and `cursor`. Responses are `{items, nextCursor}`; `nextCursor` is null on the last page. Keyset paging on the sort keys plus `id`. The cursor holds the sort signature; a foreign or malformed cursor returns 400.

**Filter and sort.** `filter[field][op]=value` and `sort=-field`. Operators: `eq`, `ne`, `in` (any allow-listed field); `gt`, `gte`, `lt`, `lte` (numbers and dates); `contains` (text, case-insensitive, wildcards literal). Sort keys must be non-nullable. Unknown names return 400.

**Errors.** RFC 9457 `application/problem+json` with `type` (`urn:zephyr:problem:<code>` or `about:blank`), `title`, `status`, `detail`, `traceId`. Validation returns 422 with `errors[]` of `{field, code, message}`. Malformed JSON returns 400. Unhandled errors return 500 without a stack trace.

**Concurrency.** Strong ETag `"<base64url sha256(id + updated_at ticks)>"`. Single-resource GET returns `ETag` and honours `If-None-Match` (304). PATCH requires `If-Match`: missing returns 428, stale returns 412. `updated_at` is the EF concurrency token. Timestamps are truncated to microseconds so the ETag after a save matches the next read.

**Write hooks.** POST, PUT and PATCH endpoints end with `.WithWriteHooks()`. `IWriteValidator` (no-op default) is the seam for FT-113 to FT-125; FT-125 registers the metadata validator. A test fails for any untagged write endpoint.

**Audit user.** `AuditInterceptor` stamps `created_by` and `updated_by` from the `zephyr_user_id` claim (`HttpCurrentUser`). Missing, anonymous or non-GUID claims map to `SystemUser`. The IdP `sub` claim is ignored. US-140 (FT-111) sets `zephyr_user_id` at sign-in; US-142 personal API tokens set the same claim.

**Business dates.** `BusinessClock` works in Europe/Stockholm. A period is `yyyy-MM`; `LockInstant(period)` is 00:00 Stockholm on the 3rd of the next month. Linux images need tzdata or ICU.

**Data.** No schema change for US-133; the migration snapshot is unchanged.

## Authentication and sign-in (US-140)

**Flow:** OpenID Connect authorisation code with PKCE, via the standard ASP.NET Core OIDC handler. One OIDC scheme per provider, callback `/signin-oidc-{name}`. Session is a cookie (HttpOnly, SameSite=Lax, Secure when not localhost). `SaveTokens=false`, `MapInboundClaims=false`. Scopes: openid, profile, email.

**Endpoints**

| Route | Access | Notes |
|---|---|---|
| GET /auth/login?provider=&returnUrl= | Anonymous | Challenge. `returnUrl` must be a local relative path, otherwise 400. |
| POST /auth/logout | Session | Clears cookie, 204. |
| GET /auth/me | Session | 200 `{id,name,email,roles,homeCountry}`; 401 anonymous. |
| GET /auth/denied | Anonymous | 403 problem+json (RFC 9457). |
| GET /health | Anonymous | Health check. |
| PATCH /api/v1/users/{id} | Admin | Body `{"active": bool}`. Requires `If-Match` and `X-Zephyr-Csrf: 1`. 403 non-admin, 428/412 ETag, 404 unknown, 422 `NOT_PATCHABLE` / `LAST_ADMIN`. Same value returns 200 with no audit row. |

All other `/api/v1/*` routes require an authenticated user (default-deny fallback policy).

**User matching (OnTokenValidated)**
1. Match on (issuer, subject), not deleted. Found: refuse if `active=false`, else sign in.
2. Not found, no active admin exists, and (issuer, subject) equal the bootstrap values: create an active Admin, log Warning and write `audit_log` `bootstrap_admin_used`.
3. Not found, provider `EmailTrusted=true`, claim `email_verified=true`, and an active user with that email and null issuer/subject exists: link and sign in. An email already linked to another subject is refused.
4. Otherwise refuse (`context.Fail`, redirect to `/auth/denied`) and log the reason. Tokens are never logged.

Session claims: `sub` = `app_user.id`, roles, issuer, `issued_utc` (stamped at sign-in). `last_login_at` is updated on sign-in.

**Session limits and deactivation recheck (US-141)**
- Code: `Auth/SessionRules.cs` (`SessionSettings`, `IUserStatus`/`UserStatus`, `SessionRules`), wired in `Auth/AuthModule.cs`.
- Idle limit: cookie `ExpireTimeSpan` = `SESSION_IDLE_SECONDS` (default 14400). `OnValidatePrincipal` sets `ShouldRenew = true` on every valid request, so the idle window counts from the last request.
- Absolute limit: `SESSION_ABSOLUTE_SECONDS` (default 43200) measured from `issued_utc`. Past it, the principal is rejected and the cookie is cleared. A cookie without `issued_utc` is refused.
- Clock: cookie options and session rules use the injected `TimeProvider`; tests use `FakeTimeProvider`.
- Deactivation recheck: `IUserStatus.IsActiveAsync` reads `app_user.active` and caches `{Active, CheckedAt}` in `IMemoryCache` for `USER_STATUS_RECHECK_SECONDS` (default 300). Active and inactive answers are both cached. `Forget(id)` evicts the entry; `PATCH /api/v1/users/{id}` calls it.
- API tokens (US-142) must call `IUserStatus.IsActiveAsync` for the token's user. Not yet built.
- Deactivating an admin takes a transaction-scoped advisory lock to prevent two admins deactivating each other concurrently.
- Migrations: none for US-141.

**Data model: `app_user`** (plus baseline columns)

| Column | Type | Notes |
|---|---|---|
| name | text not null | |
| email | text not null | Unique index on lower(email) where deleted_at is null. |
| issuer | text null | Null until linked. |
| subject | text null | Unique (issuer, subject) where both not null. |
| home_country | char(2) null | FK countries. |
| roles | text[] not null, default '{}' | Values: Salesperson, BDR, PM, Consultant, Finance, Management, Admin. |
| active | bool not null, default true | |
| last_login_at | timestamptz null | UTC. |

No password or secret column exists. Users are pre-provisioned; there is no self sign-up.

**Configuration**

| Variable | Notes |
|---|---|
| ZEPHYR_AUTH__PROVIDERS__n__NAME / AUTHORITY / CLIENTID / CLIENTSECRET / EMAILTRUSTED | Per provider. Index 0 = `zephyr`, index 1 = `zephyr-b`. Providers only from configuration. |
| ZEPHYR_BOOTSTRAP_ADMIN_ISSUER / ZEPHYR_BOOTSTRAP_ADMIN_SUBJECT | Optional. Used only while no active admin exists. |
| SESSION_IDLE_SECONDS | Optional. Idle session limit, default 14400 (4 h). |
| SESSION_ABSOLUTE_SECONDS | Optional. Maximum session age from sign-in, default 43200 (12 h). |
| USER_STATUS_RECHECK_SECONDS | Optional. Cache window for the active-user check, default 300 (5 min). |

Secrets live only in the gitignored `.env`. Provider 1 `CLIENTSECRET` must equal `OIDC_CLIENT_SECRET`.

**Deployment note (US-141):** users with a cookie from before this release must sign in once more (no `issued_utc`).

## Quality gate (US-132)

The quality gate is one script, `scripts/check` (Bash) with `scripts/check.ps1` (Windows) as its equivalent. Developers run it locally; GitHub Actions runs `bash scripts/check` on every pull request. Nothing is deployed.

| # | Step | Command | Missing target |
|---|---|---|---|
| 1 | lint | `dotnet build -warnaserror`, `dotnet format --verify-no-changes`, frontend `npm run lint && npm run typecheck` | — |
| 2 | tests | `dotnet test` against the test database | — |
| 3 | seeds | `python3 scripts/validate_seeds.py` | — |
| 4 | openapi-regenerate | `node tools/openapi-from-metadata/generate.mjs docs/api/openapi.json`; fails if git diff | SKIP (not yet applicable) |
| 5 | spectral | `npx --yes @stoplight/spectral-cli@6.15.0` on `docs/api/openapi.json` (`.spectral.yaml` extends `spectral:oas`) | SKIP (not yet applicable) |
| 6 | oasdiff | `oasdiff breaking` against the copy on `origin/main` (or `main`) | SKIP (not yet applicable) |
| 7 | schemathesis | Schemathesis 4 against the local API | SKIP (not yet applicable) |
| 8 | vulnerabilities | `dotnet list package --vulnerable --include-transitive`; `npm audit --audit-level=high` | — |

**Behaviour**
- Each step prints `PASS`, `FAIL` or `SKIP` and a `=== Summary` table. All steps run even after a failure. Exit code 1 if any step fails.
- A missing target is a SKIP, never a silent PASS. With `REQUIRE_ALL=1` (set in CI), a SKIP for a built feature fails the run.
- Steps needing Keycloak print `SKIP (needs Keycloak)` in CI. Run them locally with `docker compose up -d keycloak`.
- Locally the script starts the Compose `db` service and waits for it to be healthy, and leaves services running. In CI (`CI=true`) it does not call Compose for `db`.

**Database for tests**
- Tests read `ZEPHYR_TEST_DB` only, in Npgsql key=value form. `DATABASE_URL` is not read.
- Each run uses its own database with Respawn for isolation.

**CI workflow** (`.github/workflows/check.yml`)
- Trigger: `on: pull_request` only. No `pull_request_target`.
- Permissions: `contents: read`. No secrets, no login, no push.
- Runner: ubuntu-latest, with .NET, Node and Python set up.
- Service: Postgres 16 as a `services:` container, health-checked on port 5432. Credentials come from the `.env.example` values.
- Before the check, the workflow copies `.env.example` to `.env`.

**Pinned tools**
- Spectral: `@stoplight/spectral-cli@6.15.0`, held in one constant in the scripts. A test checks the pin. Not yet an exact devDependency (see PO answer #123).
- Later steps (oasdiff, Schemathesis 4) must be installed in the workflow when their steps become active (US-136).

**Tests:** `tests/check.test.mjs` runs with `npm test`. The bash-driven tests are skipped on native Windows.

## Documentation and decision records (US-137)

- `README.md` section "Run Zephyr locally": ordered commands from clone to signed-in seeded system, with URLs, seeded test user name and stop/reset. No secrets; `.env.example` holds placeholders only.
- `BUILD.md`: build log, one `## F{nn} — {title} (FT-code)` section per merged feature. Each section has the six parts of `build/prompts/feature-section.md` and a `Reviewed by:` line (second developer).
- `build/prompts/feature-section.md`: single source for the six part names.
- `docs/adr/`: ADR-013 (local edition) is Proposed. It defers ADR-001 and ADR-002 without replacing them. `docs/adr/README.md` is the approval index for ADR-004, 006, 007, 008, 009, 012 and 013. Status changes to Accepted only in the approving PR, with approver and approval PR recorded.
- `.github/pull_request_template.md`: checklist line for the BUILD.md section and second-developer review.
- `tests/docs.test.mjs`: checks the README order, BUILD.md headings and ADR index.

## Money and exchange rates (US-134)

**Money value.** `MoneyInput.Read` accepts only `amount` (string, at most 2 decimals, at most 16 integer digits) and `currency` (3 capitals). `amountSEK` and `rateDate` are ignored on input. The output `Money` is written by `MoneyJsonConverter` as `{amount, currency, amountSEK, rateDate}`.

**Stamping.** Domain stories call `ExchangeRates.StampAsync(db, input, businessDate)` at write time and store amount, currency, amount_sek and rate_date. The rate month is the first day of the business date's month. SEK uses rate 1 and the same rateDate. `amount_sek = Math.Round(amount × rate, 2, MidpointRounding.AwayFromZero)` on `decimal`. No rate gives 422 `MISSING_EXCHANGE_RATE` with no fallback. Stamped values are not recomputed.

**Data model.** Table `exchange_rate`:

| Column | Type | Notes |
|---|---|---|
| currency | char(3) | FK to `currency(iso3)` |
| month | date | First day of the month |
| rate_to_sek | numeric(18,6) | Greater than 0 |

Check constraints: currency <> 'SEK'; month is the first day of a month; rate_to_sek > 0. Unique index `ux_exchange_rate_currency_month`. Migration `0003_AddExchangeRate` (down drops the table). `Conventions` in `0002_ReferenceData` is internal for reuse.

**API.**

| Route | Role | Notes |
|---|---|---|
| `GET /api/v1/exchange-rates` | Signed-in user | List conventions (US-133); filter and sort on `currency`, `month`, `updatedAt` |
| `PUT /api/v1/exchange-rates/{currency}/{yyyy-MM}` | Admin | Body `{rateToSek}` (number or string). 201 created, 200 changed, 422 for SEK, unknown or inactive currency, bad month or bad rate; 401 anonymous; 403 not admin |

**Authorisation.** The admin check is `ExchangeRates.DeniedStatus`, which tests the `Admin` role claim (`ClaimTypes.Role`). It is interim until the permission engine (US-144, FT-112) replaces it. `AppUser.AdminRole` and `ExchangeRates.AdminRole` both hold `"Admin"` and should be unified then.

**Seed.** `Seed/0003_exchange_rates.sql` loads EUR 11, USD 10, NOK 0.95, AED 2.72 and GBP 12.80 for every month from 2020-01 to 2030-12 (`ON CONFLICT DO NOTHING`). 660 rows in total. Re-running the seed keeps edited rates.

**Deployment.** The API applies migration `0003_AddExchangeRate` at start. Then run `node scripts/seed.mjs`.

## Upsert by external ID and soft delete (US-135)

Integrations write idempotently through `PUT /api/v1/{plural}/by-external-id/{system}/{value}`. Only `accounts` is registered so far.

- **Upsert key:** `(system, entity_type, value)` in table `external_id`. `system` is `dynamics` or `fortnox`; `value` is at most 200 characters.
- **Flow:** role check (403) → body read (400) → `WriteHooks` (422) → field validation via `EntityBinding` (422). Nothing is written before validation passes.
- **Write:** one transaction. `INSERT … ON CONFLICT (system, entity_type, value) DO NOTHING` with a pre-generated `entity_id`. 1 row inserted → entity inserted, 201 with `Location` and `ETag`. 0 rows → `SELECT … FOR UPDATE` on the mapping; soft-deleted → 409 `RECORD_DELETED` (rolled back); otherwise fields applied, 200. `If-Match` is ignored.
- **Soft delete:** `DELETE /api/v1/{plural}/{id}` → `Remove` plus `AuditInterceptor` sets `deleted_at`; 204. 404 when missing or already deleted. The global soft-delete filter hides deleted rows from reads and lists.
- **Authorisation (interim):** `InterimRoleGate` reads the roles of the caller's active `app_user`. Upsert: Admin or Integration. DELETE: Admin only. Marked `TODO(US-143/144)`; replace with `RequirePermission(entity, create|update|delete)` at scope `org`.
- **Code:** `Common/ExternalUpsert.cs`, `Permissions/EntityRegistry.cs`, `Permissions/InterimRoleGate.cs`, `Data/ZephyrDbContext.cs` (`Account` entity on table `account`), `Common/Problems.cs` (`FORBIDDEN`), `Program.cs` (route mapping).
- **Schema:** no migration. Tables `account` (0002) and `external_id` (0001) already exist.
- **Adding an entity:** one `EntityBinding<T>` in `EntityRegistry` plus two map calls in `Program.cs`.
- **Account body:** `name` required; `countryCode` optional, two capital letters; PUT replaces the record, so an absent `countryCode` clears it.

## Personal API tokens (US-142)

**Scope:** personal API tokens let scripts and AI assistants act as a user. Full detail: `docs/api-tokens.md` and the task document for US-142.

**Format and storage**
- Token: `zph_` + 43 base64url characters (32 random bytes). Shown once at creation.
- Table `api_token`: `id`, `user_id` (FK to `app_user`), `name`, `prefix`, `token_hash` (SHA-256 hex, 64 chars), `hash_algorithm` (text, not null, default `sha256`), `expires_at`, `revoked_at`, `last_used_at`. The plaintext is never stored.
- Migration `0004_ApiToken` creates the table; the down migration drops it.
- The current algorithm is `ApiTokens.HashAlgorithm`. A row is accepted only when its `hash_algorithm` equals it. Comparison is by `CryptographicOperations.FixedTimeEquals` after an indexed lookup on `token_hash`.

**Endpoints**

| Method and route | Input | Success | Errors |
|---|---|---|---|
| `POST /api/v1/me/tokens` | `name` (required, non-blank, bounded length), `expiresInDays` (optional integer, 1–365, default 90) | 201 with `token` (plaintext), `prefix`, `expiresAt`; `Cache-Control: no-store` | 422 `VALIDATION`; 422 `OUT_OF_RANGE` (field `expiresInDays`); 422 `TOKEN_LIMIT` (11th active token); 403 when called with a token instead of a session cookie |
| `GET /api/v1/me/tokens` | — | 200 list with `id`, `name`, `prefix`, `expiresAt`, `revokedAt`, `lastUsedAt`; never `token` | 401 |
| `DELETE /api/v1/me/tokens/{id}` | — | 204 | 404 for unknown or foreign id; 401 |

**Authentication**
- Default scheme `Zephyr` (policy scheme in `AuthModule.cs`): `Authorization: Bearer zph_…` goes to scheme `ApiToken` (`ApiTokenHandler`); everything else goes to the session cookie. Sign-in and sign-out stay on the cookie.
- Each bearer request is looked up in the database, so revocation takes effect immediately. Refused (401 with `WWW-Authenticate: Bearer`) when: expired, revoked, algorithm not current, owner deactivated or deleted.
- Principal is built with `UserMatching.ToPrincipal(user, scheme)`; the token carries the owner's permissions.
- `last_used_at` is updated with a direct UPDATE at most once per minute and does not change `updated_at`.

**Concurrency and audit**
- Creation takes a PostgreSQL advisory transaction lock keyed per user, so concurrent creates cannot exceed 10 active tokens.
- Create and revoke write `audit_log` rows: `api_token_created`, `api_token_revoked`, containing the prefix only. `audit_log` is in Respawn `TablesToIgnore`.

**Testing**
- `ApiTokenTests.cs` covers AC 1–5 and a Respawn regression test (`api_token` cleared together with `app_user`). DB-backed tests require `ZEPHYR_TEST_DB`; CI sets `ZEPHYR_REQUIRE_DB=1`.

**Operations**
- Post-deploy manual step: add the GitHub custom secret-scanning pattern `zph_[A-Za-z0-9_-]{43}` (see `docs/api-tokens.md`).
- Changing the hash algorithm is not transparent: rows with another `hash_algorithm` are refused. A dual-algorithm lookup is needed before such a change.

## Permission model (US-143)

Covers US-143 and US-144. Enforcement is in `Permissions/`.

- **Source of truth:** roles, home country and team come from `app_user` through `IUserStatus.GetAsync`, cached for `USER_STATUS_RECHECK_SECONDS` (default 300). Claims are not used for authorisation, so cookie and token sessions behave the same.
- **Scopes:** `own` (owner column = caller), `team` (owner in caller's team), `country` (country column = home country; never matches without one), `org` (all rows). The widest scope across the caller's roles applies. Values are bound as query parameters.
- **Columns:** `PermissionService.Columns` maps each entity to its owner and country columns. Today: `account` (owner `created_by`).
- **Endpoint declarations:** `RequirePermission(entity, action)` returns 401 when nobody is signed in and 403 when the scope is `none`. `RequireRoles(...)` is used for role-only routes: external upsert (Admin, Integration) and soft delete (Admin). `AsMeEndpoint()` marks routes for the caller only.
- **Single-record access:** `FindAsync` returns 404 when the caller cannot read the record and 403 when it can read but may not perform the action.
- **Default deny:** every endpoint must declare a permission, use `RequireRoles`, be `me`, or be on the anonymous allow-list in `AnonymousAllowList.cs`. A test enumerates endpoints and fails on any other.
- **UI parity:** `GET /api/v1/me/permissions` (`Cache-Control: no-store`) returns `{roles, permissions, routes}` from the same engine. The frontend route guard reads it once per page load and again after a 401 or 403.
- **Extension rule:** entity list, detail and search routes must use `PermissionService.Filter` and `FindAsync` and declare their columns in `PermissionService.Columns`. Project-based predicates for `project` and `time_entry` are added by US-145.
- **Limits:** field-level security is not implemented. Token tests use a stand-in handler until US-142's real token handler is merged; AC 5 and AC 7 must be re-run then.
- **Files:** `docs/permissions.md` (enforcement section).

## Claude usage logging (US-138)

- Usage source: the pre-commit hook in `tools/claude-usage/` writes one file per push and developer to `docs/claude-usage/{story}_{timestamp}_{developer}.md`. Front matter holds tokens, active minutes and models.
- Per-story totals: `node tools/claude-usage/cli.mjs report`.
- PR template: `.github/pull_request_template.md` has a `## Claude usage` section (Tokens in, Tokens out, Cost (USD)). The author pastes the story total from the report.
- No separate usage script or log format exists (PO #88). Tests in `tests/docs.test.mjs` cover the template section.
- `tools/claude-usage/` and `docs/claude-usage/` are not part of this workspace branch.

## Approvers and delegation (US-145)

**Tables** (migration `0005_Approvers`, SQL only; the EF model is unchanged)

| Table | Columns | Notes |
|---|---|---|
| `project_approver` | `project_id uuid not null`, `user_id` FK to `app_user`, `created_at`, `created_by`, PK (`project_id`, `user_id`) | No project FK until US-161. Hard delete, no soft delete. |
| `approval_delegation` | `delegator_id`, `delegate_id`, `from_date`, `to_date`, `revoked_at`, `deleted_at`, conventional audit columns | Check `ck_approval_delegation_dates` (`to_date >= from_date`), check `ck_approval_delegation_not_self` (`delegator_id <> delegate_id`), index on (`delegate_id`, `from_date`, `to_date`). |

**Endpoints** (`Permissions/Approvers.cs`, registered by `MapApprovers()` from `MapZephyrEndpoints()`)

| Route | Role | Result |
|---|---|---|
| `PUT /api/v1/projects/{id}/approvers/{userId}` | Admin | 204, idempotent; 404 for unknown user |
| `DELETE /api/v1/projects/{id}/approvers/{userId}` | Admin | 204, idempotent |
| `POST /api/v1/delegations` | Any role; Admin may pass `delegatorId` | 201; 422 or 403 as in the task document |
| `GET /api/v1/delegations` | Any role | Own delegations; Admin sees all |
| `DELETE /api/v1/delegations/{id}` | Delegator or Admin | 204; 404 if unknown, already revoked or not the caller's |

**Helpers** (DB-backed, never cached; `today` = `BusinessClock.Today()`)
- `ApproverScopeAsync(userId, today)`: the projects the user approves directly, plus the delegator's projects for active delegations.
- `IsApproverOfAsync(userId, projectId, today)`: true when the project is in that scope.
- `EnsureNotSelfApproval(entryUserId, callerId)`: throws a 403 problem when they match.

**Active delegation:** `revoked_at IS NULL AND deleted_at IS NULL AND today BETWEEN from_date AND to_date`. Delegation is not transitive.

**Audit:** `approver_added`, `approver_removed`, `delegation_created`, `delegation_revoked`.

**Deferred:** the `PermissionService` OR-predicate (US-144), the PM-of-project rule and project FK (US-161), and endpoint-level self-approval and time-entry 404 checks (US-166, US-170). Details in `docs/permissions.md`.

## API description (OpenAPI)

The API description is generated, committed and checked on every change (US-136).

| Item | Value |
|---|---|
| Generated file | `docs/api/openapi.json` (OpenAPI 3.1.0, `servers[0].url` = `/api/v1`) |
| Generator | `tools/openapi-from-metadata/generate.mjs` (Node only, no dependencies); `[metadata.json] [--out file]` |
| Metadata source | `tools/openapi-from-metadata/field-metadata.json` (interim, hand-maintained; see US-182) |
| Output format | `JSON.stringify`, sorted keys, 2-space indent, LF. No timestamps. |
| Lint | Spectral 6.15.0 with `.spectral.yaml` (`SPECTRAL_VERSION`) |
| Breaking-change check | oasdiff 1.11.7 (`OASDIFF_VERSION`), base `origin/main:docs/api/openapi.json` |
| Contract test | `OpenApiContractTests` (registered routes must equal documented routes) |

Rules:
- Never edit `docs/api/openapi.json` by hand. Change the metadata and run `node tools/openapi-from-metadata/generate.mjs`.
- Shared components: `ProblemDetails` (errors, `application/problem+json`), `Page` (list endpoints), `Money` (money fields), parameters `Limit`, `Cursor`, `Filter`, `Sort`, `IfMatch` (`If-Match`), header `ETag`.
- Breaking changes (removed field, narrowed type, new required property) fail the oasdiff step unless a deliberate break is approved via PR label and an `oasdiff` changelog note.
- The `openapi-regenerate` step fails with "run generate" when the committed file differs from the generated output.

## Permission tests (US-146)

Backend tests in `Zephyr.Api.Tests/Permissions/MatrixCoverageTests.cs` check the permission matrix (`permission-matrix.csv`) against the stored data and the live API.

- **Setup:** the real app runs on the test database (`ZEPHYR_TEST_DB`) with a test-only auth handler (`X-Test-User`, `X-Test-Role`) and seven seeded role users.
- **Matrix cases:** one case per CSV row (665). Where a probe exists, `none` must return exactly 403; other scopes must not return 401, 403 or 5xx.
- **Probes:** account delete, app_user update, reference_data read, create and update. Probes never write data.
- **OutsideMatrix:** `/api/v1` routes that are not matrix cells, each with a reason (by-external-id PUT is an integration route, see US-135; `me/permissions`; approver and delegation routes).
- **Pending:** 16 entities without routes yet, mapped to their owning story. An entity is either probed or pending.
- **Meta-tests:** every route is probed or listed in OutsideMatrix; anonymous requests get 401 on every probed route.
- **Admin account rule:** Admin has `delete` = `org` on `account` (PO answer #156).
- **Gate:** the tests run in step `tests` of `scripts/check`. `scripts/check --ui` (`-Ui`) adds step `ui`, which fails until `src/frontend/e2e` exists.
- **CI:** set `ZEPHYR_REQUIRE_DB=1` so the database tests cannot be skipped.
- **Not yet built:** Playwright UI routes, time privacy, search leak tests (follow-up stories).

## Search (US-147)

**Migration:** `0004_SearchDocument` creates `f_unaccent`, `f_search_norm`, table `search_document` (GIN indexes `ix_search_document_tsv` and `ix_search_document_text_trgm`), `search_upsert`, and helper `search_install_trigger(table, type, title_expr, text_expr, [subtitle, owner_id, account_id, country_code, business_unit, hidden])`. Only the account trigger exists; each later entity story adds one helper call in its own migration with `p_hidden` (for contact and lead: `new.anonymised_at is not null`) and its trigger test.

**Sync:** triggers run in the same transaction as the change (insert, update, delete). Soft-deleted rows (`deleted_at`) are removed from the index; restoring them re-adds them.

**Normalisation:** `f_search_norm` = trim(punctuation→space(lower(f_unaccent(x)))), used for stored text, titles and queries.

**API:** `GET /api/v1/search?q=&types=&limit=`. `q` 2–100 characters, `limit` 1–50 (default 20), `types` CSV of account, contact, lead, opportunity, project. Response `{items:[{type,id,title,snippet,score}], fallback}`. Errors: 400 problem+json (bad parameters, repeated or unknown parameters), 401 without session, 403 without a matrix role that may read a searchable type.

**Read scope:** per type, `IPermissionService.Scope(user, type, "read")` gives the scope arrays `@org`, `@own`, `@team`, `@country`, applied as a WHERE filter before ranking. Account owner is `created_by`; account country is the contracting country. The route is declared with `RequireRoles` for all matrix roles. Integration clients get 403.

**Ranking:** full-text: exact title, title prefix, `ts_rank_cd` plus recent-activity boost, entity weight, `updated_at` desc. Trigram: `word_similarity` (threshold 0.3) desc, weight, `updated_at`. The trigram step runs only when full-text returns fewer than 5 items (`fallback=true`).

**Performance:** `tools/search-benchmark/bench.mjs` (`npm run bench:search`, optional `--explain`) runs 100 queries and reports p95. Seed volume ends with `REINDEX` and `ANALYZE` of `search_document`.

**Limitations:** the route is not in `docs/api/openapi.json` yet.

## App shell and global search (US-148)

**Frontend stack:** React, TypeScript, Vite, React Router, TanStack Query, plain CSS with variables. Playwright and axe-core for e2e and accessibility, vitest for unit tests. Served as static files by the API container. No UI-kit dependency.

**Modules**
| Path | Purpose |
|---|---|
| `src/frontend/src/main.tsx`, `App.tsx` | Router, QueryClient, routes |
| `src/frontend/src/permissions.tsx` | `SignedIn` gate (loading, sign-in redirect, failure, ready), `AccessDenied` |
| `src/frontend/src/shell/Layout.tsx` | Header, navigation (path + label array), phone menu, skip link |
| `src/frontend/src/shell/SearchBox.tsx` | Combobox, 250 ms debounce, grouped dropdown, live region |
| `src/frontend/src/pages/SearchResults.tsx` | `/search?q=` grouped results |
| `src/frontend/src/lib/api.ts` | Fetch wrapper, `credentials: 'same-origin'`, RFC 9457 `ApiError` |
| `src/frontend/src/lib/search.ts` | `useSearch`, grouping, record paths |
| `src/frontend/src/lib/format.ts` | `formatDate`, `formatDateTime` (Europe/Stockholm) |

**Authentication and permissions**
- No tokens are stored in localStorage. The session is a cookie (FT-111).
- Navigation is driven by `GET /api/v1/me/permissions` (the routes list from the US-144 permission engine). The frontend does not duplicate entity and action rules.
- A 401 from `GET /api/v1/me/permissions` redirects to `/auth/login`.

**Search**
- Calls `GET /api/v1/search`. Requests are cancelled when the input changes.
- Result links: `/accounts/{id}`, `/contacts/{id}`; lead, opportunity and project routes show a placeholder until those stories land.

**Dates and time zones**
- All dates and times go through `lib/format.ts`. Display uses Europe/Stockholm.
- Per-user time zone is an open decision (PO answer #80): a user time zone field, defaulting to Europe/Stockholm, is to be used for "today" decisions. Not implemented in US-148.

**Testing and gate**
- `scripts/check` runs the frontend `test` script (vitest) and the frontend `typecheck`.
- `npm run e2e` (or `scripts/check --ui`) runs the Playwright specs, including axe, outside the default gate (PO answer #163). It needs a Playwright browser. Follow-up: CI runs it on every PR.

**Deployment**
- `src/frontend/package-lock.json` must be generated (`npm install --prefix src/frontend`) and committed.
- `npx playwright install chromium` once for e2e.
- Optional `ZEPHYR_E2E_USER` and `ZEPHYR_E2E_PASSWORD` enable the Keycloak smoke test.

## Activities (US-151)

Activities record calls, meetings, notes, tasks and emails. One `activity` table holds all types; `activity_participant` holds the from, to, cc and attendee participants.

| Item | Detail |
|---|---|
| Routes | `/api/v1/activities`: POST, GET list (keyset paging, default 25, max 100, newest first), GET `/{id}`, PATCH, DELETE (soft) |
| Filters on list | `regardingType`, `regardingId`, `accountId`, `type`, `cursor`, `limit` |
| Regarding types | `lead`, `contact`, `account`, `opportunity`, `project`, `service_request` (type plus ID, no foreign key; validated in the API) |
| Types | `call`, `meeting`, `note`, `task`, `email` |
| Call outcomes | Table `call_outcome` (key, label, connected, sort), seeded in `Seed/0001_reference_data.sql`. Connected: `meeting_booked`, `not_interested`, `callback_requested`, `referred_to_colleague` = true; the other five = false |
| Source | `source` is set by the server from the caller (`ui`, `mcp`, `import`, `api`); clients cannot set it |
| Schema | EF migration `0005_Activity` (`Zephyr.Api/Data/Migrations/0005_Activity.cs`); applied at API start |
| Code | `Zephyr.Api/Activities/ActivityEndpoints.cs`, `ActivityValidation.cs`; `MapActivityEndpoints()` in `Endpoints.cs` |

**Server rules**
- `account_id` is derived from the regarding record and recomputed when the regarding changes. Clients cannot send `accountId` (422).
- `connected` comes from `call_outcome.connected` and is NULL for non-calls. Clients cannot send `connected`.
- Contact account change: `MoveContactActivitiesAsync(db, contactId, newAccountId, userId)` joins the caller's transaction and moves activities regarding that contact. Failure rolls back both changes. Audit rows are written in the same transaction.
- Errors use RFC 9457. 422 for invalid or unknown regarding, bad type, bad outcome key, outcome on a non-call, invalid participant. 404 for not visible. 403 for no permission.

**Known gaps**
- The OpenAPI document (`docs/api/openapi.json`) does not yet describe the activity routes. A follow-up story extends the generator (product owner decision #167).
- Visibility predicates for lead, contact, opportunity, project and service request are not applied yet, because those tables do not exist. Each entity's story adds this check.
