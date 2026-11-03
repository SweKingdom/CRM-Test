# US-147 — Test report (iteration 2)
**Verdict:** ✅ Approved · **Tester:** Cuélebre · **Date:** 2026-11-03

run_checks: typecheck, lint, test, build all pass. DB-backed tests are skipped locally (no PostgreSQL); reviewed by hand against fixtures, first real run in CI.

| Check | Result | Evidence |
|---|---|---|
| AC 1 accent-insensitive | ✅ | f_search_norm; Ac1 test |
| AC 2 prefix/typo | ✅ | tsquery prefix + word_similarity; Ac2 test |
| AC 3 fallback | ✅ (accepted, PO #146) | SearchEndpoints.RunAsync; Ac3 tests (4 vs 5 hits, limit) |
| AC 4 permissions | ✅ (PO #143, US-144 scope now used) | shared Filter in both steps; per-role x per-type leak test |
| AC 5 speed | ✅ (accepted, PO #144) | bench.mjs, GIN index test; p95 run optional |
| AC 6 freshness | ✅ | trigger in same tx; tests |
| AC 7 soft delete/anonymise | ✅ (accepted, PO #145) | account + probe-table tests, Respawn regression |
| AC 8 input | ✅ | validation + tests |

Lessons check: Respawn regression test exists; interim rule removed; MatrixCoverageTests updated.

## Not tested
- ~56,000-row p95 benchmark (optional per PO #144).
- DB tests not executed locally; CI must run them with ZEPHYR_REQUIRE_DB=1.
