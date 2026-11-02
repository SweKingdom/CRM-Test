# User manual

## Signing in locally

Use this chapter to sign in to the local test environment.

1. Start the stack: run `scripts/render-realms.sh` (or `scripts/render-realms.ps1`), then `docker compose up`.
2. Open http://localhost:8180 and choose the realm `zephyr`.
3. Sign in with one of the test users: admin, salesperson, BDR, project manager, consultant, finance or management, all at `@example.com`.
4. Use the password you set for that user in your `.env` file. Passwords are never stored in the repository.

Tip: the test users are for local testing only. Do not use them outside your own machine.
