# US-137: README "Run Zephyr locally", BUILD.md sections and local-edition ADR
**Status:** Ready for UAT · **Category:** documentation · **Points:** 2 · **Sprint:** —

## Summary
Documents the local Zephyr setup so an outsider can rebuild the environment from a clean checkout. Adds the "Run Zephyr locally" section to the README, the BUILD.md log with the first feature section, a feature-section template, and an ADR index with the local-edition ADR. No code, data model, API or configuration changed.

## What was built
- `README.md`: "Run Zephyr locally" section (ordered steps from clone to signed-in, URLs, seeded test user name, checks, stop/reset) and a "Quality gate" section.
- `BUILD.md`: intro and the section `## F01 — Local stack, seeds and quality gate (FT-108)`.
- `build/prompts/feature-section.md`: six-part template, the single source for part names.
- `docs/adr/ADR-013-local-edition.md`: Proposed; the local edition (Compose, Keycloak, Mailpit, synthetic data) defers ADR-001 and ADR-002 without superseding them.
- `docs/adr/README.md`: approval index for ADR-004, 006, 007, 008, 009, 012 and ADR-013, all Proposed, with approver and approval PR.
- `.github/pull_request_template.md`: checklist line "BUILD.md section added and reviewed by second developer".
- `tests/docs.test.mjs`: checks for AC 1–4 and the PR template line.

## How it works
- **AC 1 (README):** The "Run Zephyr locally" section lists every command from clone to signed-in seeded system, in order, with URLs and a seeded test user name. No passwords appear.
- **AC 2 (BUILD.md):** One section per merged feature. Each section has the six parts of `build/prompts/feature-section.md` and a `Reviewed by:` line. The first section is `## F01 — Local stack, seeds and quality gate (FT-108)`, with `Reviewed by: pending`.
- **AC 3 (ADR-013):** States that it defers ADR-001 and ADR-002 and does not supersede or replace them.
- **AC 4 (index):** Lists each ADR with status, approver and approval PR. None is `Accepted`. Approver reads "not yet recorded" and approval PR is "—". The index notes that the full text of these ADRs is in the original Zephyr repository.

## How to test
Run `npm run typecheck && npm run lint && npm test && npm run build`. The docs checks are in `tests/docs.test.mjs`.

- **AC 1:** Run the commands of README "Run Zephyr locally" in order on a clean checkout (full proof in US-139).
  **Verify:** `node --test tests/docs.test.mjs` checks order, URLs, seeded user and absence of passwords.
- **AC 2:** **Verify:** each `##` section in BUILD.md has the six `###` headings and a `Reviewed by:` line.
- **AC 3:** **Verify:** `docs/adr/ADR-013-local-edition.md` contains "defers ADR-001 and ADR-002" and "does not supersede or replace".
- **AC 4:** **Verify:** `docs/adr/README.md` lists ADR-004, 006, 007, 008, 009, 012, 013 with status, approver and approval PR; none is `Accepted`.

## Deployment notes
- Post-deploy: Henry, Elin and the PMs approve ADRs 004, 006, 007, 008, 009, 012 and ADR-013 by hand. The approving PR edits `docs/adr/README.md` (Status `Accepted`, Approver, Approval PR).
- Post-deploy: a second developer reviews the BUILD.md section and replaces `Reviewed by: pending` with name, date and PR.
- Post-deploy: replace `<repository-url>` in the README clone step if a fixed URL is wanted.
- No configuration to set. `.env.example` holds placeholders only.

## Known limitations
- The README is verified end to end by US-139, not by this story.
- The full text of ADR-004, 006, 007, 008, 009 and 012 lives in the original Zephyr repository, not here.
- The BUILD.md section stays `Reviewed by: pending` until a second developer reviews it.
- The `build/prompts/feature-section.md` template example uses `## FT-nnn — ...`, while the real section uses the `## F{nn} — ... (FT-code)` form. The template should be aligned.
- `tests/docs.test.mjs` does not yet assert the real ADR titles or the index note.
