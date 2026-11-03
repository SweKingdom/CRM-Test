# Changelog

## Unreleased

- 2026-11-03 — US-131 Synthetic volume seed (~56,000 records, ~105,000 time entries): `npm run seed -- --volume` loads synthetic test data with a reset guard and skips tables that do not exist yet.
- 2026-11-03 — US-137 README "Run Zephyr locally", BUILD.md sections and local-edition ADR: added the README run section, BUILD.md with the FT-108 section, the feature-section template, ADR-013 (Proposed) and the ADR approval index.
- 2026-11-03 — US-132 scripts/check quality gate and GitHub Actions: one local check script (`scripts/check` and `scripts/check.ps1`) runs eight steps in fixed order with a summary and exit 1 on failure; the PR workflow runs the same script with a Postgres service and no deployment.
- 2026-11-03 — US-140 OIDC sign-in with user matching and bootstrap admin: sign-in through the identity provider (PKCE), pre-provisioned users only, bootstrap admin, CSRF header check, same-origin SPA with no CORS.
- 2026-11-02 — US-133 API core conventions: paging, filter, sort, errors, ETag: `/api/v1` conventions for cursor paging (50 default, max 200), `filter`/`sort`, RFC 9457 problem responses, ETag with `If-Match`/`If-None-Match`, and audit user from the `zephyr_user_id` claim.
- 2026-11-02 — US-130 Reference data seeds and validate_seeds.py: reference tables, idempotent seeds (`npm run seed`), three demo accounts, and seed file validator added.
- 2026-11-02 — US-127 Docker Compose stack, .env and health endpoint: local stack (db, api, web, keycloak, mailpit) with `.env` setup script, `/health` endpoint and JSON logs. First real `docker compose up` still pending.
- 2026-11-02 — US-128 Keycloak realm file with seven test users and a second realm: committed realm templates for `zephyr` (seven users) and `zephyr-b`, passwords rendered from `.env`, and a render script.
