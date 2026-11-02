# Design critique

**Verdict: revise.** Issues 1–4 would cause rework or defects if built as written. The rest can be fixed in the stories.

## 1. Architecture

1. **`approval_delegation` is defined twice, and the two definitions disagree.** (FT-112 vs FT-119)
   - FT-112 (US-145) defines `approval_delegation(delegator_id, delegate_id, from_date, to_date, revoked_at)` with API `POST /api/v1/delegations` and `DELETE /api/v1/delegations/{id}`.
   - FT-119 (US-166) defines `approval_delegation(delegator, delegate, valid_from, valid_to, conventions)` with `/approval-delegations` CRUD.
   - The result is two migrations for one table, two route sets and two sets of column names.
   - Change: US-145 owns the table and the routes. Add one column spelling and one route set. FT-119 reuses them, and US-166 keeps only the approval-queue behaviour.
2. **Test project name differs.** FT-111 to FT-118 and FT-122 use `Zephyr.Api.Tests`. FT-119 and FT-120 use `Zephyr.Tests` (the module map has `Zephyr.Api.Tests`). Pick `Zephyr.Api.Tests` and correct FT-119 and FT-120, otherwise a second test project and duplicate fixtures appear.
3. **Migration mechanism contradicts itself.** The key decision is EF Core migrations applied at API start (US-129). FT-124 specifies `NNN_governance.sql`, and the module map has `Seed/*.sql`. Decide one rule: EF migrations for schema, `Seed/*.sql` for data only. FT-124 should be an EF migration.
   - Also settle how the `import` CLI mode of `Zephyr.Api` interacts with start-up migrations. It should migrate first and have no web host.
4. **The `custom jsonb` column and the metadata validator.** `custom jsonb` is defined in the US-129 baseline conventions. FT-125 requires `MetadataService.ValidateCustom` in every entity write endpoint, but FT-113 to FT-124 do not mention it. Either add a shared write-pipeline hook early (US-133) or accept that FT-125 retrofits about 10 endpoints. Name the owner.
5. **Background jobs are not consolidated.** `StaleProjectJob` (FT-117), `ReminderJob` (FT-121) and the FT-124 retention expiry "by the FT-121 daily job" each carry their own scheduling. Define one shared hosted-service/advisory-lock helper in FT-121 and make FT-117 and FT-124 use it. FT-121 has no holiday calendar although FT-120 adds `public_holiday`. Reuse it, or state that the difference is deliberate.
6. **Frontend paths differ.** FT-125 uses `metadata/` and `admin/`, FT-113 uses `src/frontend/src/{shell,pages,lib}`. Low. Align the paths when the stories are refined.

## 2. Security and data

1. **No CORS or CSRF decision for the SPA.** The cookie is `SameSite=Lax`, and the Vite dev server on another port calls a state-changing API with PATCH and DELETE. Specify either a dev proxy (same origin) or CORS with credentials restricted to the frontend origin. Add an antiforgery or custom-header check for cookie-authenticated writes. Add a test.
2. **Public endpoints and default deny.** The FT-112 rule "every endpoint must declare entity+action, test enforces" does not list `/health`, `/auth/*` and `POST /api/v1/public/*` as explicit anonymous exceptions. Add an allow-list the test checks. Public lead creation also needs a defined `created_by` (a system user) for the audit columns.
3. **`PUT /{plural}/by-external-id/...` has no permission definition (US-135).** A generic route over all entities needs an entity-to-permission mapping, and a rule for who may call it (Admin or an integration role?). It also needs a rule for a soft-deleted record with the same `external_id`: reuse, restore or 409. The import CLI bypasses API permissions, so state that it is a trusted operator tool.
4. **DSAR and anonymise coverage is incomplete (FT-124).** It lists only contact and lead, plus the search index. Personal data also lives in `activity_participant.email`, activity notes and subjects, `service_request.requester_email` and description, `reminder_log.reason/error`, `import_reject.detail`, `external_id` values and `custom` jsonb. Define the full list of personal-data columns, make the shared anonymise routine cover them, and add one test that scans for the sentinel value after erase.
5. **Seeds use `ON CONFLICT DO NOTHING`.** A changed label, flag or `exit_criteria` never reaches an existing database, yet several risks say "seed-only change". Use `DO UPDATE` for reference tables, or say that a database reset is required.
6. **Soft delete versus search and triggers.** The search triggers must remove or hide the `search_document` row on `deleted_at` and on anonymise. The design says nothing on this beyond FT-124's test AC. Add it to US-147.
7. **MCP preview.** The `previewId` must be bound to the calling token's user. State it explicitly, with a test.
8. Passwords and secrets: fine. The realm template rendering and the `.env` handling are acceptable.

## 3. Delivery and testability

1. **Reference seeds (US-130) cannot run on the US-129 baseline.** US-129 creates only conventions, extensions, `external_id` and `audit_log`. US-130 seeds stages, call outcomes, time types, countries, currencies and MEDDPICC elements, but those tables arrive in later features (FT-114, FT-116, FT-118 and others). The US-130 acceptance criteria are therefore untestable in FT-108. Either US-130 creates the reference tables in its own migration (and the later stories reuse them), or US-130 is limited to country and currency and the rest moves to the owning stories. The same applies to US-131: its ~56,000 / ~105,000 counts cannot be proven until all domain tables exist. Split it into a framework with per-entity files, and run the full count check in US-150.
2. **Time zone inconsistency.** FT-118 evaluates "future" in UTC. FT-119 and FT-120 use Europe/Stockholm for locks, "today" and the week. Entries made on a Sunday evening or Monday morning will be accepted or rejected differently by the different features. Use Europe/Stockholm everywhere, through one `TimeProvider`/`IsLocked` seam.
3. **How tests get a database in CI (US-132).** The design says integration tests run against the Compose DB and that CI has no secrets. It does not say how `check.yml` starts Postgres (service container?), nor how tests isolate state (per-test transaction, a Respawn reset, or a schema per run). Without this, "independent and repeatable" fails. Specify it in US-132 and US-129. Keycloak is needed only for e2e.
4. **Story order is hard-wired, but the stories are all "Ready for Sprint" with no dependency links.** The sequences that must be enforced:
   - US-155 after US-156/157.
   - US-160 after the US-158 close hook.
   - US-162 after US-163.
   - US-166 after US-145 and US-165.
   - US-169 AC4 after US-171.
   - US-176/US-175 after the FT-113 search and the FT-114 `source` column.
   - FT-119 and FT-120 after FT-118.
   - FT-113 stories use the FT-112 predicate builder.
   - US-185 needs all domain tables.
   Record the dependencies on the items.
5. **Test fixtures.** The hand-derived reference-month fixture (US-169) is the right approach, but the review step has no named owner. Name the reviewer (PO or Finance) in the story.
6. **Open assumptions blocking acceptance.** Roles, matrix values, stage set, the PM of a new project, time types (5 of 11 known) and the seed lists are "assumptions". Mark which stories are blocked until PO sign-off, so QA does not test against a guess.
7. Project has no checks configured today. US-132 must be the first story merged, or the others cannot pass `run_checks`.

## Ranked summary
1. Seed/table ordering (US-130/131 vs domain migrations).
2. Duplicate `approval_delegation` (FT-112 vs FT-119).
3. UTC vs Europe/Stockholm in time rules.
4. Migration tool contradiction and the test project name.
5. CORS/CSRF, DSAR coverage, upsert permissions, CI database.
