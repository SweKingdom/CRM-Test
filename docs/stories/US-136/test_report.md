# US-136 — Test report (iteration 5)
**Verdict:** ✅ Approved · **Tester:** Cuélebre · **Date:** 2026-11-03

## Summary
| Check | Result | Evidence |
|---|---|---|
| run_checks (typecheck, lint, test, build) | ✅ | all exit 0 |
| AC 1 Lint passes | ✅ | SPECTRAL_VERSION=6.15.0, .spectral.yaml rules, tests |
| AC 2 Drift fails | ✅ | step_openapi_regenerate, "run generate", determinism test |
| AC 3 Breaking change flagged | ✅ | oasdiff step, field-removal test |
| AC 4 Conventions documented | ✅ | generator and AC 4 test |
| Accepted deviations #135, #136, #137, #138, #161 | accepted | .json path, no package.json, hand metadata file, oasdiff 1.11.7 |

## Not tested
- Spectral run, oasdiff test and the DB-backed OpenApiContractTests need network, the binary or PostgreSQL. They run first in CI.

## Notes
- Stale docs/api/openapi.yaml is gone; no copy of the old path remains in the listed files.
