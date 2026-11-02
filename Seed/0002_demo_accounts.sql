-- Demo accounts: fake companies, no personal data. DO NOTHING so edits made in the database survive a re-seed.
INSERT INTO account (id, name, country_code) VALUES
  ('00000000-0000-4000-8000-0000000d0001', 'Lindholm Demo AB', 'SE'),
  ('00000000-0000-4000-8000-0000000d0002', 'Ekdal Demo AB', 'SE'),
  ('00000000-0000-4000-8000-0000000d0003', 'Bellini Demo Srl', 'IT')
ON CONFLICT (id) DO NOTHING;
