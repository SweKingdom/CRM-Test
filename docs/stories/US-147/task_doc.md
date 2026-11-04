# US-147: Search backend: full-text with trigram fallback
**Status:** Ready for UAT · **Category:** development · **Points:** 5 · **Sprint:** n/a (Feature FT-113, Epic EP-103)

## Summary
Salespeople can find accounts (and, later, contacts, leads, opportunities and projects) by partial or slightly wrong text, with accents ignored. The search runs a PostgreSQL full-text step first and a trigram step when fewer than 5 results are found, and it only returns records the caller may read. The search index is kept in sync by database triggers in the same transaction as each change.

## What was built
- `Migrations/0004_SearchDocument.cs`: `f_unaccent`, `f_search_norm`, table `search_document` (GIN indexes on `tsv` and on `text gin_trgm_ops`), `search_upsert`, helper `search_install_trigger`, account trigger and backfill.
- `Search/SearchEndpoints.cs`: `GET /api/v1/search` with validation, per-type read scope from `IPermissionService.Scope`, full-text step and trigram step.
- `Endpoints.cs`: `app.MapSearch()`, wired in `MapZephyrEndpoints`.
- `tools/search-benchmark/bench.mjs` and `npm run bench:search`: 100 benchmark queries, p95 check, optional `--explain`.
- `scripts/seed.mjs`: volume seed ends with `REINDEX TABLE search_document` and `ANALYZE search_document`.
- Tests: `Search/SearchTests.cs` (one group per AC, leak tests per type in both steps), `Permissions/MatrixCoverageTests.cs` (route listed as outside the matrix).

## How it works
- **AC 1 Accent-insensitive:** text is normalised (unaccented, lower-cased, punctuation turned into spaces) both when stored and when queried. `q=malmo` finds "Malmö …".
- **AC 2 Prefix and typo:** prefixes and a single typo are matched by the trigram step, which uses `word_similarity` with threshold 0.3 (PO #146).
- **AC 3 Fallback:** if the full-text step returns fewer than 5 items, the trigram step runs and `fallback=true`. If the limit is 5 or less and full-text fills it, the trigram step is skipped and `fallback=false`.
- **AC 4 Permissions:** the read scope per entity type comes from the caller's `read` cell of the permission matrix (`@org`, `@own`, `@team`, `@country`, `none`). The filter is applied in both steps before ranking and limit (PO #76). Account owner is `created_by`; account country is the contracting country. Accounts without country are visible to org-wide scopes only. Callers without a matrix role, or with scope `none` for all types, get 403.
- **AC 5 Speed:** indexes `ix_search_document_tsv` and `ix_search_document_text_trgm` serve both steps. The p95 measurement is run with `npm run bench:search` on the local stack (optional, PO #144).
- **AC 6 Freshness:** the trigger writes the `search_document` row in the same transaction as the change.
- **AC 7 Soft delete and anonymise:** when `deleted_at` is set, the trigger removes the row; clearing it re-adds the row. The helper's `p_hidden` argument handles `anonymised_at` for contact and lead. Tested on account; the anonymisation path is tested on a probe table (PO #145).
- **AC 8 Input:** `q` shorter than 2 or longer than 100 characters returns 400 problem+json naming the field.
- **Ranking (PO #76):** exact title, title prefix, `ts_rank_cd` plus a recent-activity boost (`0.05 / (1 + age_days / 30)` on `updated_at`), entity weight (account 4, opportunity 3, contact 2, lead 2, project 1), then `updated_at` descending. Trigram step: `word_similarity` descending, then weight, then `updated_at`.
- **Snippet:** subtitle, else title (PO #146).

## How to test
**Checks:** `npm run typecheck --silent && npm run lint --silent && npm run test --silent && npm run build --silent`. DB tests need PostgreSQL: set `ZEPHYR_TEST_DB` to the Compose connection string.

- **AC 1:** create "Malmö Stad" and others, call `GET /api/v1/search?q=malmo`. **Verify:** "Malmö Stad" is in the first 5 items; `q=MALMÖ` and `q=orebro` also work.
- **AC 2:** `q=eri` and `q=Ericsonn`. **Verify:** "Ericsson AB" is in the top 5 for both.
- **AC 3:** with 6 "Nordic …" accounts and "Nordik Hotel": `q=nordic` → `fallback=false`, 6 items, no "Nordik Hotel"; `q=nordik` → `fallback=true`, "Nordik Hotel" first. **Verify:** the `fallback` flag and item lists.
- **AC 4:** run `Ac4_Every_entity_type_is_filtered_by_the_scope_the_matrix_gives_the_caller_in_both_steps`. **Verify:** for each of the 5 types and each role only in-scope records appear, for a normal and a typo query. Callers without a matrix role get 403; `types=` naming only unreadable types returns an empty list.
- **AC 5:** `npm run seed -- --volume`, sign in as Management, then `ZEPHYR_BENCH_COOKIE="zephyr.session=..." npm run bench:search -- --explain`. **Verify:** output ends with `bench: PASS, 95 of 100 searches within 500 ms` and the plans show both GIN indexes.
- **AC 6:** insert an account in a transaction and count `search_document` rows before commit. **Verify:** 1 row inside the transaction, 0 after rollback; a renamed account is found by its new name at once.
- **AC 7:** `update account set deleted_at = now() where id = ...`, then `= null`. **Verify:** the `search_document` row disappears and returns.
- **AC 8:** `q=a`, a 101-character `q`, `limit=51`, `types=person`. **Verify:** 400 `application/problem+json` naming the field.

## Deployment notes
- **Pre-deploy:** none.
- **Post-deploy:**
  - The API applies `0004_SearchDocument` at start. It needs the `pg_trgm` and `unaccent` extensions from the baseline migration.
  - The migration was edited after its first draft (account `owner_id` = `created_by`). A local database that applied the earlier draft must be rebuilt (`docker compose down -v`) or the account trigger reinstalled with `search_install_trigger('account', 'account', 'new.name', 'new.country_code', p_owner_id => 'new.created_by', p_account_id => 'new.id', p_country_code => 'new.country_code')`.
  - After a bulk load, run `npm run seed -- --volume`, or run `REINDEX TABLE search_document; ANALYZE search_document;` by hand.
- **Verification:** `npm run bench:search -- --explain` with `ZEPHYR_BENCH_COOKIE` set to a Management session cookie.
- **Configuration:** none new.

## Known limitations
- Only the account table exists. Contact, lead, opportunity and project triggers are added by their own stories, each with a trigger test (soft delete, restore, hard delete, anonymisation) (PO #145).
- `GET /api/v1/search` is not in `docs/api/openapi.json`; the generator has no search operation yet.
- The ~56,000-row p95 measurement was not run in this environment (no PostgreSQL stack). It is optional and run on the local stack (PO #144); full-volume benchmark is in US-150.
- Out of scope: external search engines.
