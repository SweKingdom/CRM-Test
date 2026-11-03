# US-145: Per-project time approvers and delegation
**Status:** Ready for UAT · **Category:** development · **Points:** 5 · **Sprint:** not recorded

## Summary
Projects can now have named time approvers, and an approver can delegate approval rights to another user for a date range. Approver rights grant read access to that project's time entries, so time can still be approved when the usual approver is away. A consultant can never approve their own entries.

## What was built
- `Data/Migrations/0005_Approvers.cs`: tables `project_approver` (PK `project_id, user_id`, no project foreign key until US-161) and `approval_delegation` (checks `to_date >= from_date` and `delegator_id <> delegate_id`, index on `delegate_id, from_date, to_date`). Used through SQL only; the EF model is unchanged.
- `Permissions/Approvers.cs`: five endpoints (approver PUT/DELETE, delegation POST/GET/DELETE) and the helpers `ApproverScopeAsync`, `IsApproverOfAsync` and `EnsureNotSelfApproval`.
- `Endpoints.cs`: `MapApprovers()` registered in `MapZephyrEndpoints()`, so the endpoint inventory test covers the new routes.
- `Zephyr.Api.Tests/Permissions/ApproverTests.cs`: one test per AC, named `AcN_...`.
- `docs/permissions.md`: section "Approvers and delegation".

## How it works
- **AC 1 Approver set:** an Admin calls `PUT /api/v1/projects/{id}/approvers/{userId}` to add an approver (204, idempotent). `DELETE` removes the approver (204, idempotent). Both write an audit row. Other roles get 403. An unknown user gives 404 on PUT. The PM-of-project rule is deferred to US-161.
- **AC 2 Delegation:** `POST /api/v1/delegations` with `delegateId`, `fromDate`, `toDate` returns 201. The delegate holds approver rights on the delegator's own approver projects when `fromDate ≤ today ≤ toDate`. `today` is the Europe/Stockholm date from `BusinessClock`, and both ends are inclusive. Invalid input returns 422: `toDate` before `fromDate`, delegate equal to delegator, or a delegator who is not an approver of any project. An Admin may pass `delegatorId`; a non-Admin passing another person's id gets 403.
- **AC 3 Self-approval:** `Approvers.EnsureNotSelfApproval(entryUserId, callerId)` throws a 403 problem when both ids match. The approve endpoint that calls it is built in US-166/US-170.
- **AC 4 Consultant isolation:** `ApproverScopeAsync` returns an empty scope for a user who is not an approver. The 404 on another person's time entry is applied by the time-entry reads in US-166/US-170.
- **AC 5 Revoke:** `DELETE /api/v1/delegations/{id}` sets `revoked_at` (204). The delegate loses the scope on the next request, because nothing is cached. A second DELETE, an unknown id, or a delegation that is not the caller's returns 404.
- **AC 6 Not transitive:** a user whose only approver rights are delegated gets 422 on `POST /api/v1/delegations`.
- **AC 7 Listing:** `GET /api/v1/delegations` returns the caller's delegations as delegator or delegate, including revoked ones (`revokedAt`). An Admin sees all.

Active delegation = `revoked_at IS NULL AND deleted_at IS NULL AND today BETWEEN from_date AND to_date`. A delegate receives only the delegator's own `project_approver` projects.

## How to test
**Checks:** `npm run typecheck --silent`, `npm run lint --silent`, `npm run test --silent`, `npm run build --silent` (or `scripts/check`). DB-backed tests need `ZEPHYR_TEST_DB`; CI sets `ZEPHYR_REQUIRE_DB=1`.

**Automated:** `ApproverTests`, one test per AC.

**Manual checks** (API running and signed in; approver endpoints need an Admin):
- **AC 1:** `PUT /api/v1/projects/{id}/approvers/{userId}` as Admin returns 204. **Verify:** the `project_approver` row and an `approver_added` audit row exist. `DELETE` returns 204 and the row is gone. As PM or Consultant both return 403.
- **AC 2:** `POST /api/v1/delegations` as an approver returns 201. **Verify:** the delegate's approver scope contains the delegator's projects on `fromDate` and `toDate`, and not the day before or after. `toDate` before `fromDate`, delegate = self, or a delegator without approver rows return 422.
- **AC 3:** **Verify:** `Approvers.EnsureNotSelfApproval(x, x)` throws a 403 problem. The endpoint-level check is deferred to US-166/US-170.
- **AC 4:** **Verify:** `ApproverScopeAsync` for a non-approver is empty. The 404 on another person's time entry is deferred to US-166/US-170.
- **AC 5:** `DELETE /api/v1/delegations/{id}` returns 204. **Verify:** `revoked_at` is set, the delegate's scope no longer has the projects on the next request, and a second DELETE returns 404.
- **AC 6:** **Verify:** a user who only holds delegated rights gets 422 on `POST /api/v1/delegations`.
- **AC 7:** `GET /api/v1/delegations` **Verify:** delegator and delegate each see their own delegation, a bystander sees `[]`, and an Admin sees all.

## Deployment notes
- **Migration:** `0005_Approvers` creates `project_approver` and `approval_delegation`. Apply it with the normal migration run. The Down step drops both tables.
- **Configuration:** none.
- **Permissions:** approver PUT/DELETE require the Admin role. Delegation routes require any of the seven roles in `PermissionSeeder.Roles`; the handlers scope the data to the caller.
- **Audit events:** `approver_added`, `approver_removed` (entity `project_approver`, entity id = project id, detail `{userId}`), `delegation_created`, `delegation_revoked` (entity `approval_delegation`).

## Known limitations
- No project table or PM column yet, so the project's PM cannot manage approvers. Admin-only is the interim rule. US-161 adds the PM rule, the foreign key on `project_approver.project_id`, and the 404 for an unknown project (PO answer #151).
- The endpoint-level 404 on time entries and the 403 on self-approval are applied in US-166/US-170 (PO answers #152, #153). US-166 and US-170 must also cover the rule that another person's time entry returns 404 unless the caller is its approver or delegate.
- The `PermissionService` OR-predicate is not built; US-144, US-166 and US-170 consume the helpers.
- No multi-level approval chains.
- DB-backed `ApproverTests` are skipped locally without PostgreSQL. They first run in CI with `ZEPHYR_REQUIRE_DB=1`.
