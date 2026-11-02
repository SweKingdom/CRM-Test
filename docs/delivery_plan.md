# Delivery plan — Project CT (local edition)

## Summary
- Scope: 61 stories (US-127 to US-187) under 14 features (FT-108 to FT-126), all planned.
- Total: 261 story points, 14 sprints of 2 weeks, capacity 20 points per sprint (AI team default).
- Sprint loads: 16–20 points each; no sprint exceeds 20 points.
- Order: foundations first (stack, DB baseline, seeds, API conventions, sign-in, permission model), then core CRM objects, then time and approvals, dashboards and automation, integrations and GDPR, admin and migration, with the outsider rebuild test last.
- Key risks: permission engine (US-144) gates most of the backlog; the time-reporting path (US-163 to US-169) is the reconciliation-critical chain; the Dynamics import (US-184 to US-186) depends on most target models.
- Evaluation date: 11 Dec 2026. The plan is not yet checked against that date; see Assumptions.
- Project checks: none configured yet. Quality-gate work (US-132) is planned in sprint 1, so no check results are claimed here.

## Sprint plan

| Sprint | Stories | Points | Goal |
|---|---|---|---|
| 1 | US-127, US-128, US-129, US-130, US-132 | 17 | Runnable local stack, Keycloak realm, DB baseline, reference seeds, quality gate |
| 2 | US-137, US-131, US-133, US-134, US-140 | 20 | Synthetic volume seed, API core conventions, money and FX, OIDC sign-in, docs start |
| 3 | US-135, US-136, US-141, US-143, US-138 | 20 | Upsert and soft delete, OpenAPI with drift check, session limits, permission model, usage log |
| 4 | US-142, US-144, US-145, US-146 | 16 | API tokens, permission engine, approvers and delegation, seven-role permission tests |
| 5 | US-147, US-148, US-151, US-153 | 20 | Search backend and UI shell, activity model, lead model |
| 6 | US-149, US-152, US-154, US-156 | 20 | Account/Contact 360, quick log and timeline, BDR call list, opportunity model |
| 7 | US-155, US-157, US-158, US-163, US-178 | 19 | Lead convert/disqualify, MEDDPICC and stage gates, close outcomes, time entry API, service requests |
| 8 | US-161, US-160, US-164, US-159 | 20 | Project register, won handover, week grid UI, opportunity board |
| 9 | US-162, US-165, US-166, US-167 | 16 | Stale-project rule, submit/recall week, approval queue, rate stamping |
| 10 | US-168, US-169, US-170, US-173 | 18 | Period lock and export, reconciliation fixture, My day/PM dashboards, reminder engine |
| 11 | US-174, US-179, US-171, US-172, US-187 | 19 | Reminder admin, consent audit, Finance dashboard, drill-through performance, usability tests |
| 12 | US-176, US-175, US-177, US-180 | 20 | MCP permissions and server, website enquiry form, retention rules |
| 13 | US-182, US-183, US-184, US-181 | 20 | Metadata admin API and UI, Dynamics-shaped export, DSAR |
| 14 | US-185, US-186, US-150, US-139 | 16 | Idempotent import, country mapping and reconciliation, load test, outsider rebuild test |

## Critical path
US-127 → US-128 → US-140 → US-143 → US-144 → US-151 / US-153 / US-156 → US-161 → US-160 → US-163 → US-164 → US-165 → US-166 → US-167 → US-168 → US-170 → US-171 → US-172
Parallel chain for import: US-129 → US-130 → US-131 → US-184 → US-185 → US-186.

## Milestones
- **MVP (end of sprint 8):** sign-in, permissions, Account/Contact 360, lead and opportunity flows, project handover.
- **UAT entry (end of sprint 11):** time reporting and approvals with period lock, dashboards, reminders, usability tests.
- **Go-live readiness (end of sprint 14):** integrations, GDPR, admin pack, Dynamics import and reconciliation, outsider rebuild test. Evaluation on 11 Dec 2026.

## Risks & mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Permission engine (US-144) slips | Blocks about 30 stories | Start in sprint 4 with the permission model in sprint 3; keep US-146 test suite in the same sprint |
| Time reconciliation mismatch (US-169) | Finance trust | Reference-month fixture built in sprint 10; reconciliation as an acceptance check |
| Sprint capacity overrun | Scope slip into sprint 15+ | Capacity cap 20 points; Should items (US-150, US-159, US-172, US-174–US-175, US-177–US-178, US-182–US-183, US-187) move first if needed |
| Import engine complexity (US-185) | Late import | Export generator (US-184) built in sprint 13 so the import has input early |
| Estimates unvalidated | Plan drift | Re-estimate after sprint 2 velocity check |

## Assumptions
- Capacity is 20 points per two-week sprint for the AI team (default); no other data was given.
- WSJF is computed as (business value + time criticality + risk reduction) / story points, using planner scores (not stakeholder-validated).
- MoSCoW: Should items are those not blocking the release; no stakeholder confirmation was available. Where unclear, I defaulted to Must for security, data and time-reporting stories.
- No task (TK) items exist in the backlog; all planned items are user stories.
- No checks are configured for the project yet; quality claims are not made.
