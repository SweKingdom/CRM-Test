# US-140 — Test report (iteration 2)
**Verdict:** ✅ Approved (with notes) · **Tester:** Cuélebre · **Date:** 2026-11-03

`run_checks` passed (typecheck, lint, test, build). Review was static; DB and Keycloak tests are skipped locally and first run in CI (ZEPHYR_REQUIRE_DB=1).

## Summary
| Check | Result | Evidence |
|---|---|---|
| AC 1 Sign-in, no password column | ✅ | 0003_AppUser.cs; Ac1_* tests |
| AC 2 Unverified/untrusted email | ✅ | UserMatching.MatchAsync; Ac2_* |
| AC 3 Unknown user refused | ✅ | no create path; Ac3_* |
| AC 4 Bootstrap admin | ✅ | Warning + audit_log; Ac4_* |
| AC 5 Second realm | ✅ | per-provider scheme; Ac5_* |
| AC 6 CSRF | ✅ | RejectCsrf; Ac6_* |
| AC 7 No CORS | ✅ | no CORS registration; Ac7_*; vite proxy |
| AC 8 Anonymous set | ✅ | fallback policy; Ac8_* |
| Lessons (Respawn / audit_log) | ✅ | DbFixture TablesToIgnore audit_log; regression test Ac4_Reset_between_tests_works |

## Notes
- Bootstrap is skipped when the bootstrap email already belongs to a pre-provisioned user (falls to the email-link path, no Admin role). Edge case, not in the ACs.
- Unique (issuer, subject) index ignores deleted_at while lookups exclude soft-deleted rows; a soft-deleted linked user could block re-creation. Edge case.
- AC 8 is finally cross-checked with US-144 AC 8.

## Not tested
- DB-backed and Keycloak tests were not executed here (no PostgreSQL/Keycloak); reviewed by hand against fixtures and realm templates (subject ids match).
