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

## Checks

`npm run typecheck && npm run lint && npm test && npm run build`.
