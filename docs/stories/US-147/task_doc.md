# US-147: Search backend: full-text with trigram fallback
**Status:** Ready for UAT · **Category:** development · **Points:** 5 · **Sprint:** not recorded

## Summary
Salespeople can now find accounts, contacts, leads, opportunities and projects by partial or slightly wrong text, with accents ignored. Results are limited to records the caller is allowed to read, and the search index is updated in the same transaction as each change. This story delivers the backend endpoint `GET /api/v1/search`. Only accounts are indexed so far; the other entity types are added by their own stories.

## What was built
- `Migrations/0004_SearchDocument.cs`: functions `f_unaccent` and `f_search_norm`, table `search_document` (GIN indexes on `tsv` and on `text gin_trgm_ops`), function `search_upsert`, helper `search_install_trigger`, the account trigger (`owner_id` = `created_by`, `country_code`), and a backfill of existing accounts.
- `Search/SearchEndpoints.cs`: `GET /api/v1/search`. It validates input, reads the caller's scope per entity type from `IPermissionService.Scope`, runs the full-text step and, when needed, the trigram step. Declared with `RequireRoles` for every matrix role.
- `Endpoints.cs`: `app.MapSearch()`, registered in `MapZephyrEndpoints`.
- `Zephyr.Api.Tests/Search/SearchTests.cs`: one test group per AC, a per-role and per-entity-type leak test through both steps, ranking, injection, Respawn regression and route wiring. Includes a test that the account trigger sets the owner.
- `Zephyr.Api.Tests/Permissions/MatrixCoverageTests.cs`: `GET /api/v1/search` listed in `OutsideMatrix`.
- `tools/search-benchmark/bench.mjs` and `npm run bench:search`: 100 queries, p95, optional `--explain`.
- `scripts/seed.mjs`: the volume seed ends with `REINDEX TABLE search_document` and `ANALYZE search_document`.
- `package.json`: keeps both sides of the merge (`bench:search` and the bench check, plus the OpenAPI generator and test checks).

## How it works
- **Index sync (AC 6, AC 7):** a trigger on the table writes the `search_document` row in the same transaction as the change. A renamed account is found by its new name at once. A row with `deleted_at` set, or with `anonymised_at` set for contacts and leads, is removed from the index. Clearing `deleted_at` on a record that is not anonymised adds it back.
- **Normalisation:** stored text, titles and queries all go through `f_search_norm` (trim, punctuation to space, lower case, unaccent). This gives accent-insensitive matching (AC 1).
- **Full-text step:** runs first and ranks by exact title, then title prefix, then `ts_rank_cd` with a recent-activity boost (`0.05 / (1 + age_days / 30)` on `updated_at`), then entity weight (account 4, opportunity 3, contact 2, lead 2, project 1), then `updated_at` descending (AC 2).
- **Trigram step (AC 3):** runs only when the full-text step returns fewer than 5 items. It uses `word_similarity` (`q <% text`, threshold 0.3) and ranks by similarity, then weight, then `updated_at`. `fallback` is `true` when this step ran, even if it added nothing. When `limit` is 5 or less and the full-text step fills it, the step is skipped and `fallback` is `false`.
- **Snippet:** `subtitle`, or `title` when there is no subtitle.
- **Permissions (AC 4):** for each entity type the caller's `read` scope is applied in the WHERE clause of both steps, before ranking and limit: `@org`, `@own` (`owner_id` = caller), `@team` (`owner_id` in the caller's team), `@country` (`country_code` = home country). A type with scope `none` matches no row. A null home country or empty team matches no row. An account with no country is visible to org-wide scopes only.
- **Input (AC 8):** `q` is trimmed and must be 2 to 100 characters. Invalid `q`, `limit`, `types`, or a repeated or unknown parameter returns 400 problem+json.

## How to test
**Checks:** `npm run typecheck --silent && npm run lint --silent && npm run test --silent && npm run build --silent`. The DB tests need PostgreSQL: set `ZEPHYR_TEST_DB` to the Compose database connection string. CI sets `ZEPHYR_REQUIRE_DB=1`.

- **AC 1:** create "Malmö Stad" and other accounts, then `GET /api/v1/search?q=malmo`. **Verify:** "Malmö Stad" is in the first 5 items. `q=MALMÖ` and `q=orebro` also work.
- **AC 2:** `q=eri` and `q=Ericsonn`. **Verify:** "Ericsson AB" is in the top 5 for both.
- **AC 3:** with 6 "Nordic …" accounts and "Nordik Hotel". **Verify:** `q=nordic` gives `fallback=false`, 6 items, no "Nordik Hotel". `q=nordik` gives `fallback=true`, "Nordik Hotel" first, then the Nordic accounts.
- **AC 4:** run `Ac4_Every_entity_type_is_filtered_by_the_scope_the_matrix_gives_the_caller_in_both_steps`. **Verify:** for each of the 5 types and each role, only records in scope appear, for a normal query and a typo query. A caller without a matrix role gets 403. Asking only for an unreadable type returns an empty list.
- **AC 5:** `npm run seed -- --volume`, sign in as a Management user, then `ZEPHYR_BENCH_COOKIE="zephyr.session=..." npm run bench:search -- --explain`. **Verify:** output ends with `bench: PASS, 95 of 100 searches within 500 ms`, and the plans use `ix_search_document_tsv` and `ix_search_document_text_trgm`. (Optional, not run yet.)
- **AC 6:** insert an account in a transaction and count `search_document` rows before committing. **Verify:** 1 row inside the transaction, 0 after rollback.
- **AC 7:** `update account set deleted_at = now() where id = ...`, then set it to `null`. **Verify:** the `search_document` row disappears and comes back.
- **AC 8:** `q=a`, a 101-character `q`, `limit=51`, `types=person`. **Verify:** 400 `application/problem+json` naming the field.

## Deployment notes
- **Pre-deploy:** none.
- **Post-deploy:** the API applies migration `0004_SearchDocument` at start. It needs the `pg_trgm` and `unaccent` extensions from the baseline migration.
- **Existing local databases:** migration 0004 was edited after its first draft (account `owner_id` = `created_by`). A local database that already applied the draft must be rebuilt with `docker compose down -v`, or the trigger must be reinstalled with `select search_install_trigger('account','account','new.name','new.country_code', p_owner_id => 'new.created_by', p_account_id => 'new.id', p_country_code => 'new.country_code')`.
- **After a bulk load:** run `npm run seed -- --volume`, or run `REINDEX TABLE search_document; ANALYZE search_document;` by hand.

## Known limitations
- Only the `account` table is indexed. Contact, lead, opportunity and project triggers come with their own stories. Each of those adds one `search_install_trigger` call, passes `p_hidden` where needed (contact and lead: anonymisation), and tests soft delete, restore, hard delete and anonymisation (PO #145).
- AC 7 is tested on the account table. The anonymisation path (`p_hidden`) is tested on a probe table, since no table with `anonymised_at` exists yet.
- AC 4 leak tests seed `search_document` rows directly for all 5 types, because only accounts have tables. The account path also runs through its real trigger.
- The route is not in `docs/api/openapi.json`, because the generator has no search operation yet.
- The DB-backed tests were not executed locally (no PostgreSQL stack). They must be confirmed green in CI with `ZEPHYR_REQUIRE_DB=1`.
- The p95 benchmark over ~56,000 records was not run. `npm run bench:search` does it on the local stack (optional, PO #144).
- External search engines are out of scope.
