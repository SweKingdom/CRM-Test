-- Fixed demo exchange rates (SEK per unit) for every month from 2020-01 to 2030-12 (132 months). SEK is the base currency and has no rows.
-- Rates set on 2026-10-26 by the product owner. DO NOTHING: a rate an admin has edited is never overwritten by a re-seed.
-- A month outside this range has no rate, so amounts dated there are refused until an admin adds the rate.
INSERT INTO exchange_rate (currency, month, rate_to_sek)
SELECT r.currency, (date '2020-01-01' + n * interval '1 month')::date, r.rate_to_sek
FROM (VALUES
  ('EUR', 11.000000),
  ('USD', 10.000000),
  ('NOK', 0.950000),
  ('AED', 2.720000),
  ('GBP', 12.800000)
) AS r (currency, rate_to_sek)
CROSS JOIN generate_series(0, 131) AS n
ON CONFLICT (currency, month) DO NOTHING;
