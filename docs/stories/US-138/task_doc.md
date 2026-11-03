# US-138: Claude usage log per story and PR description note
**Status:** Ready for UAT · **Category:** script · **Points:** 2 · **Sprint:** —

## Summary
Pull requests now carry a "Claude usage" section so the author records the story's Claude token usage and cost in the PR description. No new usage script or log format was added: usage data comes from the existing mechanism in `tools/claude-usage/`, as decided by the product owner (PO #85, #88, newest wins).

## What was built
- `.github/pull_request_template.md`: new `## Claude usage` section with fields Tokens in, Tokens out and Cost (USD). The author pastes the story total from `node tools/claude-usage/cli.mjs report`.
- `tests/docs.test.mjs`: test that the PR template section exists and has the required fields.

## How it works
- **AC 1 (log file), met differently:** no `scripts/claude-usage.mjs`. The pre-commit hook writes `docs/claude-usage/{story}_{timestamp}_{developer}.md` per push; `node tools/claude-usage/cli.mjs report` totals per story (PO #88, #140).
- **AC 2 (invalid input), met differently:** no script, so no input validation. The absence test was removed (PO #141).
- **AC 3 (PR template), met differently:** the template section is present. The author pastes the story total from the report command rather than a script-printed snippet. The PO noted that AgentStudio also tracks cost per story (PO #142).

## How to test
Unit: `npm run test --silent` (tests/docs.test.mjs).

Manual:
- **Verify:** open `.github/pull_request_template.md`; the `## Claude usage` section has Tokens in, Tokens out and Cost (USD) fields and tells the author to paste the story total from `node tools/claude-usage/cli.mjs report`.

## Deployment notes
- No pre-deploy or post-deploy steps.
- Per PO #85, add a step to the PR process: paste each story's total into the PR description.

## Known limitations
- `tools/claude-usage/` and `docs/claude-usage/` are not in this workspace branch. The report command could not be checked against its output format here; the PO should confirm the tool exists on the base branch.
- AC 1–3 are accepted as met differently, per PO answers #140, #141 and #142.
