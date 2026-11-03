# Permissions: roles, scopes and the rules matrix

Who may do what in Zephyr (US-143, F06-S01). This page describes the model; the engine that applies it comes in a later story.
The single source of truth is [`permission-matrix.csv`](../src/backend/Zephyr.Api/Permissions/permission-matrix.csv). At API start,
after the migrations, `PermissionSeeder` checks it and loads it into the `role` and `role_permission` tables. There is no screen or
endpoint that edits the matrix: a change is a pull request on the CSV (and on the table below) plus a deploy.

## Roles

Seven roles. A user holds one or more; the keys are the values stored in `app_user.roles` (spelled exactly like this).

| Key | Who |
|---|---|
| `Salesperson` | Sells and owns accounts, contacts and opportunities |
| `BDR` | Business development representative: works and converts leads |
| `PM` | Project manager and time approver |
| `Consultant` | Delivers projects and reports time |
| `Finance` | Reopens approved time, locks periods, exports approved billable hours |
| `Management` | Reads everything for dashboards |
| `Admin` | Sets the system up (users, roles, reference data, metadata) |

`Integration` (the API client that loads data) is not a user role and is not in the matrix.

## Actions and scopes

Actions: `read`, `create`, `update`, `delete` (a soft delete) and `approve` (approve or reject, for example time, or a retention run).
Each (role, entity, action) has one scope:

| Scope | The caller may act on |
|---|---|
| `none` | Nothing. A cell that is not listed is `none` too. |
| `own` | Records the caller owns or created, or, for the project entities, records tied to the caller's projects (see Project-scoped access) |
| `team` | Records of the caller's team (see Team) |
| `country` | Records whose contracting country equals the caller's `app_user.home_country`. There is no separate business-unit scope (PO answer #90). |
| `org` | Every record |

When a user holds several roles, the widest scope per cell wins (the union). The order is `none` < `own` < `team` < `country` < `org`.

## Team

A team is the users who have the same non-null `app_user.manager_id`, plus that manager. A user whose `manager_id` is null belongs to
no team and is only in the team of anyone who names them as manager. An admin sets `manager_id`; there is no team table.
It applies to the `team` scope on leads (BDR teams).

## Project-scoped access

Access that follows a project is not the manager team above. It is defined by who is on or responsible for the project:

- **Project manager:** the person who manages the project.
- **Project team:** the people listed as `project_team_member` on the project.
- **Approver:** the people who approve time on the project (`project_approver`, and delegates, from US-145).

For the project entities the scopes mean:

| Entity | Role | Scope | Meaning |
|---|---|---|---|
| `project` | Salesperson | `own` | Projects of accounts the person owns |
| `project` | PM | `own` | Projects the person manages |
| `project` | Consultant | `own` | Projects the person is on |
| `project_team_member` | PM | `own` | Team members of projects the person manages |
| `project_team_member` | Consultant | `own` | The person's own membership rows |
| `opportunity`, `meddpicc` | PM | `own` | The deal behind a project the person manages |
| `time_entry` | PM | `team` | Entries on projects where the person is manager or approver (the project team, not the manager team) |

Self-approval is not allowed whatever the matrix says: the engine refuses to let a person approve their own time.

## Entities

`account`, `contact`, `lead`, `opportunity`, `meddpicc`, `activity`, `project`, `project_team_member`, `skill`, `time_type`,
`time_entry`, `service_request`, `app_user`, `api_token`, `reference_data`, `audit_log`, `retention_rule`, `import_run`, `dsar_request`.

7 roles x 19 entities x 5 actions = 665 rows in the CSV: every combination is written out, including `none`.

## Rules behind the matrix

From the product owner's answer #90:

- **Accounts and contacts** are readable by every role across the organisation, so people find existing customers instead of creating duplicates.
- **Salesperson** edits own accounts, contacts and opportunities, reads leads and opportunities in their country, closes own deals as Won or Lost, and reads projects of their own accounts. Time: own only.
- **BDR** reads accounts and contacts and creates them; edits own and team leads (including convert and disqualify, which are `update`); creates opportunities from conversion and reads own. No projects. Time: own only.
- **PM** reads accounts and contacts, reads the deals behind their projects, edits projects they manage, reads time on their projects and approves or rejects it. Own time too.
- **Consultant** reads accounts and contacts and the projects they are on; time: own only.
- **Finance** reads accounts, contacts, opportunities, projects and time across the organisation. `update` on `time_entry` is for reopening approved entries and locking or unlocking periods; it changes an entry's status only, never hours or notes (the engine enforces that). Exporting approved billable hours is `read`.
- **Management** reads everything for dashboards and writes nothing except its own time.
- **Admin** reads accounts, contacts, leads, opportunities, projects and time across the organisation (needed for DSAR and retention reviews) but cannot edit business records or approve time. Admin manages users, API tokens, reference data and metadata, approves retention runs (`approve` on `retention_rule`) and runs DSAR export and erase (`dsar_request`).
- Every read and change by Admin and Finance on other people's time is written to the audit log. That is a separate story, not part of this matrix.

## Matrix

R = read, C = create, U = update, D = delete, A = approve; `—` = none. The test `MatrixTests` checks that this table equals the CSV.

| Entity | Salesperson | BDR | PM | Consultant | Finance | Management | Admin |
|---|---|---|---|---|---|---|---|
| account | R:org C:own U:own | R:org C:own | R:org | R:org | R:org | R:org | R:org |
| contact | R:org C:own U:own | R:org C:own | R:org | R:org | R:org | R:org | R:org |
| lead | R:country | R:team C:own U:team | — | — | — | R:org | R:org |
| opportunity | R:country C:own U:own | R:own C:own | R:own | — | R:org | R:org | R:org |
| meddpicc | R:country C:own U:own | R:own | R:own | — | R:org | R:org | R:org |
| activity | R:own C:own U:own D:own | R:own C:own U:own D:own | — | — | — | R:org | R:org |
| project | R:own | — | R:own U:own | R:own | R:org | R:org | R:org |
| project_team_member | — | — | R:own C:own U:own D:own | R:own | R:org | R:org | R:org |
| skill | R:org | R:org | R:org | R:org | R:org | R:org | R:org C:org U:org D:org |
| time_type | R:org | R:org | R:org | R:org | R:org | R:org | R:org C:org U:org D:org |
| time_entry | R:own C:own U:own D:own | R:own C:own U:own D:own | R:team C:own U:own D:own A:team | R:own C:own U:own D:own | R:org U:org | R:org C:own U:own D:own | R:org |
| service_request | R:org C:own U:own | R:org C:own U:own | R:org C:own U:own | R:org | R:org | R:org | R:org |
| app_user | R:org | R:org | R:org | R:org | R:org | R:org | R:org C:org U:org |
| api_token | — | — | — | — | — | — | R:org C:org U:org D:org |
| reference_data | R:org | R:org | R:org | R:org | R:org | R:org | R:org C:org U:org D:org |
| audit_log | — | — | — | — | — | — | R:org |
| retention_rule | — | — | — | — | — | — | R:org C:org U:org D:org A:org |
| import_run | — | — | — | — | — | — | R:org C:org |
| dsar_request | — | — | — | — | — | — | R:org C:org U:org |

## Integrity

- Seeding stops, and the API does not start, when the CSV has an unknown role, entity, action or scope, a row with the wrong number of columns, or a duplicate (role, entity, action). The error names every bad line and nothing is written to the database.
- After a seed `role` has the 7 rows above and `role_permission` has one row per CSV row (`none` rows included). Seeding again changes nothing. Rows no longer in the CSV are removed.
- An action that is not listed for a role and entity is `none`.
- Every value in `app_user.roles` must be a `role.key`. The column is `text[]`, so there is no foreign key; a test checks it over the seeded data.

## Assumptions and open points for PO sign-off

The cells below are not in answer #90 and were filled in by the developer; please confirm or correct them.

- `delete` is granted only where a record is a personal working item: own activities, own time entries, team members of a PM's projects, and Admin-managed setup data. Everything else is deactivated or changed by `update`.
- `activity`, `meddpicc`, `project_team_member`, `skill`, `time_type`, `service_request`, `app_user`, `reference_data`: not in answer #90. `meddpicc` follows `opportunity`; `activity` is own for Salesperson and BDR and readable by Management and Admin; `skill`, `time_type`, `reference_data` and `app_user` (a name directory for pickers) are readable by everyone and maintained by Admin; `service_request` is visible to all staff (design assumption) and created and updated by Salesperson, BDR and PM.
- Management has `create`, `update` and `delete` on `own` time entries only ("no writes beyond their own records"). Finance and Admin have no own-time rights in this matrix.
- Salesperson has no `create` on leads (answer #90 lists read in their country only).
