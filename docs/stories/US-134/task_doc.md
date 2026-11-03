# US-134: Money type and exchange rates (SEK base)
**Status:** Ready for UAT · **Category:** development · **Points:** 3 · **Sprint:** not recorded

## Summary
Every amount can now be stored in its own currency together with its SEK equivalent, so totals are comparable across currencies. Six currencies are supported (SEK, EUR, USD, NOK, AED, GBP), and the rates are kept in a table that administrators can edit.

## What was built
- `Money/Money.cs`: `MoneyInput` (parses and validates `amount` and `currency`), `Money` (stamped value with half-up conversion) and `MoneyJsonConverter` (output shape `{amount, currency, amountSEK, rateDate}`).
- `Money/ExchangeRates.cs`: `ExchangeRates.StampAsync` (rate lookup, 422 `MISSING_EXCHANGE_RATE`), `GET /api/v1/exchange-rates` and admin `PUT /api/v1/exchange-rates/{currency}/{yyyy-MM}`.
- `Data/ZephyrDbContext.cs`: `ExchangeRate` entity, table mapping and check constraints.
- `Data/Migrations/0003_AddExchangeRate.cs` and the model snapshot: new `exchange_rate` table.
- `Data/Migrations/0002_ReferenceData.cs`: `Conventions` made internal so the new migration can reuse it.
- `Common/Problems.cs`: `ErrorCodes.MissingExchangeRate`.
- `Program.cs`: maps the exchange-rate endpoints.
- `Seed/0003_exchange_rates.sql`: fixed rates for every month from 2020-01 to 2030-12.
- Tests: `Zephyr.Api.Tests/Money/MoneyTests.cs` and the exchange-rate seed test in `Seeds/SeedTests.cs`.

## How it works
- **AC 1 (output shape):** a money value is returned as `amount` (string, two decimals), `currency`, `amountSEK` and `rateDate`.
- **AC 2 (conversion):** `amountSEK = Math.Round(amount × rate, 2, MidpointRounding.AwayFromZero)`. Only the final amount is rounded. Example: 100.00 USD gives `"1000.00"`, 100.00 EUR gives `"1100.00"`. The rate is the rate of the business date's month (first day of the month).
- **AC 3 (SEK passthrough):** for SEK, the rate is 1 and no rate row is read. `amountSEK` equals `amount`.
- **AC 4 (missing rate):** a currency with no rate for the month returns 422 `MISSING_EXCHANGE_RATE` naming the currency and the month. There is no fallback to another month.
- **Input:** `amountSEK` and `rateDate` sent by a client are ignored. Stamped values are stored and are not recomputed later.
- **Admin rate maintenance:** `PUT` creates a rate (201) or changes it (200). SEK, unknown or inactive currencies, bad months and bad rates return 422. Non-admins get 403 and anonymous callers get 401.

## How to test
**Checks:** `npm run typecheck --silent`, `npm run lint --silent`, `npm run test --silent`, `npm run build --silent`. DB-backed tests run when `ZEPHYR_TEST_DB` is set, and always in CI with `ZEPHYR_REQUIRE_DB=1`.

- **AC 1:** `Ac1_Money_is_written_...` serialises a money value to `{"amount":"300000.00","currency":"SEK","amountSEK":"300000.00","rateDate":"2026-11-01"}`.
- **AC 2:** `Ac2_Amount_is_converted_...` checks 100.00 USD → `"1000.00"`, 100.00 EUR → `"1100.00"` and 1.00 × 2.345 → `"2.35"`. `Ac2_Stamping_uses_the_rate_of_the_month...` checks the rate of the business date's month.
- **AC 3:** `Ac3_SEK_...` checks that amountSEK equals amount with no rate row.
- **AC 4:** `Ac4_...` checks a 422 `MISSING_EXCHANGE_RATE` naming `NOK` and `2026-11`, and that the October rate is not used for November.
- **Endpoints:** as an Admin, `PUT /api/v1/exchange-rates/USD/2026-11` with `{"rateToSek":"10.5"}` returns 201, then 200 on change. `GET /api/v1/exchange-rates?filter[currency][eq]=USD` lists it.
- **Seed:** `node scripts/seed.mjs` run twice leaves 660 rows in `exchange_rate`, and an edited rate survives the second run.

## Deployment notes
- The API applies migration `0003_AddExchangeRate` at start.
- After deployment, run `node scripts/seed.mjs` to load the exchange rates (see the manual steps).
- Seeded rates: EUR 11.00, USD 10.00, NOK 0.95, AED 2.72, GBP 12.80 SEK per unit, for every month from 2020-01 to 2030-12. The rates are stored with 6 decimals. `rateDate` is the first day of the rate's month. 2026-10-26 is the date the demo rates were set.

## Known limitations
- Live rate feeds are out of scope.
- The admin check is an interim role check (`Admin` role claim). It will be replaced by the permission engine (US-144, FT-112). `ExchangeRates.DeniedStatus` is the place to change.
- The string `"Admin"` is defined both in `AppUser.AdminRole` and `ExchangeRates.AdminRole`. Unify them with US-144.
- Months outside 2020-01 to 2030-12 return 422 until an admin adds a rate.
- A converted amount outside the numeric(18,2) range returns 422 instead of a database error.
- DB-backed tests were not run in the local review (no PostgreSQL). They run in CI.
