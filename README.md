# Zephyr

Lean, headless CRM and project-operations system. This edition runs entirely on localhost with synthetic data and no cloud services.

## Run Zephyr locally

Prerequisites: Docker Desktop (or Rancher Desktop / Podman with compose) and Node.js 20+. The Compose file uses plain `docker compose` features only.

```sh
node scripts/setup-env.mjs   # creates .env with random passwords; never overwrites an existing .env
docker compose up --build
```

| Service | URL |
|---|---|
| API | http://localhost:8080 (health: http://localhost:8080/health) |
| Web | http://localhost:3000 |
| Keycloak | http://localhost:8180 |
| Mailpit (caught mail) | http://localhost:8025 (SMTP on localhost:1025) |
| PostgreSQL | localhost:5432 |

All ports are bound to 127.0.0.1. Generated passwords (including the Keycloak test users, `KC_USER_*_PASSWORD`) are in `.env`, which is gitignored. `.env.example` lists every variable with placeholders only.

Check that it works:

```sh
curl http://localhost:8080/health   # {"status":"Healthy"}
docker compose logs api             # JSON log lines on stdout
```

Repository checks: `npm run typecheck && npm run lint && npm test && npm run build`.
