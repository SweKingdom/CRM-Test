-- Synthetic accounts (US-131). Helpers and settings: see _helpers.sql.
INSERT INTO account (id, name, country_code, custom)
SELECT pg_temp.uid('account', n),
       v.name,
       split_part(p.city, '|', 2),
       pg_temp.marker(v.name, p.name) || jsonb_build_object('city', split_part(p.city, '|', 1))
FROM generate_series(1, pg_temp.target('account')) AS n
CROSS JOIN LATERAL (
  SELECT pg_temp.pick('family', pg_temp.h('account', n, 'a')) || ' '
         || pg_temp.pick('sectors', pg_temp.h('account', n, 'b')) || ' '
         || pg_temp.pick('legal', pg_temp.h('account', n, 'c')) AS name,
         pg_temp.pick('cities', pg_temp.h('account', n, 'd')) AS city
) p
CROSS JOIN LATERAL (SELECT pg_temp.typo(p.name, pg_temp.h('account', n, 't')) AS name) v
ORDER BY n
ON CONFLICT (id) DO NOTHING;
