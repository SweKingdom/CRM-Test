-- Synthetic opportunities (US-131, US-156), each for a synthetic account, in the Discover stage, owned by one synthetic user.
-- The opportunity needs an owner (app_user) and a stage (reference data) to exist, and a bare database has neither, so the same
-- statement adds them if missing: the synthetic user (fixed id, marked) and the discover stage (the reference seed keeps its own values).
WITH owner AS (
  INSERT INTO app_user (id, name, email, roles, custom)
  VALUES ('00000000-0000-4000-8000-0000000f0001', 'Synthetic Owner', 'synthetic.owner@example.com', ARRAY['Salesperson'],
          jsonb_build_object('synthetic', true))
  ON CONFLICT (id) DO NOTHING
), discover AS (
  INSERT INTO stage (key, label, sort_order) VALUES ('discover', 'Discover', 1)
  ON CONFLICT (key) DO NOTHING
)
INSERT INTO opportunity (id, name, account_id, stage_key, owner_user_id, custom)
SELECT pg_temp.uid('opportunity', n),
       v.name,
       pg_temp.uid('account', 1 + pg_temp.h('opportunity', n, 'a') % pg_temp.target('account')),
       'discover',
       '00000000-0000-4000-8000-0000000f0001'::uuid,
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
