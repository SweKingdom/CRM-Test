# US-133: API core conventions: paging, filter, sort, errors, ETag
**Status:** Ready for UAT · **Category:** development · **Points:** 5 · **Sprint:** not recorded

## Summary
Every endpoint under `/api/v1` now follows the same rules for paging, filtering, sorting, errors, concurrency and audit. Integrators get predictable responses, and the UI uses the same API. Write requests are audited with the caller's application user, taken from the token.

## What was built
- `Common/Problems.cs`: error code catalogue, `ProblemException`, RFC 9457 `application/problem+json` writer, global exception handler (500 without stack trace, with `traceId`; concurrency conflicts map to 412; binding errors map to 400), problem responses for bare 401/403/404.
- `Common/ListQuery.cs`: `ListSpec<T>` allow-list, `ListQuery<T>` parsing and keyset `ToPageAsync`, `ListPage<T>` (`items`, `nextCursor`).
- `Common/ETags.cs`: strong ETag, `Ok` (ETag header, 304 on `If-None-Match`), `Set`, `RequireMatch` (428 when missing, 412 when stale).
- `Common/WriteHooks.cs`: `IWriteValidator`, `ModelState`, no-op default validator, `ReadBodyAsync`, `Run`, `WithWriteHooks()` (tags and guards write endpoints).
- `Common/BusinessClock.cs`: Europe/Stockholm business dates; a period is a calendar month `yyyy-MM`; `LockInstant(period)` is 00:00 Stockholm on the 3rd of the next month.
- `Common/ApiConventions.cs`: `AddApiConventions` / `UseApiConventions`, used by `Program.cs` and the tests.
- `Data/AuditInterceptor.cs`: audit user from the `zephyr_user_id` claim (`HttpCurrentUser`); timestamps truncated to microseconds.
- `Data/ZephyrDbContext.cs`: `updated_at` is a concurrency token. No schema change, no new migration.
- `Program.cs`: uses the conventions; keeps JSON console logging and `/health`; `public partial class Program` for tests.
- Tests: `ApiConventionsTests.cs`, `BusinessClockTests.cs`; `Zephyr.Api.Tests.csproj` adds Microsoft.AspNetCore.Mvc.Testing 8.0.11.

## How it works
**AC 1: Default paging.** A list call without `limit` returns at most 50 items and a `nextCursor`. The cursor holds the sort signature and the values of the last row. Following it returns the next page with no duplicates or gaps. `nextCursor` is null on the last page. `id` is appended to every sort as the tie-break.

**AC 2: Limit cap.** `limit` accepts 1 to 200 (default 50). `limit=500`, `limit=0`, negative, or non-numeric values return 400 naming `limit`.

**AC 3: PATCH concurrency.** A write on a single resource loads the row, requires `If-Match`, and checks it:
- No `If-Match`: 428.
- Stale ETag or weak ETag: 412.
- Matching ETag: 200 with a new `ETag`.
- The UPDATE is guarded by `updated_at`. A lost race returns 412.
- A GET of a single resource returns `ETag`. A matching `If-None-Match` returns 304.
- ETag format: `"<base64url sha256(id + updated_at ticks)>"`. `If-Match` accepts a list and `*`.

**AC 4: Validation problem details.** An invalid body returns 422 with `type`, `title`, `status`, `detail` and `errors[]` (`{field, code, message}`, one entry per failing field). Malformed JSON, an empty body or a non-object body returns 400.

**AC 5: Filter and sort.** `filter[field][op]=value` and `sort=-field` filter and order the list. Supported operators:

| Operator | Allowed on |
|---|---|
| `eq`, `ne`, `in` (comma-separated) | any allow-listed field |
| `gt`, `gte`, `lt`, `lte` | numbers and dates only |
| `contains` | text only (case-insensitive `ILIKE`; `%`, `_` and `\` are literal) |

Sort keys must be non-nullable columns. Unknown fields, operators, sort keys or query parameters return 400 naming the item. Only `limit`, `cursor`, `sort` and `filter[field][op]` are accepted. A repeated parameter returns 400. A cursor from a different sort, or one that does not parse, returns 400.

**AC 6: Audit user.** On writes, `created_by` and `updated_by` come from the `zephyr_user_id` claim of the authenticated principal. The value is parsed as the app user id. The request body is never used. Anonymous, missing or non-GUID claims map to `SystemUser`. The IdP `sub` and NameIdentifier claims are ignored, because they are the provider's id, not the app user's id.

**Write hooks.** Every POST, PUT and PATCH endpoint under `/api/v1` must end with `.WithWriteHooks()`. The test `Every_write_endpoint_of_the_real_app_calls_the_write_hooks` fails for an untagged write endpoint. A 2xx response without a `Run` call throws.

## How to test
**Checks:** `npm run typecheck --silent`, `npm run lint --silent`, `npm run test --silent`, `npm run build --silent` (or `run_checks`). DB-backed tests need PostgreSQL (`ZEPHYR_TEST_DB`). CI sets `ZEPHYR_REQUIRE_DB=1`, so they run there. Without PostgreSQL they are skipped locally. The tests build a small external-id API on the real conventions, because the project has no domain endpoints yet.

- **AC 1:** `Ac1_*` tests. **Verify:** seed 120 rows; `GET /api/v1/external-ids` returns 50 items and a `nextCursor`. Following the cursors gives 3 pages, 120 distinct ids, and a null `nextCursor` on the last page. A tampered cursor returns 400.
- **AC 2:** `Ac2_*` tests. **Verify:** `limit=500|201|0|-1|abc|` returns 400 problem+json naming `limit`; `limit=200` returns 200 with up to 200 items.
- **AC 3:** `Ac3_*` tests. **Verify:** PATCH without `If-Match` returns 428; stale or weak ETag returns 412; matching ETag returns 200 with a new `ETag` equal to the next GET's; `If-None-Match` equal to the current ETag returns 304.
- **AC 4:** `Ac4_*` tests. **Verify:** POST with empty fields returns 422 `application/problem+json` with `type,title,status,detail` and one `errors[]` entry per field; malformed JSON, empty body and non-object body return 400.
- **AC 5:** `Ac5_*` tests. **Verify:** `filter[system][eq]=fortnox&sort=-updatedAt` returns only fortnox rows, newest first. `ne`, `gt`, `lt`, `contains` (wildcards literal) and `in` work. Unknown field, operator, sort key or parameter returns 400 naming it.
- **AC 6:** `Ac6_*` tests. **Verify:** create as user A (`zephyr_user_id` claim) with `createdBy` of user C in the body, then patch as user B: `created_by` = A and `updated_by` = B. No token gives the system user. A token with only the IdP `sub` claim gives the system user.
- **Write hooks / BusinessClock:** `Every_write_endpoint_of_the_real_app_calls_the_write_hooks`, `Write_hook_check_*`, `BusinessClockTests` (DST days, midnight Stockholm, ISO week, lock instant).

## Deployment notes
- No migration. No schema change. The Respawn database reset is unaffected.
- Configuration: none new. Linux images need tzdata or ICU so that `Europe/Stockholm` resolves.
- Run the DB-backed tests in CI with `ZEPHYR_REQUIRE_DB=1` on first merge.
- Later token sign-in must set the `zephyr_user_id` claim (FT-111 / US-140; personal API tokens US-142). Until then, every authenticated write is audited as the system user.

## Known limitations
- Only the external-id test API uses the conventions. Domain endpoints follow in later stories.
- Sort keys must be non-nullable. Sorting on a nullable column (for example close date) needs NULLS LAST with `id` as tie-break, added in the story that needs it.
- Operators are restricted by field type (PO answer #113). This differs from the written AC 5, which allowed every operator on every field.
- Audit user comes from `zephyr_user_id`, not `sub` (PO answer #114). This differs from the written AC 6. Real sign-in does not set the claim until US-140.
- The DB-backed tests were skipped in the local run. Their first real run is in CI.
- No end-user-visible change. The user manual is not affected.
