# US-148: App shell and global search UI
**Status:** Ready for UAT · **Category:** development · **Points:** 5 · **Sprint:** —

## Summary
Every page now has a shared app shell with header, permission-driven navigation and a single search box. Typing in the search box shows matching accounts and contacts grouped by type, and users can jump to any record with the mouse or keyboard.

## What was built
- `src/frontend/src/main.tsx`, `App.tsx`: router, query client, routes (`/`, `/search`, `/access-denied`, "Not available yet" placeholder for other pages).
- `src/frontend/src/permissions.tsx`: `SignedIn` gate (loading, sign-in redirect, failure, ready), `AccessDenied` view.
- `src/frontend/src/shell/Layout.tsx`: header, navigation (path + label array, shown when `GET /api/v1/me/permissions` offers the route), phone menu, skip link, user name and sign out.
- `src/frontend/src/shell/SearchBox.tsx`: labelled combobox, 250 ms debounce, grouped dropdown (max 5 per type), keyboard support, live region, empty/error/retry states.
- `src/frontend/src/pages/SearchResults.tsx`: `/search?q=` page listing all results grouped by type.
- `src/frontend/src/lib/api.ts`: fetch wrapper with `credentials: 'same-origin'`, parses RFC 9457 problem bodies into `ApiError`.
- `src/frontend/src/lib/search.ts`: `useSearch`, grouping and record paths.
- `src/frontend/src/lib/format.ts`: `formatDate` (ISO `2026-11-02`) and `formatDateTime` (Europe/Stockholm), with unit tests in `format.test.ts`.
- `src/frontend/src/strings.ts`, `styles.css`: UI strings, tokens, focus ring, breakpoint at 640 px.
- `src/frontend/e2e/shell.spec.ts`, `playwright.config.ts`: Playwright specs for AC 1–5 (API mocked, axe check) and a Keycloak sign-in smoke test.
- `scripts/check`, `scripts/check.ps1`: the tests step runs the frontend `test` script; `.gitignore` covers Playwright output.

## How it works
- **AC 1 Shell:** After sign-in every page shows header, navigation and search box. Navigation items appear only when `GET /api/v1/me/permissions` offers their route. A 401 from that endpoint redirects to sign-in. `/search` is open to any signed-in user.
- **AC 2 Search results:** Input of at least 2 characters, debounced 250 ms, shows a dropdown grouped by record type (max 5 per group). Arrow keys move the highlight, Enter opens the highlighted item. Enter with no highlight opens `/search?q=…`. Accounts open `/accounts/{id}` (Account 360), contacts open `/contacts/{id}` (Contact 360). Lead, opportunity and project links go to their routes if present, otherwise to "Not available yet". A new input cancels the in-flight request.
- **AC 3 Empty and error:** No hits shows "No results for “…”. Check the spelling or try fewer letters." An API error shows "Search is unavailable right now. Try again." with a retry control. Neither state blocks navigation.
- **AC 4 Accessibility:** `/` focuses the search box, Esc closes the list. The combobox uses `aria-expanded` and `aria-activedescendant`, and a live region announces the result count. The axe check runs as a Playwright spec.
- **AC 5 Phone size:** At 360 px there is no horizontal scroll. Navigation collapses behind a "Menu" button and the search box sits on its own row.
- **AC 6 Dates:** All dates go through `formatDate` and all times through `formatDateTime`, so the whole UI uses one formatter.

## How to test
**Automated**
- Gate: `scripts/check` (or `scripts/check.ps1`). The tests step runs `npm --prefix src/frontend run test` (vitest, `src/lib/format.test.ts`).
- UI: `cd src/frontend && npm install && npx playwright install chromium && npm run e2e` (or `scripts/check --ui`). The API is mocked. The Keycloak smoke test needs `docker compose up -d` and `ZEPHYR_E2E_USER` / `ZEPHYR_E2E_PASSWORD`; without them it is skipped.

**Manual** (stack running, `npm run dev` in `src/frontend`, open http://localhost:3000)
- AC 1: Open the site signed out. **Verify:** you are taken to Keycloak; after sign-in header, navigation and search box show, and navigation lists only the screens your role may open.
- AC 2: Type `ac`. **Verify:** after about a quarter second a dropdown grouped by type (max 5 each) appears; Down/Up move, Enter opens the highlighted record; Enter with no highlight opens `/search?q=ac`.
- AC 3: Type `zzzzzz`; stop the API and type `acme`. **Verify:** the no-results message, then "Search is unavailable right now. Try again." with a "Try again" button; navigation links still work.
- AC 4: Press `/` on any page, type, press Esc. **Verify:** focus moves to the box; Esc closes the list; a screen reader announces the result count; `npm run e2e` axe test passes.
- AC 5: Browser at 360 px width. **Verify:** no horizontal scroll; navigation is behind "Menu"; search box is on its own row.
- AC 6: `npm --prefix src/frontend run test`. **Verify:** dates read `2026-11-02`, times `14:30` in Europe/Stockholm.

## Deployment notes
- Run `npm install --prefix src/frontend` and commit `src/frontend/package-lock.json` (not yet present).
- Run `npx playwright install chromium` once in `src/frontend` before the e2e suite.
- No database changes. No new configuration beyond the optional `ZEPHYR_E2E_USER` and `ZEPHYR_E2E_PASSWORD` variables for the Keycloak smoke test.

## Known limitations
- Per-user time zone is not implemented. All display uses Europe/Stockholm. Recorded as an open question (PO answer #80 asks for a per-user time zone field, defaulting to Europe/Stockholm, to be used for "today" decisions; the UI setting comes later).
- The Playwright e2e specs (including axe and the 360 px check) were reviewed by reading only; they were not executed in the QA environment because no browser was available.
- The axe check is not part of the default eight-step gate; it runs under `npm run e2e` and `scripts/check --ui` (PO answer #163). The e2e follow-up story will add CI execution on every PR.
- Lead, opportunity and project pages are placeholders ("Not available yet") until their stories land.
- Accepted deviations from the written spec: AC 1 navigation uses path + label filtered by `/api/v1/me/permissions` (PO answer #162); AC 4 axe check outside the default gate (PO answer #163).
