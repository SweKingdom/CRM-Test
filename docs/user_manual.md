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

Loading reference data and demo companies

Use this to fill a local database with realistic test data.

1. Open a terminal in the project folder.
2. To load the reference data and demo companies, run `npm run seed`.
3. To load the larger test volume (about 56,000 searchable records and about 105,000 time entries), run `npm run seed -- --volume`.
4. If a table is not yet available in your database, the loader prints `SKIP <entity>: table missing` for it and loads the rest.
5. To run the volume load again, add `--reset`. This removes only the generated test records and keeps any records you created by hand.

Tip: Generated records are marked as synthetic. Do not use them as real customer data.

Warning: Without `--reset`, a second volume load stops with an error that names `--reset`. Nothing is changed in that case.
