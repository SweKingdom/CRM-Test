# Requirements brief — Zephyr CRM, local edition

Source: Zephyr_CRM_requirements_local.pdf (compiled 2 Nov 2026). Where the BRD and team docs differ, the docs win (per the document).

## Goal
Sirocco Group's ~100 sales, delivery, finance and management staff get a lean, headless CRM and project-operations system (accounts, leads, opportunities, projects, weekly time, approvals) that is faster than Dynamics 365 and whose data can be trusted. By the 11 Dec 2026 evaluation Sirocco can decide whether it could replace Dynamics. This edition runs entirely on a laptop (`docker compose up`) at zero cost on synthetic data.

## In scope
- Local stack: db (PostgreSQL 16 + pg_trgm + unaccent), api :8080 (/health), web :3000, keycloak :8180 (realm `zephyr`), mailpit (SMTP 1025, UI 8025); `.env` from `.env.example`; `npm run seed`; `scripts/check`.
- F02 sign-in (OIDC/PKCE vs Keycloak, two realms, deactivation ≤5 min, personal API tokens, session limits, email-trust setting).
- F06 seven roles x four scopes, one permission engine for UI and API, automated permission suite.
- F07 app shell, global search (FTS + trigram fallback <5 results, unaccent), Account 360, Contact 360.
- F08 activity timeline and quick log. F10 BDR call-first lead workspace (convert/disqualify).
- F09/F29 opportunity pipeline, stage gates, MEDDPICC, close outcomes, close-date history.
- F11 project register, won-deal handover, daily stale-project rule.
- F12 weekly time grid. F13 approval, rate stamping, period lock/unlock (Finance).
- F14/F15 dashboards (Should). F16 reminders (Should). F04 headless REST API conventions + generated OpenAPI.
- F18 MCP server (Must). F19 website form + service request (Should). F24 GDPR (Must). F21 synthetic Dynamics generator + import (Should). F17 no-code admin metadata (Should). F22 build log/docs (Must).
- Seed data incl. synthetic volume set (~56,000 searchable records, ~105,000 time entries). Performance targets measured locally.

## Out of scope
Cloud hosting/Azure/OpenTofu/Entra/Key Vault, real Dynamics data, real email, paid services, SMS, native mobile, marketing automation, quotes/orders/invoices (Fortnox export only), case management beyond minimal service request, WBS/capacity planning/rate matrix, lead scoring, competitor record type, expenses, business-unit/team recreation, two-way email/calendar sync, AI inside Zephyr, production cut-over, multi-tenant packaging, customer portal, PI27-1 Could items (F20, F25–F28). Cloud performance re-measurement, real-user pilot, Entra: Deferred.

## Actors and permissions
| Role | Can | Cannot |
|---|---|---|
| Salesperson (~6) | own accounts/opps, stage moves, MEDDPICC, close Won/Lost | see others' time entries |
| BDR (12–14) | call list, log calls, qualify/disqualify leads | approve time |
| PM/approver (~8) | own projects, team, approve/reject time, delegate, review stale projects | unlock periods |
| Consultant (~67) | own time entries, submit/recall week | read others' time (unless approver) |
| Finance | finance view, reopen entries, lock/unlock periods, export approved hours | — |
| Management | read dashboards/pipeline org-wide | write |
| Admin | users, reference data, thresholds, field metadata, approve deletion/anonymisation | — |
Scopes: own, team, contracting country/business unit, organisation. Tokens act as their owner.

## Behaviours (normal path / edge cases)
- Sign-in: Keycloak user lands in Zephyr, no password stored; users matched on issuer+subject, first login by verified email only and only if provider "email is trusted"; no self sign-up; deactivation effective ≤5 min; idle 4 h / absolute 12 h; tokens `zph_`+32 bytes, SHA-256, shown once, expiry default 90 d max 365 d, max 10 active (11th refused, expired refused). Bootstrap admin via env only while no admin exists, logged.
- Permissions: out-of-scope reads 403/404; search never leaks (0 leaks).
- Search: prefix, one typo, missing accents; permission filtered.
- Activity: type call/meeting/note/task/email; regarding validated (422 on bad type/ID); account_id server-set; contact account change moves activities in one transaction; no regarding = no timeline; 9 call outcomes set `connected`.
- Lead: lead source required; call_tries++ and last_call_outcome per call; convert → account+contact+opportunity; disqualify with reason.
- Opportunity: account+stage required; stages by key from metadata; gate dialog lists missing MEDDPICC, override needs logged reason; Lost needs reason + competitor; outcomes Won/Lost/No decision/Disqualified each with reason; close-date slips kept as history.
- Won: blocked until value+currency, rate+currency, contracting unit; one project per deal; copy fields; budget hours = estimated hours else round(contract ÷ rate) if same currency (300,000/1,100 = 273); status Active. Daily stale rule: Active, ≥60 days old, type not Support/Internal, no time in 60 days → Review; back to Active restarts 60 days.
- Time: 11 types; billable default only Client Work, Overtime (per-row override); project required except Vacation/Absence; only those may be future dated; 15-min steps; ≤24 h/day refused, >10 h warns; notes ≤500; edit only Draft/Rejected else STATUS_CHANGE_NOT_ALLOWED; submit/recall week; source ui/api/mcp/import/form/webhook/automation; 375 px usable.
- Approval: Submitted→Approved/Rejected; reject needs comment; resubmit; bulk approve; delegate; rate/amount/currency stamped; due 2nd of next month then locked; only Finance reopens/unlocks.
- API: /api/v1, camelCase, filter[field][op], sort=-updatedAt, cursor paging 50/200, RFC 9457, ETag/If-Match (412/428), money object with string amounts, upsert by external ID, soft delete 204, audit user from token.
- MCP: preview without save, confirm saves with source=mcp; refused for invisible records.
- GDPR: consent per contact; retention rehearsal lists candidates, nothing changes until admin approves; DSAR export then erase/anonymise; all logged.
- Import: file-only, mapping/*.yaml, idempotent by Dynamics GUID in external_id, reconciliation report, country mapping (blank/invalid → null, logged, never default Sweden), ~161,000 rows.

## Constraints
ASP.NET Core API (src/backend/Zephyr.Api), web in src/frontend, PostgreSQL 16, Docker Compose; OpenAPI 3.1 generated from field metadata, Spectral 6.15.0; one OCI image, env config, no cloud SDK, stdout logs, /health; UTC storage, display Europe/Stockholm, dates 2026-11-02; six countries/currencies, SEK base; WCAG 2.1 AA; p95 ≤2 s on six key screens, ≤0.5 s API/search, errors <0.5%; no AI at runtime; UUID keys, soft delete, custom JSONB, external_id table. `scripts/check` gate (lint, dotnet test, validate_seeds.py, OpenAPI regen+Spectral, oasdiff, Schemathesis, vulnerability audit). No checks configured yet in this project.

## Dependencies
Docker/Podman, .NET SDK, Node.js; committed Keycloak realm file; existing repo assets referenced by the doc (Seed/0003 SQL, tools/search-benchmark/generate.sql, openapi-from-metadata, validate_seeds.py, mapping/*.yaml) — assumed present or to be created; decisions from Henry, Mats, Nicole, Nellie.

## Assumptions
- A1: Existing ADRs/seeds are not visible to me; stories say "create or extend".
- A2: Web framework unconfirmed; stories are framework-neutral.
- A3: UI in English.
- A4: Should-priority features (F14–F17, F19, F21) are in the backlog flagged Should; F17 admin is built as Should.
- A5: Stale-deal reminder threshold 30 days and other [confirm] values used as stated.
- A6: PM of an auto-created project = opportunity owner's configured PM field if set, else left null (pending question).
- A7: Exchange rates fixed 10.0 SEK/USD, 11.00 SEK/EUR; other currencies need values (question).
- A8: Docs/ADR/BUILD.md work is in scope as documentation stories.

## Open questions
Scope: Q on contracting units at Won (3 vs 6 countries); F17/F19/F16/F21 priority for 11 Dec.
Behaviour: PM and budget formula on handover; time on Review projects; Client Work future-dating; billable per project; per-user time zone; blank/other country handling; stage set/gates workshop; search ranking and index sync.
Constraints: web framework/UI language; token hashing; retention periods; exchange rates for 6 currencies.
Dependencies/manual: Docker licence; automation vs manual for env setup, import runs, benchmarks, usability tests, rebuild test, ADR approvals, Claude usage log.
