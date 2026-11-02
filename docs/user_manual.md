# User manual

## Signing in locally

Use this chapter to sign in to the local test environment.

1. Start the stack: run `scripts/render-realms.sh` (or `scripts/render-realms.ps1`), then `docker compose up`.
2. Open http://localhost:8180 and choose the realm `zephyr`.
3. Sign in with one of the test users: admin, salesperson, BDR, project manager, consultant, finance or management, all at `@example.com`.
4. Use the password you set for that user in your `.env` file. Passwords are never stored in the repository.

Tip: the test users are for local testing only. Do not use them outside your own machine.

## Running Zephyr locally

Developers and testers can start the whole system on one laptop with Docker.

1. Install Docker Desktop (or Rancher Desktop or Podman).
2. In the project folder, run `node scripts/setup-env.mjs`. This creates `.env` with random passwords. It never overwrites an existing `.env`.
3. Run `docker compose up --build`.
4. Open http://localhost:8025 to see emails caught by Mailpit. Nothing is sent outside your machine.
5. Check the API is up at http://localhost:8080/health. A healthy system returns `{"status":"Healthy"}`.

Tip: `.env` holds your local passwords. Do not commit it.

## Loading reference data and demo companies

Reference data (call outcomes, time types, countries, currencies, sales stages, MEDDPICC elements) and three fake demo companies are loaded with one command.

1. Make sure the database is running and `DATABASE_URL` is set (environment or `.env` file).
2. Run `npm run seed` from the project root.
3. Check the result. The three demo companies **Lindholm Demo AB**, **Ekdal Demo AB** and **Bellini Demo Srl** appear in the account list.

Tips:
- It is safe to run the seed again. Existing reference rows are updated, not duplicated.
- Demo companies you have renamed are never overwritten by the seed.
- Exchange rates are not loaded by the seed. Amounts in a currency other than SEK show "missing rate" until exchange rates are delivered.
