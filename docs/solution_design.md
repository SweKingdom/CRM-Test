# Solution design — Zephyr CRM, local edition

Scope of this version: FT-108 Local stack, seeds and quality gate (US-127…US-132). Domain features will extend it. The repository is greenfield (only docs and `.gitignore`).

## Architecture overview
Headless .NET API (`src/backend/Zephyr.Api`) + web frontend (`src/frontend`, framework not yet confirmed) + PostgreSQL 16, all on one machine via Docker Compose. Keycloak replaces Entra (OIDC code + PKCE); Mailpit catches SMTP. One OCI image per app, configured only by env vars, logs to stdout, `/health`. No cloud SDK, no paid service.

## Module map
| Path | Purpose |
|---|---|
| docker-compose.yml, .env.example | Stack and configuration (US-127) |
| src/backend/Zephyr.Api | API, EF Core data layer, migrations (US-129) |
| src/backend/Zephyr.Api.Tests | Integration tests against Compose DB |
| src/frontend | Web UI |
| infra/keycloak | Realm templates; rendered to gitignored `generated/` (US-128) |
| Seed/*.sql, scripts/seed.mjs, scripts/validate_seeds.py | Reference and demo seeds (US-130) |
| tools/search-benchmark/generate.sql | Deterministic volume seed (US-131) |
| scripts/check(.ps1), .github/workflows/check.yml | Quality gate (US-132) |

## Data model (baseline)
Conventions on every table: `id uuid`, `created_at`, `updated_at`, `created_by`, `updated_by`, `deleted_at`, `custom jsonb`. Money: `amount numeric(18,2)`, `currency char(3)`, `amount_sek`, `rate_date`. Tables: `external_id` (unique system+entity_type+value), `audit_log` (append-only). Extensions: `pg_trgm`, `unaccent`. Reference tables (stages, call outcomes, time types, countries, currencies, MEDDPICC) are seeded by `Seed/*.sql` with `ON CONFLICT DO NOTHING`.

## API inventory
| Route | Notes |
|---|---|
| GET /health | Anonymous; 200 when API and DB reachable, else 503 |
Errors follow RFC 9457 (defined in the API-conventions story).

## Security model
Sign-in via Keycloak issuer+subject; Zephyr stores no passwords. Secrets only in gitignored `.env`; realm files are templates with placeholders; ports bound to 127.0.0.1; CI has read-only permissions and no secrets.

## Integration inventory
| System | Use |
|---|---|
| Keycloak :8180 | Realms `zephyr` (7 users, fixed admin subject) and `zephyr-b` (issuer switch test) |
| Mailpit SMTP 1025 / UI 8025 | Local mail sink |
| PostgreSQL :5432 | Data |

## Key decisions
| Decision | Reason | Alternatives |
|---|---|---|
| EF Core migrations, applied at API start | Default for .NET, has down migrations | DbUp, Flyway |
| Seeds as SQL run by `scripts/seed.mjs` | Idempotent via ON CONFLICT, works from host and CI | EF HasData, app-startup seeding |
| Realm templates rendered by script | Keycloak import does not substitute env into JSON reliably; keeps passwords out of git | Committed passwords, Admin API calls after start |
| Deterministic volume seed in SQL (`setseed`, md5-derived UUIDs) | Repeatable, fast set-based load | App-level generator |
| `scripts/check` runs all steps, SKIPs inapplicable ones visibly | Gate usable before all stories exist | Fail on missing parts |

## Risks
| Risk | L/I | Mitigation |
|---|---|---|
| Seed lists (call outcomes, time types, countries, currencies) not itemised in the brief | M/M | Take from requirements_brief; ask PO if missing |
| Frontend framework unconfirmed | M/L | Framework-neutral Dockerfile |
| Volume seed depends on domain tables not yet built | H/M | Script skips missing sets with a warning |
| Podman/Rancher differences | L/M | Plain Compose features only |
| Windows developers | M/L | PowerShell twin of scripts |
| No checks configured yet | certain | US-132 introduces them |

## Open questions
`.env` creation: by hand or `scripts/init-env` (PO). Run import/benchmarks: script by hand (PO to confirm).
