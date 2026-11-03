-- Synthetic contacts (US-131), each belonging to a synthetic account. Assumes contact(name, account_id).
INSERT INTO contact (id, name, account_id, custom)
SELECT pg_temp.uid('contact', n),
       v.name,
       pg_temp.uid('account', 1 + pg_temp.h('contact', n, 'a') % pg_temp.target('account')),
       pg_temp.marker(v.name, p.name)
FROM generate_series(1, pg_temp.target('contact')) AS n
CROSS JOIN LATERAL (
  SELECT pg_temp.pick('given', pg_temp.h('contact', n, 'b')) || ' '
         || pg_temp.pick('family', pg_temp.h('contact', n, 'c')) AS name
) p
CROSS JOIN LATERAL (SELECT pg_temp.typo(p.name, pg_temp.h('contact', n, 't')) AS name) v
ORDER BY n
ON CONFLICT (id) DO NOTHING;
