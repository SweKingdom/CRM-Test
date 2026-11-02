# US-130 — Test report (iteration 4)
**Verdict:** ✅ Approved · **Tester:** Cuélebre · **Date:** 2026-11-02

## Summary
| Check | Result | Evidence |
|---|---|---|
| run_checks (typecheck, lint, test, build) | ✅ | all exit 0 |
| AC 1 Reference data | ✅ | Seed/0001 counts 9/11/6/6/7/8; rates not seeded: accepted #122 |
| AC 2 Idempotent | ✅ | ON CONFLICT everywhere; Ac2 test |
| AC 3 Reference rows update | ✅ | DO UPDATE on key; Ac3 test |
| AC 4 Demo never overwritten | ✅ | DO NOTHING on id; Ac4 test |
| AC 5 Validation | ✅ | seeds.test.mjs, 4 cases |

## Notes
- DB-backed SeedTests (AC 1-4) were not run here; they run in CI with ZEPHYR_REQUIRE_DB=1, accepted by the PO (#121).
- Lessons: no triggers or constraints that break Respawn; the FK close_reason→close_outcome is handled by Respawn; audit_log is already in TablesToIgnore.

## Not tested
- Execution of SeedTests against PostgreSQL (CI only, accepted #121).
