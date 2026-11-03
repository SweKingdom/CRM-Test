# User manual

## Signing in locally

Zephyr does not have its own passwords. You sign in through the organisation's identity provider.

1. Open Zephyr in your browser and select **Sign in**. (Local: `/auth/login?provider=zephyr`.)
2. Enter your work account in the identity provider's sign-in page.
3. You are returned to Zephyr and see your name in the header.

**Tips**
- If you have no Zephyr account yet, the sign-in is refused. Ask an administrator to create your user.
- If you see the message "Your account is not set up in Zephyr. Contact an administrator.", your account exists in the identity provider but is not linked to a Zephyr user, or it is deactivated.
- Signing out clears your session in this browser.

## Running Zephyr locally

The step-by-step local setup now lives in the project README, section "Run Zephyr locally". It lists every command in order, from cloning the repository to signing in as a seeded test user. Test user passwords are set in your local `.env` file and are never written in the README.

Tip: if you only need to sign in again, skip to the sign-in step. Your data stays on your computer.

## Loading reference data and demo companies

Reference data (call outcomes, time types, countries, currencies, sales stages, MEDDPICC elements) and three fake demo companies are loaded with one command.

1. Make sure the database is running and `DATABASE_URL` is set (environment or `.env` file).
2. Run `npm run seed` from the project root.
3. Check the result. The three demo companies **Lindholm Demo AB**, **Ekdal Demo AB** and **Bellini Demo Srl** appear in the account list.

Tips:
- It is safe to run the seed again. Existing reference rows are updated, not duplicated.
- Demo companies you have renamed are never overwritten by the seed.
- Exchange rates are not loaded by the seed. Amounts in a currency other than SEK show "missing rate" until exchange rates are delivered.
