-- Broken fixture: reference data must use DO UPDATE.
INSERT INTO close_outcome (key, label, sort_order, maps_to_stage) VALUES
  ('won', 'Won', 1, 'won')
ON CONFLICT (key) DO NOTHING;
