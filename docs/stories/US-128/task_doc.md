# US-128: Keycloak realm file with seven test users and a second realm
**Status:** Ready for UAT · **Category:** configuration · **Points:** 3 · **Sprint:** not recorded

## Summary
Adds a committed Keycloak realm `zephyr` with one test user per Zephyr role, so sign-in and permissions can be tested locally in place of Entra. Adds a second realm `zephyr-b` with its own issuer for the switch-by-configuration test. Passwords are never committed; they are rendered from `.env`.

## What was built
- `infra/keycloak/zephyr-realm.template.json`: realm `zephyr`, client `zephyr-web`, seven users with `${KC_USER_*_PASSWORD}` placeholders.
- `infra/keycloak/zephyr-realm-b.template.json`: realm `zephyr-b`, one user `user-b@example.com`.
- `scripts/render-realms.mjs`: single Node module that renders the templates to `infra/keycloak/generated/*.json`. Fails naming any missing variable.
- `scripts/render-realms.sh`, `scripts/render-realms.ps1`: one-line wrappers around the Node module.
- `docker-compose.yml` (Keycloak service only): mounts `generated/` at `/opt/keycloak/data/import`, command `start-dev --import-realm`. US-127 extends this file.
- `.env.example`: Keycloak entries and the seven `KC_USER_*_PASSWORD` variables.
- `.gitignore`: ignores `infra/keycloak/generated/`.
- `tests/realms.test.mjs`: checks the seven user names in role order, the absence of retired role names, and the template placeholders.
- `scripts/lint.mjs`, `package.json`, `package-lock.json`, `README.md`: lint and test wiring, and the documented startup step.

## How it works
- **AC 1, realm imported:** the render step writes the realm file, and Keycloak imports it at start. The realm holds seven enabled, email-verified users: admin, salesperson, BDR, project manager, consultant, finance and management. Passwords are non-temporary.
- **AC 2, fixed admin ID:** `admin@example.com` has the fixed `id` taken from `ZEPHYR_BOOTSTRAP_ADMIN_SUBJECT`. The value is also rendered into the template, so the ID is stable across rebuilds.
- **AC 3, no secrets in repo:** the committed template contains placeholders only. The renderer injects passwords from `.env` and refuses to run if a variable is missing.
- **AC 4, second realm:** `zephyr-b` has issuer `http://localhost:8180/realms/zephyr-b`, which differs from `zephyr`.
- Keycloak users only identify (issuer and subject). Zephyr roles live in `app_user`, not in tokens.

## How to test
Automated: `npm run typecheck && npm run lint && npm test && npm run build`

Manual (needs Docker):
1. Copy `.env.example` to `.env` and fill the empty values.
2. Run `scripts/render-realms.sh` or `scripts/render-realms.ps1`.
3. Run `docker compose up`.

- **AC 1 Verify:** `GET /admin/realms/zephyr/users` returns 7 users, or sign in at http://localhost:8180 (realm `zephyr`) with each user.
- **AC 2 Verify:** the `admin@example.com` user ID equals `ZEPHYR_BOOTSTRAP_ADMIN_SUBJECT`.
- **AC 3 Verify:** `npm run lint` passes. Rendering with a variable removed from `.env` fails and names it.
- **AC 4 Verify:** `http://localhost:8180/realms/zephyr-b/.well-known/openid-configuration` shows issuer `http://localhost:8180/realms/zephyr-b`.

## Deployment notes
**Pre-deploy steps**
- Set the seven `KC_USER_*_PASSWORD` values in `.env` by hand. Never commit them.
- Run `scripts/render-realms.sh` or `.ps1` before `docker compose up`. The README documents this as the first startup step.

**Post-deploy steps**
- Sign in at http://localhost:8180 (realm `zephyr`) with each user to verify.

**Configuration**
- `ZEPHYR_BOOTSTRAP_ADMIN_SUBJECT`, `ZEPHYR_BOOTSTRAP_ADMIN_ISSUER`, `OIDC_CLIENT_SECRET`, `KC_USER_ADMIN_PASSWORD`, `KC_USER_SALESPERSON_PASSWORD`, `KC_USER_BDR_PASSWORD`, `KC_USER_PROJECT_MANAGER_PASSWORD`, `KC_USER_CONSULTANT_PASSWORD`, `KC_USER_FINANCE_PASSWORD`, `KC_USER_MANAGEMENT_PASSWORD`, `KC_USER_B_PASSWORD`.

## Known limitations
- The live Docker import was not tested. It was reviewed statically only.
- Entra registration is out of scope. Keycloak stands in for Entra locally only.
- Keycloak runs in dev mode and is for localhost only.
- `scripts/setup-env.mjs` and `scripts/stack.test.mjs` do not exist on this branch. They belong to US-127 and need the same `KC_USER_PM_PASSWORD` to `KC_USER_PROJECT_MANAGER_PASSWORD` rename when they merge.
- `scripts/render-realms` is not yet wired into `scripts/check`. US-132 owns that file and adds the step when it is created.
- Merge with US-127: both branches change `docker-compose.yml` and `.env.example`. The merge keeps the full five-service stack from US-127 and adds the Keycloak service and variables from US-128.
