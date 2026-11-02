-- Broken fixture: INSERT without ON CONFLICT.
INSERT INTO close_outcome (key, label, sort_order, maps_to_stage) VALUES
  ('won', 'Won', 1, 'won');
