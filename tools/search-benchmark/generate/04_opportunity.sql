-- Synthetic opportunities (US-131), each for a synthetic account. Assumes opportunity(name, account_id).
INSERT INTO opportunity (id, name, account_id, custom)
SELECT pg_temp.uid('opportunity', n),
       v.name,
       pg_temp.uid('account', 1 + pg_temp.h('opportunity', n, 'a') % pg_temp.target('account')),
       pg_temp.marker(v.name, p.name)
FROM generate_series(1, pg_temp.target('opportunity')) AS n
CROSS JOIN LATERAL (
  SELECT pg_temp.pick('family', pg_temp.h('opportunity', n, 'b')) || ' '
         || pg_temp.pick('legal', pg_temp.h('opportunity', n, 'c')) || ' - '
         || pg_temp.pick('services', pg_temp.h('opportunity', n, 'd')) AS name
) p
CROSS JOIN LATERAL (SELECT pg_temp.typo(p.name, pg_temp.h('opportunity', n, 't')) AS name) v
ORDER BY n
ON CONFLICT (id) DO NOTHING;
