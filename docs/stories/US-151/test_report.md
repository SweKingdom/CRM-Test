# US-151 — Test report (iteration 4)
**Verdict:** ✅ Approved · **Tester:** Cuélebre · **Date:** 2026-11-04

## Summary
| Check | Result | Evidence |
|---|---|---|
| AC 1 Timeline membership | ✅ | Ac1_* tests; stub opportunity table accepted (#166) |
| AC 2 Connected flag | ✅ | Ac2_* tests, all 9 seeded outcomes |
| AC 3 Invalid regarding | ✅ | Ac3_* tests; scope predicate deferred (#165) |
| AC 4 Client account ignored | ✅ | Ac4_* tests |
| AC 5 Contact moves | ✅ | Ac5_* tests; helper method accepted (#164) |
| AC 6 Outcome rules | ✅ | Ac6_* tests |
| Checks and lessons | ✅ | run_checks passed; the Respawn reset test covers the activity tables |

## Not tested
- DB-backed tests are skipped locally without PostgreSQL. They were reviewed by hand against the fixtures; the first real run is in CI.
- The OpenAPI extension is accepted as met differently (#167).
