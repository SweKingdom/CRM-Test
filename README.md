# Zephyr

Lean, headless CRM and project-operations system. This edition runs entirely on localhost with synthetic data and no cloud services.

## Run Zephyr locally

Prerequisites: Docker Desktop (or Rancher Desktop / Podman with compose) and Node.js 20+. The Compose file uses plain `docker compose` features only.

```sh
node scripts/setup-env.mjs   # creates .env with random passwords (never overwrites it) and renders the Keycloak realms
docker compose up --build
```

| Service | URL |
|---|---|
| API | http://localhost:8080 (health: http://localhost:8080/health) |
| Web | http://localhost:3000 |
| Keycloak | http://localhost:8180 |
| Mailpit (caught mail) | http://localhost:8025 (SMTP on localhost:1025) |
| PostgreSQL | localhost:5432 |

All ports are bound to 127.0.0.1. Generated passwords are in `.env`, which is gitignored. `.env.example` lists every variable with placeholders only.

### Keycloak

Keycloak imports two realms from `infra/keycloak/*.template.json` (placeholders only), rendered to `infra/keycloak/generated/` (gitignored) from `.env`:

- `zephyr`: seven users, one per role (`admin`, `salesperson`, `bdr`, `pm`, `consultant`, `finance`, `management` `@example.com`). `admin@example.com` has the subject in `ZEPHYR_BOOTSTRAP_ADMIN_SUBJECT`.
- `zephyr-b`: one user `user-b@example.com`, a different issuer for the switch-by-configuration test.

Passwords are in `.env`:

| Variable | Role |
|---|---|
| `KC_USER_ADMIN_PASSWORD` | Admin |
| `KC_USER_SALESPERSON_PASSWORD` | Salesperson / opportunity owner |
| `KC_USER_BDR_PASSWORD` | Business development rep |
| `KC_USER_PROJECT_MANAGER_PASSWORD` | Project manager / approver |
| `KC_USER_CONSULTANT_PASSWORD` | Consultant |
| `KC_USER_FINANCE_PASSWORD` | Finance |
| `KC_USER_MANAGEMENT_PASSWORD` | Management / leadership |
| `KC_USER_B_PASSWORD` | `user-b@example.com` in realm `zephyr-b` |

After changing a password in `.env`, run `node scripts/render-realms.mjs` and recreate the Keycloak container; realms are only imported when they don't exist yet.

Check that it works:

```sh
curl http://localhost:8080/health   # {"status":"Healthy"}
docker compose logs api             # JSON log lines on stdout
```

## Synthetic volume seed

Dynamics-scale test data for performance measurements: about 56,000 searchable records (6,000 accounts, 18,000 contacts, 12,000 leads, 9,000 opportunities, 11,000 projects) and about 105,000 time entries, 18,495 of them in the 12 months up to the anchor date. All of it is synthetic and every row has `custom->>'synthetic' = 'true'`. About 2% of the names are typo variants (the correct spelling is in `custom->>'typo_of'`), and names include åäöéü, Malmö, Göteborg and Västerås.

```sh
npm run seed -- --volume                     # load (needs DATABASE_URL, see .env.example; the stack must be migrated)
npm run seed -- --volume --reset             # remove the synthetic rows first, then load again
npm run seed -- --volume --seed 7 --anchor 2026-12-11
```

- Entities whose table does not exist yet print `SKIP <entity>: table missing`; the rest are loaded. Run the seed again once more tables exist.
- Running again without `--reset` refuses. `--reset` only deletes rows with the synthetic marker.
- The same `--seed` (default 42) and `--anchor` (default: today in Europe/Stockholm) always give the same ids and names.
- Targets are in `tools/search-benchmark/generate/targets.json`; one SQL file per entity next to it; name lists in `tools/search-benchmark/data/`.

## Checks

`npm run typecheck && npm run lint && npm test && npm run build`.
