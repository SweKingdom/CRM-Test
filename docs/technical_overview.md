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

All other `/api/v1/*` routes require an authenticated user (default-deny fallback policy).

**User matching (OnTokenValidated)**
1. Match on (issuer, subject), not deleted. Found: refuse if `active=false`, else sign in.
2. Not found, no active admin exists, and (issuer, subject) equal the bootstrap values: create an active Admin, log Warning and write `audit_log` `bootstrap_admin_used`.
3. Not found, provider `EmailTrusted=true`, claim `email_verified=true`, and an active user with that email and null issuer/subject exists: link and sign in. An email already linked to another subject is refused.
4. Otherwise refuse (`context.Fail`, redirect to `/auth/denied`) and log the reason. Tokens are never logged.

Session claims: `sub` = `app_user.id`, roles, issuer. `last_login_at` is updated on sign-in.

**CSRF:** cookie-authenticated POST, PUT, PATCH and DELETE to `/api/v1/*` require header `X-Zephyr-Csrf: 1` and a same-origin `Origin`. Otherwise 403 `CSRF_REJECTED`. Bearer-token calls are exempt. GET is unaffected.

**CORS:** none. The SPA is same-origin. The Vite dev server proxies `/api`, `/auth` and `/signin-oidc-*`.

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

Secrets live only in the gitignored `.env`. Provider 1 `CLIENTSECRET` must equal `OIDC_CLIENT_SECRET`.

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
