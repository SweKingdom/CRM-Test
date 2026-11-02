# Zephyr CRM — local edition

## Start (Keycloak part, US-128)

1. `cp .env.example .env` and fill in every empty value (passwords, client secret). `.env` is gitignored.
2. Render the realm files: `scripts/render-realms.sh` (or `scripts/render-realms.ps1` on Windows). It fails and lists any variable missing from `.env`.
3. `docker compose up`

Keycloak runs at http://localhost:8180 with two realms:

- `zephyr` — seven users (`admin`, `salesperson`, `bdr`, `pm`, `consultant`, `finance`, `management` `@example.com`); `admin@example.com` has the subject in `ZEPHYR_BOOTSTRAP_ADMIN_SUBJECT`.
- `zephyr-b` — one user `user-b@example.com`, a different issuer, for the switch-by-configuration test.

Realm files in `infra/keycloak/*.template.json` hold placeholders only; rendered files go to `infra/keycloak/generated/` (gitignored). Re-render and recreate the Keycloak container after changing a password; realms are only imported when they don't exist yet.

## Checks

`npm run typecheck`, `npm run lint`, `npm test`, `npm run build`.
