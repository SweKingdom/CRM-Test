# User manual

## Signing in locally

Zephyr does not have its own passwords. You sign in through the organisation's identity provider.

1. Open Zephyr in your browser and select **Sign in**. (Local: `/auth/login?provider=zephyr`.)
2. Enter your work account in the identity provider's sign-in page.
3. You are returned to Zephyr and see your name in the header.

**How long you stay signed in**
- Your session ends after **4 hours** without activity. Any action you take in Zephyr keeps it open.
- Your session ends **12 hours** after you signed in, even if you have been active. You then see the sign-in page again.
- If an administrator deactivates your account, you lose access within **5 minutes**.
- Sessions that started before this change must sign in once more.

**Tips**
- If you have no Zephyr account yet, the sign-in is refused. Ask an administrator to create your user.
- If you see the message "Your account is not set up in Zephyr. Contact an administrator.", your account exists in the identity provider but is not linked to a Zephyr user, or it is deactivated.
- Signing out clears your session in this browser.

## Running Zephyr locally

The step-by-step local setup now lives in the project README, section "Run Zephyr locally". It lists every command in order, from cloning the repository to signing in as a seeded test user. Test user passwords are set in your local `.env` file and are never written in the README.

Tip: if you only need to sign in again, skip to the sign-in step. Your data stays on your computer.

## Loading reference data and demo companies

Loading reference data and demo companies

Use this to fill a local database with realistic test data.

1. Open a terminal in the project folder.
2. To load the reference data and demo companies, run `npm run seed`.
3. To load the larger test volume (about 56,000 searchable records and about 105,000 time entries), run `npm run seed -- --volume`.
4. If a table is not yet available in your database, the loader prints `SKIP <entity>: table missing` for it and loads the rest.
5. To run the volume load again, add `--reset`. This removes only the generated test records and keeps any records you created by hand.

Tip: Generated records are marked as synthetic. Do not use them as real customer data.

Warning: Without `--reset`, a second volume load stops with an error that names `--reset`. Nothing is changed in that case.

## Exchange rates and amounts in SEK

Amounts are shown in their own currency and in SEK, so totals can be compared.

**Supported currencies:** SEK (base), EUR, USD, NOK, AED and GBP.

### How amounts are converted
1. Each amount is kept in its own currency, for example 100.00 USD.
2. The SEK value (shown as **amountSEK**) uses the exchange rate of the month of the business date.
3. The rate date (**rateDate**) is the first day of that month.
4. SEK amounts are not converted; the SEK value equals the amount.

> **Tip:** Rates are per month. An amount in November uses the November rate, not the October rate.

### How to view exchange rates
1. Sign in.
2. Open the exchange rates list. You can filter by **Currency** and sort by **Month** or **Updated**.

### How to change an exchange rate (administrators only)
1. Sign in with an account that has the **Admin** role.
2. Select the currency and the month.
3. Enter the rate to SEK (up to 6 decimals, greater than 0). SEK cannot be changed.
4. Save. A new rate is created; an existing rate is updated.

> **Warning:** Stored amounts keep the SEK value that was calculated when they were saved. Changing a rate does not change existing amounts.

> **Note:** If a currency has no rate for the month of an amount, the amount cannot be saved and the message names the currency and the month. Ask an administrator to add the rate.

## Managing your personal API tokens

Not changed by US-136. The API description is for integrators; see the technical overview section "API description (OpenAPI)".

## Who can see and change what

Access depends on your role and on the approver assignments.

| What you want to do | Who can do it |
|---|---|
| Delete an account | Administrators only |
| Add or remove a time approver on a project | Administrators only (for now) |
| See the time entries of a project you approve | That project's approvers |
| Approve time for a project | That project's approvers and their delegates |
| Delegate your approval rights for a date range | An approver of at least one project |
| See a delegation you gave or received | The delegator, the delegate, and administrators |
| Revoke a delegation | The delegator and administrators |

**Notes**
- You cannot approve your own time, even when you hold delegated approval rights.
- A delegation covers only the projects you approve yourself. It does not pass on rights you received from someone else.
- A delegation is active on both its start date and its end date, using Swedish time. It ends at once when it is revoked.
- Time entries of people who are not your approver or delegate are not shown to you.
- The project's PM will be able to manage approvers once the project register is in place (US-161).

## Recording Claude usage on a pull request

Every pull request for a story records the Claude usage for that story.

1. Open the pull request description.
2. Find the **Claude usage** section.
3. Run `node tools/claude-usage/cli.mjs report` in the repository and find the total for your story.
4. Enter the totals in **Tokens in**, **Tokens out** and **Cost (USD)**.

Tip: the usage files are written automatically by the pre-commit hook. Do not edit them by hand.
