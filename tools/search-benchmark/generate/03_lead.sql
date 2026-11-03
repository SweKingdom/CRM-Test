-- Synthetic leads (US-131): a person at a company. Assumes lead(name).
INSERT INTO lead (id, name, custom)
SELECT pg_temp.uid('lead', n),
       v.name,
       pg_temp.marker(v.name, p.name)
FROM generate_series(1, pg_temp.target('lead')) AS n
CROSS JOIN LATERAL (
  SELECT pg_temp.pick('given', pg_temp.h('lead', n, 'a')) || ' '
         || pg_temp.pick('family', pg_temp.h('lead', n, 'b')) || ', '
         || pg_temp.pick('family', pg_temp.h('lead', n, 'c')) || ' '
         || pg_temp.pick('legal', pg_temp.h('lead', n, 'd')) AS name
) p
CROSS JOIN LATERAL (SELECT pg_temp.typo(p.name, pg_temp.h('lead', n, 't')) AS name) v
ORDER BY n
ON CONFLICT (id) DO NOTHING;
