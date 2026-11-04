# US-151: Activity data model and API with regarding validation
**Status:** Ready for UAT · **Category:** development · **Points:** 5 · **Sprint:** (not recorded)

## Summary
Adds one activity record for calls, meetings, notes, tasks and emails, with a "regarding" link to a lead, contact, account, opportunity, project or service request. The server derives the account from the regarding record, so every activity lands on the right account timeline. Call outcomes set the `connected` flag automatically.

## What was built
- `Zephyr.Api/Activities/ActivityEndpoints.cs`: routes under `/api/v1/activities` (POST, GET list with keyset paging, GET by id, PATCH, DELETE), SQL, and `MoveContactActivitiesAsync` (moves a contact's activities to a new account inside the caller's transaction).
- `Zephyr.Api/Activities/ActivityValidation.cs`: field, call outcome, participant and regarding validation; server-side derivation of `account_id` and `connected`.
- `Zephyr.Api/Data/Migrations/0005_Activity.cs`: EF migration creating `activity` and `activity_participant`, constraints and indexes. The down step drops objects in reverse order.
- `Zephyr.Api/Endpoints.cs`: `MapActivityEndpoints()` registration.
- `Seed/0001_reference_data.sql`: the 9 call outcomes with `counts_as_contact` (unchanged, now the source of truth).
- `Zephyr.Api.Tests/Activities/ActivityTests.cs`: integration tests per AC.
- `Zephyr.Api.Tests/Permissions/MatrixCoverageTests.cs`: activity permission probes; `activity` removed from Pending; `GET /activities/{id}` listed as outside the matrix.

## How it works
- **AC 1 (timeline membership):** An activity regarding an opportunity appears in `GET /activities?accountId=` for that opportunity's account. An activity with no regarding has no account and appears on no account timeline.
- **AC 2 (connected flag):** `connected` is set by the server from the call outcome. "Meeting booked" gives `true`, "No answer" gives `false`. A client-sent `connected` is rejected.
- **AC 3 (invalid regarding):** A regarding type outside the six values, or a record that does not exist or is not visible, returns 422 (RFC 9457). Nothing is stored.
- **AC 4 (client account ignored):** A body containing `accountId` returns 422. The stored `account_id` always comes from the regarding record.
- **AC 5 (contact moves):** `MoveContactActivitiesAsync` moves activities regarding the contact to the new account in the same transaction. A failure rolls back both the contact change and the move.
- **AC 6 (outcome rules):** `callOutcomeKey` on a non-call, or an unknown key, returns 422.

### Account derivation

| regarding_type | account_id |
|---|---|
| account | regarding_id |
| contact | contact.account_id (may be NULL) |
| opportunity | opportunity.account_id |
| project | project.account_id |
| service_request | service_request.account_id |
| lead | lead.account_id if converted, else NULL |
| none | NULL |

## How to test
**Checks:** `npm run typecheck --silent && npm run lint --silent && npm run test --silent && npm run build --silent`

**Activity tests (need PostgreSQL):** set `ZEPHYR_TEST_DB` to the Compose database connection string, then run `dotnet test src/backend/Zephyr.Api.Tests --filter "FullyQualifiedName~ActivityTests|FullyQualifiedName~MatrixCoverageTests"`. Set `ZEPHYR_REQUIRE_DB=1` so the tests fail instead of skipping when the database is missing.

- **AC 1:** POST an activity regarding an opportunity of account A, and a note without regarding. **Verify:** `GET /api/v1/activities?accountId=A` lists only the first; the note has `accountId` null.
- **AC 2:** POST a call with `callOutcomeKey` `meeting_booked`, then `no_answer`. **Verify:** `connected` is true, then false; a body with `connected` returns 422.
- **AC 3:** POST with `regardingType` `invoice`, and with a random `regardingId`. **Verify:** both return 422 (`application/problem+json`) and no row is stored.
- **AC 4:** POST with `accountId` in the body. **Verify:** 422; a valid create stores the account of the regarding record.
- **AC 5:** In one transaction, update a contact's account and call `ActivityEndpoints.MoveContactActivitiesAsync`. **Verify:** its activities move on commit; with a forced failure (trigger raising), both stay unchanged.
- **AC 6:** POST a note with `callOutcomeKey`, and a call with an unknown key. **Verify:** both return 422 and nothing is stored.

## Deployment notes
- No manual steps. Migration `0005_Activity` is applied at API start.
- Call outcomes come from `Seed/0001_reference_data.sql`.
- Activity routes are not yet in `docs/api/openapi.json`. Follow-up story is planned (see Known limitations).

## Known limitations
- **OpenAPI:** `docs/api/openapi.json` is not extended for the activity routes. The generator supports only list, write and delete operations. Product owner decision #167: a follow-up story ("Extend the OpenAPI generator to cover detail, search and custom action routes") is ranked next in the sprint. From then on, each story documents its own routes.
- **Regarding visibility (decision #165):** Only the `account` table exists. Regarding types whose table is missing (lead, contact, opportunity, project, service_request) return 422 "not found". The visibility predicate is not applied yet. Each story that creates one of these entities must add a visibility AC and test.
- **Opportunity test (decision #166):** The "regarding opportunity" timeline test uses a test-created stub `opportunity` table. The stub is dropped after each test. Switch to the real table and remove the stub when the opportunity story is merged.
- **Contact move (decision #164):** There is no `ContactEndpoints.cs` or contact table yet. `MoveContactActivitiesAsync` is built and tested against a stub `contact` table. The contact update endpoint must call it, and a new AC covers this in the contact story.
- **Lead conversion (assumption):** On lead conversion, activities regarding the lead must be re-pointed to the new account, contact or opportunity. This is to be handled by the conversion story.
- **Activities on participants:** Activities where a contact is only a participant do not move when the contact changes account.
- **Test run:** The DB-backed tests have not run locally. The first real run is in CI with `ZEPHYR_REQUIRE_DB=1`.
- **Out of scope:** Email and calendar sync; transcript capture automation.
