-- Synthetic projects (US-131), each for a synthetic account. Assumes project(name, account_id).
INSERT INTO project (id, name, account_id, custom)
SELECT pg_temp.uid('project', n),
       v.name,
       pg_temp.uid('account', 1 + pg_temp.h('project', n, 'a') % pg_temp.target('account')),
       pg_temp.marker(v.name, p.name)
FROM generate_series(1, pg_temp.target('project')) AS n
CROSS JOIN LATERAL (
  SELECT pg_temp.pick('services', pg_temp.h('project', n, 'b')) || ' '
         || pg_temp.pick('family', pg_temp.h('project', n, 'c')) || ' '
         || split_part(pg_temp.pick('cities', pg_temp.h('project', n, 'd')), '|', 1) AS name
) p
CROSS JOIN LATERAL (SELECT pg_temp.typo(p.name, pg_temp.h('project', n, 't')) AS name) v
ORDER BY n
ON CONFLICT (id) DO NOTHING;
