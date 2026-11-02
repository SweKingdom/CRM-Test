-- Reference data. Idempotent: ON CONFLICT (key) DO UPDATE, so edits here reach existing databases.
-- PO sign-off needed: call outcomes, time types (only 5 are named in the brief), countries, currencies,
-- close reasons. Values below are proposals, not confirmed business facts.

-- Stages (7). No decision / Disqualified are close outcomes, not stages.
INSERT INTO stage (key, label, sort_order, active, is_terminal) VALUES
  ('discover', 'Discover', 1, true, false),
  ('qualify', 'Qualify', 2, true, false),
  ('validate', 'Validate', 3, true, false),
  ('propose', 'Propose', 4, true, false),
  ('close', 'Close', 5, true, false),
  ('won', 'Won', 6, true, true),
  ('lost', 'Lost', 7, true, true)
ON CONFLICT (key) DO UPDATE SET label = EXCLUDED.label, sort_order = EXCLUDED.sort_order,
  active = EXCLUDED.active, is_terminal = EXCLUDED.is_terminal;

-- MEDDPICC elements (8).
INSERT INTO meddpicc_element (key, label, sort_order, active) VALUES
  ('metrics', 'Metrics', 1, true),
  ('economic_buyer', 'Economic buyer', 2, true),
  ('decision_criteria', 'Decision criteria', 3, true),
  ('decision_process', 'Decision process', 4, true),
  ('paper_process', 'Paper process', 5, true),
  ('identify_pain', 'Identify pain', 6, true),
  ('champion', 'Champion', 7, true),
  ('competition', 'Competition', 8, true)
ON CONFLICT (key) DO UPDATE SET label = EXCLUDED.label, sort_order = EXCLUDED.sort_order,
  active = EXCLUDED.active;

-- Close outcomes (4) and close reasons.
INSERT INTO close_outcome (key, label, sort_order, active, maps_to_stage) VALUES
  ('won', 'Won', 1, true, 'won'),
  ('lost', 'Lost', 2, true, 'lost'),
  ('no_decision', 'No decision', 3, true, NULL),
  ('disqualified', 'Disqualified', 4, true, NULL)
ON CONFLICT (key) DO UPDATE SET label = EXCLUDED.label, sort_order = EXCLUDED.sort_order,
  active = EXCLUDED.active, maps_to_stage = EXCLUDED.maps_to_stage;

INSERT INTO close_reason (key, label, sort_order, active, outcome_key) VALUES
  ('won_best_fit', 'Best fit for the need', 1, true, 'won'),
  ('lost_price', 'Price', 2, true, 'lost'),
  ('lost_competitor', 'Chose a competitor', 3, true, 'lost'),
  ('no_decision_timing', 'Customer postponed', 4, true, 'no_decision'),
  ('no_decision_budget', 'No budget', 5, true, 'no_decision'),
  ('disqualified_no_fit', 'Not a fit', 6, true, 'disqualified')
ON CONFLICT (key) DO UPDATE SET label = EXCLUDED.label, sort_order = EXCLUDED.sort_order,
  active = EXCLUDED.active, outcome_key = EXCLUDED.outcome_key;

-- Call outcomes (9). counts_as_contact sets Activity.connected.
INSERT INTO call_outcome (key, label, sort_order, active, counts_as_contact) VALUES
  ('no_answer', 'No answer', 1, true, false),
  ('voicemail', 'Left voicemail', 2, true, false),
  ('busy', 'Busy', 3, true, false),
  ('wrong_number', 'Wrong number', 4, true, false),
  ('gatekeeper', 'Reached gatekeeper only', 5, true, false),
  ('call_back_later', 'Call back later', 6, true, true),
  ('not_interested', 'Not interested', 7, true, true),
  ('interested', 'Interested', 8, true, true),
  ('meeting_booked', 'Meeting booked', 9, true, true)
ON CONFLICT (key) DO UPDATE SET label = EXCLUDED.label, sort_order = EXCLUDED.sort_order,
  active = EXCLUDED.active, counts_as_contact = EXCLUDED.counts_as_contact;

-- Time types (11). Billable by default: client_work and overtime only.
-- Project required except vacation/absence; only those two may be future dated.
INSERT INTO time_type (key, label, sort_order, active, billable_default, requires_project, allows_future, category) VALUES
  ('client_work', 'Client work', 1, true, true, true, false, 'client'),
  ('overtime', 'Overtime', 2, true, true, true, false, 'client'),
  ('internal', 'Internal', 3, true, false, true, false, 'internal'),
  ('vacation', 'Vacation', 4, true, false, false, true, 'absence'),
  ('absence', 'Absence', 5, true, false, false, true, 'absence'),
  ('presales', 'Presales', 6, true, false, true, false, 'internal'),
  ('training', 'Training', 7, true, false, true, false, 'internal'),
  ('travel', 'Travel', 8, true, false, true, false, 'client'),
  ('support', 'Support', 9, true, false, true, false, 'client'),
  ('management', 'Management', 10, true, false, true, false, 'internal'),
  ('recruitment', 'Recruitment', 11, true, false, true, false, 'internal')
ON CONFLICT (key) DO UPDATE SET label = EXCLUDED.label, sort_order = EXCLUDED.sort_order,
  active = EXCLUDED.active, billable_default = EXCLUDED.billable_default,
  requires_project = EXCLUDED.requires_project, allows_future = EXCLUDED.allows_future,
  category = EXCLUDED.category;

-- Countries (6). key = iso2.
INSERT INTO country (key, label, sort_order, active, iso2) VALUES
  ('SE', 'Sweden', 1, true, 'SE'),
  ('NO', 'Norway', 2, true, 'NO'),
  ('DK', 'Denmark', 3, true, 'DK'),
  ('FI', 'Finland', 4, true, 'FI'),
  ('DE', 'Germany', 5, true, 'DE'),
  ('IT', 'Italy', 6, true, 'IT')
ON CONFLICT (key) DO UPDATE SET label = EXCLUDED.label, sort_order = EXCLUDED.sort_order,
  active = EXCLUDED.active, iso2 = EXCLUDED.iso2;

-- Currencies (6). key = iso3, SEK is the base currency.
INSERT INTO currency (key, label, sort_order, active, iso3, decimals) VALUES
  ('SEK', 'Swedish krona', 1, true, 'SEK', 2),
  ('EUR', 'Euro', 2, true, 'EUR', 2),
  ('USD', 'US dollar', 3, true, 'USD', 2),
  ('NOK', 'Norwegian krone', 4, true, 'NOK', 2),
  ('DKK', 'Danish krone', 5, true, 'DKK', 2),
  ('GBP', 'Pound sterling', 6, true, 'GBP', 2)
ON CONFLICT (key) DO UPDATE SET label = EXCLUDED.label, sort_order = EXCLUDED.sort_order,
  active = EXCLUDED.active, iso3 = EXCLUDED.iso3, decimals = EXCLUDED.decimals;
