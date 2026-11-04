# US-146: Automated permission test suite for seven roles
**Status:** Ready for UAT · **Category:** testing · **Points:** 3 · **Sprint:** build-v0.3 gate (27 Nov)

## Summary
Adds automated backend tests that check the stored permission matrix against the CSV and against the live API for every role. A permission leak on a probed route now fails the build. Browser (Playwright), time-privacy and search-leak tests are split into follow-up stories.

## What was built
- `Zephyr.Api.Tests/Permissions/MatrixCoverageTests.cs`: runs the real app on the test database with a test-only auth handler (`PermissionAuthHandler`, `X-Test-User` / `X-Test-Role`) and seven seeded role users (`PermissionWorld`).
- One theory case per CSV row (665 cases, `Ac1_Each_matrix_row_is_stored_and_enforced_by_the_api`): checks the stored `role_permission` and, where a probe exists, the HTTP result.
- Probe registry: account delete, app_user update, reference_data read/create/update.
- `OutsideMatrix`: `/api/v1` routes that are not matrix cells, each with a reason (by-external-id PUT is an integration route, see US-135; `me/permissions`; approver and delegation routes from US-144 and US-145).
- `Pending`: 16 entities with no route yet, mapped to their owning story or feature.
- Meta-tests: one case per role × entity × action; every entity has a probe or is pending (never both); the `/api/v1` route inventory equals probes plus `OutsideMatrix`; anonymous requests get 401 on every probed route.
- `permission-matrix.csv`: Admin `account` `delete` = `org` (PO answer #156, US-135 AC 2).
- `scripts/check` and `scripts/check.ps1`: `--ui` / `-Ui` adds a ninth step `ui`. It fails with "--ui was requested but src/frontend/e2e does not exist yet" until the e2e suite exists.
- `tests/check.test.mjs`: tests for the `--ui` step.
- `docs/permissions.md`, `README.md`: matrix, Admin account rule, by-external-id note, quality gate.

## How it works
- **AC 1 Matrix coverage:** every CSV row has a case that checks the stored permission. Where a probe exists, `none` must answer exactly 403 and any other scope must not answer 401, 403 or 5xx. Probes never write data; they use missing IDs or invalid payloads.
- **AC 1 Route inventory:** a new `/api/v1` route without a probe or an `OutsideMatrix` reason fails the build.
- **AC 1 Pending:** an entity is either probed or pending, never both. When its route lands, its entry moves from `Pending` to a probe.
- **AC 5 In gate:** the permission tests run in step `tests` of `scripts/check`. `--ui` adds the `ui` step, which fails until the e2e suite exists.

## How to test
See `how-to-test.md` in the story folder. Short form:
- Checks: `npm run typecheck --silent && npm run lint --silent && npm run test --silent && npm run build --silent`.
- Permission tests: `dotnet test src/backend/Zephyr.Api.Tests --filter FullyQualifiedName~MatrixCoverageTests` (needs PostgreSQL, `ZEPHYR_TEST_DB`; CI sets `ZEPHYR_REQUIRE_DB=1`).

## Deployment notes
- No migrations and no new triggers or constraints. The tests call `fixture.ResetAsync`.
- CI must set `ZEPHYR_REQUIRE_DB=1` so the database-backed tests cannot be skipped. The first real run is in CI; locally they are skipped.
- No configuration to set.

## Known limitations
- **AC 2 (Playwright UI routes):** not built. No e2e tool, no UI routes config, no `/api/v1/me/permissions` on this branch. Follow-up: "E2E permission suite for the seven realm users (Playwright)", depends on US-144 and US-148 (PO answer #157).
- **AC 3 (time privacy 404/200):** not built. No `time_entry` table or read route. Follow-up: "Time-privacy permission tests (404/200)", depends on US-145, US-166 and US-170 (PO answer #158).
- **AC 4 (search leak):** not built. No search backend or scope filter. Follow-up: "Search leak tests with an independent SQL oracle (SearchLeakTests.cs)", depends on US-144 and US-147 (PO answer #159).
- **AC 5 wording:** `--ui` fails with a clear message instead of wiring Playwright into the build-v0.3 gate. Accepted in PO answer #160; the gate wiring stays open until the AC 2 suite exists.
- **Account create and update:** no probe until the POST and PATCH routes exist in FT-113. They are checked against the matrix only.
- Penetration testing is out of scope.
