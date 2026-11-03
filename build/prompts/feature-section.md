# BUILD.md feature section template

Every merged feature gets one section in `BUILD.md`, added in that feature's pull request and reviewed by a second developer. Copy the block below, set the `##` title, and fill all six parts. This file is the single source for the part names: `tests/docs.test.mjs` reads the `###` headings from here.

```markdown
## FT-nnn — Feature title (US-nnn, US-nnn)

### Goal
What the feature does and why, in two or three sentences.

### Prompts used
The prompts or story texts given to the AI team, and the review feedback that changed the result. Say so plainly when none was recorded.

### Decisions
Choices made and the alternatives rejected, one bullet each. Link ADRs by id.

### Files
The files and modules added or changed, grouped by area.

### How to verify
The commands and checks that prove the feature works, as copy-pasteable blocks.

### Reviewer sign-off
Reviewed by: <second developer> on <YYYY-MM-DD>, <pull request link>
```

Use `Reviewed by: pending` until the second developer has reviewed it. The author never signs off their own section.
