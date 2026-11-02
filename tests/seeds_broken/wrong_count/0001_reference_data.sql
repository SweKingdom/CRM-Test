-- Broken fixture: stage has 3 rows, 7 expected.
INSERT INTO stage (key, label, sort_order, is_terminal) VALUES
  ('discover', 'Discover', 1, false),
  ('qualify', 'Qualify', 2, false),
  ('won', 'Won', 3, true)
ON CONFLICT (key) DO UPDATE SET label = EXCLUDED.label, sort_order = EXCLUDED.sort_order,
  is_terminal = EXCLUDED.is_terminal;
