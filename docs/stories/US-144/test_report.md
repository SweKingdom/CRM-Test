# US-144 — Test report (iteration 3)
**Verdict:** ✅ Approved · **Tester:** Cuélebre · **Date:** 2026-11-03

## Summary
| Check | Result | Evidence |
|---|---|---|
| AC 1 Own scope | ✅ (PO #148) | EngineTests Ac1_* |
| AC 2 Country scope | ✅ (PO #148) | Ac2_* |
| AC 3 Organisation scope | ✅ (PO #148) | Ac3_* |
| AC 4 UI parity | ✅ (PO #149) | Ac4_*; no frontend test, accepted |
| AC 5 Admin changes | ✅ (PO #150) | Ac5 theory with fake time, cookie and token |
| AC 6 Search | ✅ (PO #148) | Ac6_* |
| AC 7 Tokens | ✅ (PO #150) | Ac7_* |
| AC 8 Default deny | ✅ | Ac8_* and AnonymousAllowList.cs |
| Checks | ✅ | typecheck, lint, test and build all exit 0 |

## Notes
- The DbFact tests need PostgreSQL. They were reviewed by hand only; CI runs them with ZEPHYR_REQUIRE_DB=1.
- `InterimRoleGate.cs` is an empty, comment-only file. Run `git rm` on it before merge, as the manual step says.
- Once US-142 is merged, the AC 5 and AC 7 tests must pass with the real token handler (PO #150).
- Entity stories FT-113 to FT-119 must add the Filter/FindAsync/Columns criterion (PO #148).
