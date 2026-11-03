# US-142 — Test report (iteration 1)
**Verdict:** ✅ Approved (with notes) · **Tester:** Cuélebre · **Date:** 2026-11-03

## Summary
| Check | Result | Evidence |
|---|---|---|
| run_checks (typecheck, lint, test, build) | ✅ | all exit 0 |
| AC 1 Create | ✅ | ApiTokens.cs CreateAsync/ListAsync; Ac1_* tests |
| AC 2 Limit | ✅ | advisory lock + count; Ac2_* tests |
| AC 3 Expiry | ✅ | ReadInput, handler expiry; Ac3_* tests |
| AC 4 Revoke | ✅ | RevokeAsync, uncached handler; Ac4_* tests |
| AC 5 Storage | ✅ | migration 0004, Ac5 test |
| Lessons (Respawn, mirrored names) | ✅ | Reset regression test; audit_log already ignored |

Static review only; no screenshots (no running UI). C# DB tests could not run locally (no PostgreSQL); first real run is CI with ZEPHYR_REQUIRE_DB=1.

## Notes (not failures)
- `docs/api-tokens.md` says that after changing the algorithm "old rows keep working until they expire". The code does not do that: the handler looks up by the new hash and `HashMatches` refuses rows whose algorithm is not current, so old tokens stop working. Correct the sentence (or add a dual-algorithm lookup when the change happens).
- Error codes are upper-case as the catalogue requires; matches PO answer #74 intent.

## Not tested
- GitHub secret-scanning pattern (post-deploy manual step, repository admin).
- DB-backed tests execution (CI only).
