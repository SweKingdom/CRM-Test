# US-129 — Test report (iteration 2)
**Verdict:** ✅ Approved · **Tester:** Cuélebre · **Date:** 2026-11-02

## Summary
| Check | Result | Evidence |
|---|---|---|
| run_checks (install, typecheck, lint, test, build) | ✅ | all exit 0 |
| AC 1 Extensions | ✅ | Baseline.cs; Ac1 test |
| AC 2 Idempotent migrations | ✅ | EF history; Ac2 test |
| AC 3 external_id uniqueness/check | ✅ | unique index + check constraint; 3 tests |
| AC 4 Audit columns | ✅ | AuditInterceptor; 2 tests |
| AC 5 Soft delete | ✅ | interceptor + query filter; Ac5 test |

## Not tested
- The 9 DB tests were not executed against a real PostgreSQL here (they skip without ZEPHYR_TEST_DB). The product owner accepted this; US-132 CI must set ZEPHYR_REQUIRE_DB=1 and run `dotnet ef migrations has-pending-model-changes` (the snapshot is hand-written).
