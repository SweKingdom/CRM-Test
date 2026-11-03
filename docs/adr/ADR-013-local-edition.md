# ADR-013: Local edition

Status: Proposed

## Context

This edition of Zephyr runs on one laptop at zero cost with synthetic data, for evaluation on 11 Dec 2026. It uses Docker Compose (PostgreSQL, API, web), Keycloak for sign-in and Mailpit for mail. ADR-001 and ADR-002 are referenced by the requirements and design but their text is not in this repository.

## Decision

The local edition is the only edition built now: Compose, Keycloak in place of the production identity provider, Mailpit in place of a mail relay, and synthetic data.

This ADR defers ADR-001 and ADR-002. Their decisions are not applied in the local edition and are not changed by it. It does not supersede or replace either of them: both stay as written and remain the reference for any later non-local edition, which will revisit them when it is planned.

## Consequences

- No cloud services, cloud SDKs, secrets stores or deploy pipelines are built in this edition.
- Everything is configured from environment variables and stdout logs, so the same image can move to another host later.
- When a non-local edition is planned, ADR-001 and ADR-002 are reviewed then; this ADR is replaced or amended by a new ADR at that point.
- Approval is pending (see `docs/adr/README.md`). The wording above about ADR-001 and ADR-002 is an assumption to be confirmed by the approvers.
