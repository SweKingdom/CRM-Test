# US-127: Docker Compose stack, .env and health endpoint
**Status:** Ready for UAT · **Category:** devops · **Points:** 3 · **Sprint:** —

## Summary
A developer can start the local Zephyr stack with one Compose file (`docker compose up`). The stack runs PostgreSQL 16, the API, the web app, Keycloak and Mailpit, all bound to localhost. Settings come from a `.env` file that is created from the committed `.env.example` and is never committed.

## What was built
- `docker-compose.yml`: services db, api, web, keycloak and mailpit; named volume `pgdata`; one network; published ports bound to `127.0.0.1`; healthchecks on every service; `env_file: .env`.
- `.env.example`: every variable with placeholder values (`change-me`). Includes `ZEPHYR_DB`, `SMTP_HOST`, `SMTP_PORT`, `OIDC_*`, `ZEPHYR_BOOTSTRAP_ADMIN_*`, `KC_BOOTSTRAP_ADMIN_*` and the seven `KC_USER_*_PASSWORD` variables.
- `scripts/setup-env.mjs`: copies `.env.example` to `.env` if `.env` is missing, generates random passwords, and never overwrites an existing `.env`.
- `scripts/render-realms.mjs`: renders the Keycloak realm; treats `change-me` as unfilled.
- `src/backend/Zephyr.Api/Program.cs`: `/health` endpoint (anonymous) with an Npgsql database check; JSON console logging to stdout; configuration from environment variables only.
- `src/backend/Zephyr.Api/Dockerfile` and `.dockerignore`: multi-stage .NET SDK build, ASP.NET runtime image, `ASPNETCORE_URLS=http://+:8080`, non-root user.
- `src/frontend/Dockerfile` and `.dockerignore`: multi-stage Node build; `npm ci` when `package-lock.json` exists, otherwise `npm install`; serves on port 3000.
- `src/frontend` skeleton: React, Vite and TypeScript.
- `.gitignore`: ignores `.env`, `generated/` and `infra/keycloak/generated/`.
- `scripts/stack.test.mjs` and `tests/realms.test.mjs`: configuration tests.
- `README.md`: "Run Zephyr locally" section.

## How it works
- **AC 1 Stack starts:** `docker compose up --build` starts db, api, web, keycloak and mailpit. The API waits for a healthy db. `GET /health` returns 200 `{"status":"Healthy"}` when the database answers `SELECT 1`, and 503 otherwise. *Not yet run in Docker; verified at the first real run.*
- **AC 2 Mail caught locally:** Mailpit listens for SMTP on 1025 and serves its UI on 8025. No relay is configured, so mail does not leave the machine. *Config checked only; real send deferred to the first real run.*
- **AC 3 Secrets not committed:** `.env` is gitignored. `.env.example` contains only `change-me` placeholders. Checked by `scripts/stack.test.mjs`.
- **AC 4 Portability:** API logs are JSON lines on stdout. The API reads all configuration from environment variables. No cloud-vendor SDK is referenced.
- **AC 5 Start-up time:** Moved to US-130 by the product owner. Not measured in this story.

## How to test
- Run `npm run typecheck && npm run lint && npm test && npm run build`.
- AC 1: run `node scripts/setup-env.mjs` then `docker compose up --build`. Check `docker compose ps` shows 5 healthy services and `curl -i http://localhost:8080/health` returns 200. Run `docker compose stop db` and confirm 503.
- AC 2: run `swaks --server localhost:1025 --to a@example.com --from b@example.com`, then open http://localhost:8025.
- AC 3: run `git check-ignore .env` (prints `.env`) and confirm every secret in `.env.example` is `change-me`.
- AC 4: run `docker compose logs api` (JSON lines) and confirm no Azure, AWS or Google package in `Zephyr.Api.csproj`.

## Deployment notes
- **Pre-deploy (manual, once per machine):** install Docker Desktop (or Rancher Desktop or Podman), the .NET SDK and Node.js.
- **Pre-deploy (manual, per clone):** run `node scripts/setup-env.mjs`. It creates `.env` with random passwords and never overwrites it.
- **Run:** `docker compose up --build`.
- **Configuration:** all values come from `.env`. See `.env.example` for the full list.
- Pinned image versions (Keycloak 26.0.7, Mailpit v1.21, .NET 10 images, HealthChecks.NpgSql) are unverified until the first real run.

## Known limitations
- The stack has not been started in Docker. AC 1 and AC 2 real runs are deferred to the first real `docker compose up` (PO decisions #118 and #119).
- AC 5 (15-minute start-up with seed data) is measured in US-130.
- `npm run seed` does not yet call `setup-env.mjs`. The README step covers this until US-130.
- No `package-lock.json` is committed yet. The frontend build uses `npm install` until a lockfile exists.
- The connection string variable is `ZEPHYR_DB`, not `ConnectionStrings__Default` as the spec says. The implementation follows the solution components list.
- Out of scope: OpenTofu, Azure, Key Vault, deploy pipelines.
