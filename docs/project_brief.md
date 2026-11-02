# Project brief — Zephyr CRM, local edition

## Goal
Lean, headless CRM and project-operations system for ~100 Sirocco staff, replacing the used parts of Dynamics 365 (accounts, leads, opportunities, projects, weekly time, approvals). Evaluation on 11 Dec 2026. This edition runs on localhost at zero cost with synthetic data.

## Scope
See requirements_brief. Features F02, F04, F06, F07, F08, F09/F29, F10, F11, F12, F13, F14/F15, F16, F17, F18, F19, F21, F22, F24 plus the local platform (Compose, seeds, checks). Cloud, Entra, real data, AI at runtime are out.

## Personas
Salesperson (~6), BDR (12–14), PM/approver (~8), Consultant (~67), Finance, Management, Admin.

## Business processes
1. Sign-in: user → Keycloak (OIDC code+PKCE) → Zephyr matches issuer+subject → session (idle 4 h, absolute 12 h).
2. Lead to deal: BDR logs calls on call list → qualifies (convert to account/contact/opportunity) or disqualifies with reason → owner moves stages Discover→Qualify (gate)→Validate→Propose→Close → Won/Lost/No decision/Disqualified.
3. Won handover: fill value, rate, contracting unit → one linked Active project created → daily stale check sets Review.
4. Time: consultant fills week grid → submits (or recalls) → PM approves/rejects (bulk) → rate/amount/currency stamped → period locked after the 2nd of next month → Finance reopens/unlocks/exports.
5. Reporting: personal, PM, finance dashboards with drill-through; reminders by rule to Mailpit.
6. Governance: consent, retention rehearsal with admin approval, DSAR export/erase.
7. Migration rehearsal: generator writes Dynamics-shaped files → import with mapping → reconciliation report → idempotent re-run.
8. AI capture: MCP tool preview → user confirm → save with source=mcp.

## Data entities
Account, Contact, Lead, Opportunity, MEDDPICC element, Activity + participant, Project, Project team member, Skill/user–skill, Time type, Time entry, Service request, App user + API token, reference data (stages, close outcomes/reasons, call outcomes, countries, currencies, rates, field metadata, picklists), external_id, audit log, reminder log, retention rule. Conventions: UUID, created/updated/by, deleted_at, custom JSONB, money (amount numeric(18,2), currency, amount_sek, rate_date).

## Integrations
Keycloak (OIDC), Mailpit (SMTP), MCP server over REST, website test form, file-based Dynamics import. No external services.

## Non-functional requirements
p95 ≤2 s on six screens, ≤0.5 s API and search over ~56,000 records, errors <0.5%, tile ≤2 s over 18,495 entries; WCAG 2.1 AA; phone-sized time entry; RFC 9457; portability rules (one image, env config, stdout logs, /health); zero cost; no AI at runtime; UTC storage.

## Manual steps
| Step | Proposed handling | Story |
|---|---|---|
| Install Docker/Podman, .NET SDK, Node | By hand, once per developer | platform setup |
| Create `.env` and set test-user passwords | Question to PO: script vs by hand | platform setup |
| Run import / benchmarks | Script run by hand — PO to confirm | F21, perf |
| Usability tests (3-click, timed week) | By hand (F23) | time/BDR |
| Outsider rebuild test | By hand (F22-S06) | docs |
| ADR approvals (004, 006–009, 012, local-edition ADR) | By hand (Henry, Elin, PMs) | docs |
| Claude usage log per story | Semi-automated — PO to confirm | docs |

## Assumptions
See requirements_brief A1–A8.

## Out of scope
Cloud/Azure/Entra, real data and email, paid services, native mobile, quotes/invoices, SLAs, WBS, AI in Zephyr, cut-over, PI27-1 Could items.
