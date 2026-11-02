# US-129 — Test report (iteration 3)
**Verdict:** ✅ Approved · **Tester:** Cuélebre · **Date:** 2026-11-02

## Summary
| Check | Result | Evidence |
|---|---|---|
| AC 1 Extensions | ✅ | Ac1 test in BaselineTests.cs |
| AC 2 Idempotent migrations | ✅ | Ac2 test |
| AC 3 external_id constraints | ✅ | 3 tests: duplicate, unknown system, other system |
| AC 4 Audit columns | ✅ | 2 tests: user and System user |
| AC 5 Soft delete | ✅ | Ac5 test |
| Rework: Respawn vs append-only trigger | ✅ | DbFixture TablesToIgnore includes audit_log; regression test added |
| run_checks | ✅ | typecheck, lint, test, build pass |

## Not tested
- The DB tests are skipped here (no PostgreSQL). The PO accepted this; CI with ZEPHYR_REQUIRE_DB=1 will run them.
- The hand-written model snapshot is unverified. The PO accepted this; `has-pending-model-changes` goes into the US-132 CI.
