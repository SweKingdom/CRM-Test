# Changelog

## Unreleased

- 2026-11-02 — US-130 Reference data seeds and validate_seeds.py: reference tables, idempotent seeds (`npm run seed`), three demo accounts, and seed file validator added.
- 2026-11-02 — US-127 Docker Compose stack, .env and health endpoint: local stack (db, api, web, keycloak, mailpit) with `.env` setup script, `/health` endpoint and JSON logs. First real `docker compose up` still pending.
- 2026-11-02 — US-128 Keycloak realm file with seven test users and a second realm: committed realm templates for `zephyr` (seven users) and `zephyr-b`, passwords rendered from `.env`, and a render script.
