# US-130: Reference data seeds and validate_seeds.py
**Status:** Ready for UAT · **Category:** data_migration · **Points:** 3 · **Sprint:** —

## Summary
`npm run seed` loads the reference data (call outcomes, time types, countries, currencies, stages, MEDDPICC elements) and three fake demo accounts, so the system is usable straight after start. Re-running the seed is safe: reference rows are updated in place and demo accounts are never overwritten. `python3 scripts/validate_seeds.py` checks the seed files for common mistakes.

## What was built
- `src/backend/Zephyr.Api/Data/Migrations/0002_ReferenceData.cs`: raw-SQL migration creating the reference tables (`stage`, `close_outcome`, `close_reason`, `call_outcome`, `time_type`, `country`, `currency`, `meddpicc_element`) and the minimal `account` table. Down drops them in reverse order.
- `Seed/0001_reference_data.sql`: reference rows, upserted with `ON CONFLICT (key) DO UPDATE`.
- `Seed/0002_demo_accounts.sql`: Lindholm Demo AB, Ekdal Demo AB, Bellini Demo Srl with fixed UUIDs, `ON CONFLICT (id) DO NOTHING`.
- `scripts/seed.mjs`: applies `Seed/*.sql` in filename order, one transaction per file, connection from `DATABASE_URL` or `.env`.
- `scripts/validate_seeds.py`: stdlib-only validator for seed files.
- `src/backend/Zephyr.Api.Tests/Seeds/SeedTests.cs`: database tests for AC 1–4.
- `tests/seeds.test.mjs` and `tests/seeds_broken/{no_conflict,reference_do_nothing,wrong_count}/`: fixtures for AC 5.
- `package.json`: `seed` script; dependencies `pg` and `dotenv`.
- `.gitignore`: `.env`, `bin/`, `obj/`, `__pycache__/`.

## How it works
- **AC 1 Reference data loaded:** after migrate and seed, the tables hold 9 call outcomes, 11 time types, 6 countries, 6 currencies (including SEK), 7 stages (discover, qualify, validate, propose, close, won, lost) and 8 MEDDPICC elements.
- **AC 2 Idempotent:** running the seed twice leaves all row counts unchanged.
- **AC 3 Reference rows update:** changing a label in `Seed/0001_reference_data.sql` and re-running the seed updates the existing row (same id, new label). No duplicate is created.
- **AC 4 Demo data is never overwritten:** a demo account edited in the database keeps its edited name after re-seeding. A fresh seed creates the three demo accounts.
- **AC 5 Validation:** `validate_seeds.py` exits 0 on valid files. It exits 1 and prints `file:line reason` for an INSERT without ON CONFLICT, a reference INSERT using DO NOTHING, or a wrong row count.

## How to test
- Run `npm run typecheck`, `npm run lint`, `npm run test`, `npm run build`. Node tests need Python 3 on PATH.
- **AC 1:** After migrate, run `DATABASE_URL=... npm run seed`. Verify the counts in the reference tables.
- **AC 2:** Run `npm run seed` twice. Verify that row counts are unchanged.
- **AC 3:** Change `'Sweden'` in `Seed/0001_reference_data.sql` and re-run the seed. Verify that country `SE` keeps its id, has the new label, and the table still has 6 rows.
- **AC 4:** Run `update account set name='X' where name='Ekdal Demo AB'`, then re-run the seed. Verify the name stays `X`.
- **AC 5:** Run `python3 scripts/validate_seeds.py` (exit 0). Run `python3 scripts/validate_seeds.py tests/seeds_broken/<case>` (exit 1, prints `file:line reason`).
- DB tests: set `ZEPHYR_TEST_DB`, then run `dotnet test src/backend/Zephyr.Api.Tests --filter SeedTests`. In CI, `ZEPHYR_REQUIRE_DB=1` is set.

## Deployment notes
- Pre-deploy: run `npm install` once. Set `DATABASE_URL` in the environment or in `.env`.
- Deploy: apply migrations (`0002_ReferenceData`), then run `npm run seed`.
- Exchange rates are not part of this story. They are delivered by US-134.

## Known limitations
- Exchange rates (SEK per unit, rate_date 2026-10-26) are not seeded here. Until US-134 is merged, any amount in a currency other than SEK returns 422 "missing rate". Accepted by the product owner (#112, #122).
- `SeedTests.cs` (DB-backed) has not been run locally because no PostgreSQL is available. It runs in CI with `ZEPHYR_REQUIRE_DB=1`. Accepted by the product owner (#103, #111, #121).
- The volume set is out of scope and is delivered by US-131.
