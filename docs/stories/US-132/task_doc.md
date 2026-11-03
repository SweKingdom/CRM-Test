# US-132: scripts/check quality gate and GitHub Actions
**Status:** Ready for UAT · **Category:** devops · **Points:** 3 · **Sprint:** n/a (see work item)

## Summary
Developers now run one local script, `scripts/check`, that executes the same eight quality steps in a fixed order and prints a summary. The same script runs on every pull request in GitHub Actions. Nothing is deployed by this story.

## What was built
- `scripts/check`: Bash orchestrator. Runs all eight steps (lint, tests, seeds, openapi-regenerate, spectral, oasdiff, schemathesis, vulnerabilities), prints PASS/FAIL/SKIP per step, prints a `=== Summary` table and exits 1 if any step failed. Sourceable for tests.
- `scripts/check.ps1`: Windows equivalent with the same step names and order.
- `.github/workflows/check.yml`: runs on `pull_request` on ubuntu-latest with `permissions: contents: read`. Uses a Postgres 16 service container, creates `.env` from `.env.example`, runs `bash scripts/check`. No deployment, no login, no secrets.
- `.spectral.yaml`: extends `spectral:oas`.
- `tests/check.test.mjs`: tests for AC 1–3, SKIP and `REQUIRE_ALL` behaviour, and the Spectral version pin.
- `package.json`: lint script also syntax-checks `tests/check.test.mjs`.
- `src/frontend/package.json`: `typecheck` script (`tsc --noEmit`) used by step 1.
- `README.md`: "Checks" section.

## How it works
- **Single entry point (AC 1):** `scripts/check` runs the steps in order and ends with the summary table of eight rows. Steps 4–7 print `SKIP (not yet applicable)` until the OpenAPI file exists at `docs/api/openapi.json`.
- **Failure propagation (AC 2):** a failing step is reported as `FAIL`. The remaining steps still run. The script exits 1 if any step failed.
- **CI parity (AC 3):** the workflow invokes `bash scripts/check` only. It has a Postgres service, `contents: read` and `on: pull_request`.
- **SKIP handling:** a missing target prints `SKIP (not yet applicable)`, never a silent PASS. Skipped steps are listed in the summary. With `REQUIRE_ALL=1` (set in CI), a SKIP for a built feature becomes a failure.
- **Database for tests:** tests read `ZEPHYR_TEST_DB` in Npgsql key=value form. Locally the script starts the Compose `db` service and waits for health. In CI (`CI=true`) it does not call Compose for `db`; the service container is used.
- **Keycloak:** not started in CI. Steps that need it print `SKIP (needs Keycloak)` and run locally with `docker compose up -d keycloak`.
- **Spectral:** run as `npx --yes @stoplight/spectral-cli@6.15.0`. The version is one constant, and a test checks the pin.
- **Step 6 (oasdiff):** compares the generated file with the copy on `origin/main` (or `main` if there is no remote). It does not need a running API.

## How to test
**Unit tests:** `npm test` (includes `tests/check.test.mjs`; bash-driven tests run on Linux, macOS and WSL and are skipped on native Windows). All checks: `npm run typecheck && npm run lint && npm test && npm run build`.

- **AC 1 – Single entry point.** Run `scripts/check` (or `scripts/check.ps1`).
  **Verify:** the output ends with a `=== Summary` table of 8 rows in order: lint, tests, seeds, openapi-regenerate, spectral, oasdiff, schemathesis, vulnerabilities. Steps 4–7 show `SKIP ... (not yet applicable ...)` until the OpenAPI file exists.
- **AC 2 – Failure propagates.** Add a temporary failing test (e.g. `Assert.True(false)` in `src/backend/Zephyr.Api.Tests`) and run `scripts/check; echo $?`.
  **Verify:** `FAIL tests` in the summary, the other steps still ran, exit code 1. Remove the test again.
- **AC 3 – CI parity.** `grep -nE "scripts/check|deploy|login|push|secrets" .github/workflows/check.yml`.
  **Verify:** only the `bash scripts/check` line matches; the workflow has `services: postgres`, `permissions: contents: read` and `on: pull_request`.

## Deployment notes
- No deployment. The workflow only runs checks on pull requests.
- No secrets are required. CI uses the `.env.example` values for the Postgres service.
- Merge order: US-127 (Compose stack) must be Release Ready first. This story should be the first of the quality-gate stories to merge.
- Later stories add their steps: the OpenAPI generator and `docs/api/openapi.json` (US-136) must install oasdiff and Schemathesis 4 in the workflow. With `REQUIRE_ALL=1`, a missing tool fails once the file exists.

## Known limitations
- The bash-driven tests do not run on native Windows. The PowerShell script is not covered by the same automated tests.
- The end-to-end run of `scripts/check` has not been executed on a real machine yet. AC 1 and AC 2 rest on tests with stubbed steps and on code review. The real eight-row output and exit code still need one manual run.
- Steps 4–7 SKIP until the OpenAPI generator and file exist (US-136).
- Steps needing Keycloak SKIP in CI.
- Lockfile: `@stoplight/spectral-cli` is not yet an exact devDependency. It is run through `npx` at the pinned version until the next local lockfile regeneration (PO answer #123).
- Deployment pipelines are out of scope.
