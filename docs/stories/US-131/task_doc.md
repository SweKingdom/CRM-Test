# US-131: Synthetic volume seed (~56,000 records, ~105,000 time entries)
**Status:** Ready for UAT · **Category:** script · **Points:** 5 · **Sprint:** n/a

## Summary
Adds a synthetic data generator that loads Dynamics-scale volume (about 56,000 searchable records and about 105,000 time entries) into the local database, so performance targets can be measured on localhost. All data is synthetic and marked as such. The loader skips entities whose tables do not exist yet.

## What was built
- `scripts/seed.mjs`: `npm run seed -- --volume [--seed N] [--anchor YYYY-MM-DD] [--reset]`. Skips missing tables with `SKIP <entity>: table missing`, refuses to run twice without `--reset`, removes only synthetic rows on reset, runs one transaction per entity, and runs ANALYZE.
- `tools/search-benchmark/generate/_helpers.sql`: pg_temp helpers (hash, uid, target, pick, typo, marker). Values derive from md5(seed || entity || n).
- `tools/search-benchmark/generate/01_account.sql` to `06_time_entry.sql`: one generator per entity. `06_time_entry.sql` also fills `user_id` and `time_type`.
- `tools/search-benchmark/generate/targets.json`: row targets and totals.
- `tools/search-benchmark/data/names.json`, `cities.json`, `typos.json`, `time_types.json`: input lists. `time_types.json` keys match the reference seed.
- `src/backend/Zephyr.Api.Tests/Seeds/VolumeSeedTests.cs`: DB-backed tests for AC 1 to AC 3. The checksum test loops over every existing table.
- `tests/volume.test.mjs`: Node tests for AC 4 (targets), file layout, time entry columns and time type keys, and option validation.
- `package.json`, `README.md`: volume seed script and section.

## How it works
- **AC 1 (partial schema):** For each entity file, the loader checks whether the table exists. Missing tables print `SKIP <entity>: table missing`. Existing tables load, and the command exits 0. On a fresh schema with only FT-108 tables, `account` loads 6,000 rows.
- **AC 2 (repeatable):** Values come from `md5(seed || entity || n)`, not `setseed`/`random()`, so output does not depend on the query plan (PO answer #128). Checksum per entity is `md5(string_agg(id::text || coalesce(name,''), ',' ORDER BY id))`. For time entries the checksum is `id || user_id || work_date || duration_minutes || time_type`, with nullable columns wrapped in `coalesce(..., '')` (PO answer #129).
- **AC 3 (marker and guard):** Every generated row has `custom->>'synthetic' = 'true'`. A second run without `--reset` exits 1 and names `--reset` in the message. `--reset` deletes only rows with the synthetic marker before loading.
- **AC 4 (targets):** `targets.json` states 56,000 searchable records, 105,000 time entries, and 18,495 time entries in the trailing 12 months. Time entries: the first 18,495 are spread over [anchor − 12 months, anchor]; the rest over the 1,460 days before that window.
- **Typos:** Values whose hash is divisible by 50 (about 2%) get a typo. The original spelling is stored in `custom->>'typo_of'`.

## How to test
Run the unit tests and checks:
- `npm run typecheck && npm run lint && npm test && npm run build`
- DB-backed tests need `ZEPHYR_TEST_DB`. CI sets `ZEPHYR_REQUIRE_DB=1`.

Manual checks:
- **AC 1:** On a migrated database with only the FT-108 tables, run `npm run seed -- --volume`. **Verify:** it prints `SKIP contact: table missing` (and lead, opportunity, project, time_entry), loads 6,000 accounts, and exits 0.
- **AC 2:** Load two fresh databases with `--seed 7 --anchor 2026-06-15`. **Verify:** the account checksum is identical in both (`VolumeSeedTests.Ac2`).
- **AC 3:** **Verify:** `select count(*) from account where custom->>'synthetic' is distinct from 'true'` counts only hand-made rows. A second run without `--reset` exits 1 with a message naming `--reset`. With `--reset`, hand-made rows survive.
- **AC 4:** **Verify:** `tools/search-benchmark/generate/targets.json` states 56,000 / 105,000 / 18,495 (`tests/volume.test.mjs`). Real counts are checked by US-150.

## Deployment notes
- No migrations and no schema changes.
- Run `npm run seed -- --volume` after the domain tables exist. Use `--reset` to regenerate.
- Assumed columns for tables not yet built: `contact(name, account_id)`, `lead(name)`, `opportunity(name, account_id)`, `project(name, account_id)`, `time_entry(project_id, work_date date, duration_minutes, notes, user_id, time_type)`. All carry `custom jsonb`.
- Assumptions to align with owning stories: `time_entry.time_type` holds `time_type.key`; `user_id` is a synthetic uuid with no foreign key to an app user.

## Known limitations
- The DB-backed tests (`VolumeSeedTests`) could not run locally because PostgreSQL was not available. They were reviewed by hand. The first real run is in CI with `ZEPHYR_REQUIRE_DB=1`.
- Only `account` exists today. The SQL for contact, lead, opportunity, project and time_entry is untested.
- The real count check (≈56,000 / ≈105,000 / 18,495) is deferred to US-150.
- Reference-month fixture is out of scope (see approval stories).
