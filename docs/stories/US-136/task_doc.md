# US-136: Generated OpenAPI 3.1 with Spectral lint and CI drift check
**Status:** Ready for UAT · **Category:** devops · **Points:** 5 · **Sprint:** n/a

## Summary
The API description is now generated from field metadata and committed as `docs/api/openapi.json`. Developers never edit it by hand: the local check and CI fail when the committed file differs from the generated output, Spectral lints it, and oasdiff flags breaking changes against `origin/main`.

## What was built
- `tools/openapi-from-metadata/generate.mjs`: builds the OpenAPI 3.1 object (`buildSpec`) and writes it with `toJson` (sorted keys, 2-space indent, LF). Accepts `[metadata.json] [--out file]`; default output `docs/api/openapi.json`. Node only, no dependencies.
- `tools/openapi-from-metadata/field-metadata.json`: hand-maintained field metadata for Account, ExchangeRate and CurrentUser, plus their operations (interim source, see Known limitations).
- `docs/api/openapi.json`: generated and committed, with "generated, do not edit" in `info.description`.
- `.spectral.yaml`: Spectral `spectral:oas` ruleset with the project's custom rules; applied to the JSON file.
- `scripts/check`, `scripts/check.ps1`: steps `openapi-regenerate` (generate to temp, diff), `spectral` (pinned via `SPECTRAL_VERSION` / `$SpectralVersion` = 6.15.0), `oasdiff` (base `origin/main:docs/api/openapi.json`), `schemathesis` (looks for `"securitySchemes"`).
- `.github/workflows/check.yml`: installs oasdiff 1.11.7 (`OASDIFF_VERSION`) and runs the steps.
- `.gitattributes`: LF line endings for the JSON file.
- `tests/openapi.test.mjs`, `tests/check.test.mjs`: generator output, sorted keys, drift, oasdiff behaviour; path consistency across scripts.
- `src/backend/Zephyr.Api.Tests/Common/OpenApiContractTests.cs`: reads `docs/api/openapi.json` with System.Text.Json and asserts that registered routes equal documented routes.
- `README.md`: "API description (OpenAPI)" section and the gate table.

## How it works
- **AC 1 Lint passes:** Spectral 6.15.0 lints `docs/api/openapi.json` with 0 errors. The version is set in one constant per script.
- **AC 2 Drift fails:** the `openapi-regenerate` step generates to a temp file and diffs it with the committed file. A hand edit produces a diff and the message "run generate" with a non-zero exit. Unedited files pass. Generation is deterministic: stable key order, no timestamps.
- **AC 3 Breaking change flagged:** removing a field from the metadata makes `oasdiff breaking` report a breaking change (for example, a removed response property) and exit non-zero with `--fail-on ERR`. Without oasdiff installed locally the step shows `SKIP (oasdiff is not installed)`; in CI it is required.
- **AC 4 Conventions documented:** the file declares `"openapi": "3.1.0"`, `servers[0].url` = `/api/v1`, `ProblemDetails` error schema, parameters `Limit`, `Cursor`, `Filter`, `Sort`, `IfMatch` (`If-Match`), and the `ETag` header under `components`. `/exchange-rates` uses the shared `Page` schema.

## How to test
**Checks:** `npm run typecheck && npm run lint && npm test && npm run build` (or `scripts/check`, `scripts/check.ps1`).

- **AC 1 Lint passes.** `npx --yes @stoplight/spectral-cli@6.15.0 lint docs/api/openapi.json`. **Verify:** 0 errors.
- **AC 2 Drift fails.** Add a line to `docs/api/openapi.json` and run `scripts/check`. **Verify:** step `openapi-regenerate` fails with a diff and "run generate"; after `node tools/openapi-from-metadata/generate.mjs` it passes. Running the generator twice yields identical files.
- **AC 3 Breaking change flagged.** Delete `createdAt` from `entities.Account.fields` in `field-metadata.json`, run the generator, then run `oasdiff breaking` against the base file. **Verify:** a response-property-removed finding naming `createdAt` and a non-zero exit.
- **AC 4 Conventions documented.** Open `docs/api/openapi.json`. **Verify:** the fields listed under AC 4 above are present.
- **Contract test.** `dotnet test src/backend/Zephyr.Api.Tests --filter OpenApiContractTests` with `ZEPHYR_TEST_DB` set. **Verify:** passes; a route added in `Program.cs` without documentation makes it fail and names the route.

## Deployment notes
- **Pre-deploy:** none.
- **First CI run (verification):**
  - Confirm that the "Install oasdiff" step downloads 1.11.7. If it fails, pin the newest released 1.11.x in `OASDIFF_VERSION` (PO #138). Do not remove the check.
  - Confirm Spectral reports 0 errors (it needs network and was not run locally).
  - A deliberate breaking change needs a PR label and an `oasdiff` changelog note (CI policy, review-enforced).
- No `package.json` or lock file for the generator: it has no dependencies; Spectral is pinned through `SPECTRAL_VERSION` and `npx` (PO #136, #161).

## Known limitations
- Field metadata is hand-maintained in `field-metadata.json` (three entities). The contract test guards routes only, not field-level drift. Follow-up: US-182 must read metadata from the metadata tables and remove `field-metadata.json` (PO #137).
- The OpenAPI file is `.json`, not `.yaml` as first written in the story; this follows PO #135 and #124.
- The `docs/api/openapi.yaml` file was removed; `docs/api/openapi.json` is the only description.
- Spectral and oasdiff were not executed in the build environment; they are verified in the first CI run.
