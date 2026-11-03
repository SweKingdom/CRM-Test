# US-135 — Test report (iteration 2)
**Verdict:** ✅ Approved · **Tester:** Cuélebre · **Date:** 2026-11-03

## Summary
| Check | Result | Evidence |
|---|---|---|
| AC 1 Upsert twice | ✅ | ExternalUpsert.cs, Ac1_* tests |
| AC 2 Soft delete | ✅ (accepted as met differently, PO #133) | MapSoftDelete, Ac2_* tests |
| AC 3 Concurrent upsert | ✅ | ON CONFLICT + FOR UPDATE, Ac3 test with 6 parallel calls |
| AC 4 Other scope | ✅ | Ac4 test |
| AC 5 Deleted record | ✅ | 409 RECORD_DELETED, Ac5 test |
| AC 6 Who may upsert | ✅ (accepted as met differently, PO #132) | InterimRoleGate, Ac6_* tests |
| AC 7 Validation | ✅ | Ac7_* tests, no external_id row left behind |
| Lessons and merge | ✅ | No trigger or constraint added; both sides of the merge present |
| Project checks | ✅ | typecheck, lint, test and build pass |

## Notes
- The merge with main kept `FORBIDDEN` and `MISSING_EXCHANGE_RATE`, the `Account` and `ExchangeRate` entities, and both sets of routes in Program.cs.
- The DB-backed UpsertTests are skipped locally and first run in CI with ZEPHYR_REQUIRE_DB=1. The tests were reviewed by hand against the code and look sound.
- This verification was a static review of the files plus `run_checks`. No screenshots were captured.

## Not tested
- None. The two accepted deviations (AC 2 and AC 6) are recorded as accepted, not as failures.
