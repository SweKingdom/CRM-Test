-- Helpers for generate/NN_<entity>.sql. scripts/seed.mjs runs this first in every transaction.
-- Input comes from transaction settings: zephyr.seed, zephyr.anchor (date), zephyr.data (lists), zephyr.targets.
-- Everything is derived from md5(seed || entity || n), so two databases loaded with the same seed are identical.

-- Pseudo-random integer in [0, 2^28) for row n of an entity; salt gives each column its own value.
CREATE OR REPLACE FUNCTION pg_temp.h(entity text, n int, salt text) RETURNS int
LANGUAGE sql STABLE AS $$
  SELECT ('x' || substr(md5(current_setting('zephyr.seed') || entity || n || salt), 1, 7))::bit(28)::int
$$;

CREATE OR REPLACE FUNCTION pg_temp.uid(entity text, n int) RETURNS uuid
LANGUAGE sql STABLE AS $$
  SELECT md5(current_setting('zephyr.seed') || entity || n)::uuid
$$;

-- Row target of an entity from targets.json ('count' or e.g. 'trailing_12_months').
CREATE OR REPLACE FUNCTION pg_temp.target(entity text, key text DEFAULT 'count') RETURNS int
LANGUAGE sql STABLE AS $$
  SELECT (current_setting('zephyr.targets')::jsonb #>> ARRAY['entities', entity, key])::int
$$;

-- Element h (modulo the list length) of a list in data/*.json.
CREATE OR REPLACE FUNCTION pg_temp.pick(list text, h int) RETURNS text
LANGUAGE sql STABLE AS $$
  SELECT (d -> list) ->> (h % jsonb_array_length(d -> list))
  FROM (SELECT current_setting('zephyr.data')::jsonb AS d) x
$$;

-- About 2% of values (h divisible by 50) become a typo variant: one "from>to" pair from the typos list is applied;
-- if the value does not contain it, its third character is dropped instead.
CREATE OR REPLACE FUNCTION pg_temp.typo(v text, h int) RETURNS text
LANGUAGE sql STABLE AS $$
  SELECT CASE
    WHEN h % 50 <> 0 THEN v
    WHEN strpos(v, split_part(p, '>', 1)) > 0 THEN replace(v, split_part(p, '>', 1), split_part(p, '>', 2))
    ELSE overlay(v PLACING '' FROM 3 FOR 1)
  END
  FROM (SELECT pg_temp.pick('typos', h / 50) AS p) x
$$;

-- The marker every generated row carries; typo_of keeps the correct spelling of a typo variant.
CREATE OR REPLACE FUNCTION pg_temp.marker(variant text, original text) RETURNS jsonb
LANGUAGE sql IMMUTABLE AS $$
  SELECT jsonb_build_object('synthetic', true)
         || CASE WHEN variant <> original THEN jsonb_build_object('typo_of', original) ELSE '{}'::jsonb END
$$;
