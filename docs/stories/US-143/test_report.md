# US-143 — Test report (iteration 2)
**Verdict:** ✅ Approved · **Tester:** Cuélebre · **Date:** 2026-11-03

## Summary
| Check | Result | Evidence |
|---|---|---|
| AC 1 Matrix | ✅ | The seeder lists give 665 rows. The Ac1 tests check the PO rules and that the docs table equals the CSV. |
| AC 2 Stored model | ✅ (accepted per PO #139) | Migration 0004 and the idempotent seeder. The Ac2 tests compare DB to CSV and check xmin on re-seed. |
| AC 3 Team definition | ✅ | The Team and Project-scoped access sections in docs/permissions.md. The migration adds the manager_id FK and the self check. |
| AC 4 Integrity | ✅ | Parse validates everything and reports every bad line. An invalid CSV writes nothing and stops the API at start. |
| AC 5 Role keys | ✅ | The Ac5 test, including a negative probe. |
| Lessons (Respawn, mirrored paths) | ✅ | The reset regression test exists. Shared fixture helpers. |
| run_checks | ✅ | typecheck, lint, test and build all pass. |

## Not tested
- DB-backed tests were skipped locally (no PostgreSQL). I reviewed them by hand against the migration and the fixture. The first real run is in CI with ZEPHYR_REQUIRE_DB=1.
- The cells not covered by PO answer #90 are listed in docs/permissions.md for PO sign-off. They default to none or follow the stated assumptions (see #139).
