-- Reference data. Idempotent: ON CONFLICT (key) DO UPDATE, so edits here reach existing databases.
-- Exchange rates (SEK per unit, rate_date 2026-10-26) belong to the money story (US-134), not to these tables.

-- Stages (7). No decision / Disqualified are close outcomes, not stages. outcome is set on the terminal stages only.
-- exit_criteria are the defaults agreed for F09-S01 (product owner answer #82): what must be true to leave a stage.
-- requires = MEDDPICC element at least at that status (identified < validated); recorded = facts that must be on the deal;
-- warnBelowIdentified = a warning, not a block, when fewer elements are identified. Mats reviews them later by editing this seed
-- (a configuration change, no deploy of code). The gate dialog lets an owner move a deal anyway with a logged reason (US-157).
INSERT INTO stage (key, label, sort_order, active, is_terminal, outcome, exit_criteria) VALUES
  ('discover', 'Discover', 1, true, false, NULL,
   '{"requires":[{"element":"identify_pain","status":"identified"},{"element":"economic_buyer","status":"identified"},{"element":"champion","status":"identified"}]}'::jsonb),
  ('qualify', 'Qualify', 2, true, false, NULL,
   '{"gate":"go_no_go","requires":[{"element":"economic_buyer","status":"validated"},{"element":"decision_process","status":"identified"}],"recorded":["compelling_event"]}'::jsonb),
  ('validate', 'Validate', 3, true, false, NULL,
   '{"requires":[{"element":"metrics","status":"validated"},{"element":"decision_criteria","status":"identified"}]}'::jsonb),
  ('propose', 'Propose', 4, true, false, NULL,
   '{"requires":[{"element":"decision_process","status":"validated"},{"element":"paper_process","status":"validated"}],"warnBelowIdentified":4}'::jsonb),
  ('close', 'Close', 5, true, false, NULL, '{}'::jsonb),
  ('won', 'Won', 6, true, true, 'won', '{}'::jsonb),
  ('lost', 'Lost', 7, true, true, 'lost', '{}'::jsonb)
ON CONFLICT (key) DO UPDATE SET label = EXCLUDED.label, sort_order = EXCLUDED.sort_order,
  active = EXCLUDED.active, is_terminal = EXCLUDED.is_terminal, outcome = EXCLUDED.outcome,
  exit_criteria = EXCLUDED.exit_criteria;

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

-- Call outcomes (9). counts_as_contact = the call connected (Activity.connected).
INSERT INTO call_outcome (key, label, sort_order, active, counts_as_contact) VALUES
  ('no_answer', 'No answer', 1, true, false),
  ('voicemail_left', 'Voicemail left', 2, true, false),
  ('gatekeeper_blocked', 'Gatekeeper blocked', 3, true, false),
  ('wrong_number', 'Wrong or bad number', 4, true, false),
  ('not_a_fit', 'Not a fit', 5, true, false),
  ('not_interested', 'Not interested', 6, true, true),
  ('callback_requested', 'Callback requested', 7, true, true),
  ('referred_to_colleague', 'Referred to colleague', 8, true, true),
  ('meeting_booked', 'Meeting booked', 9, true, true)
ON CONFLICT (key) DO UPDATE SET label = EXCLUDED.label, sort_order = EXCLUDED.sort_order,
  active = EXCLUDED.active, counts_as_contact = EXCLUDED.counts_as_contact;

-- Time types (11). Billable by default: client_work and overtime only.
-- A project is required except for vacation/absence; only those two may be entered ahead.
INSERT INTO time_type (key, label, sort_order, active, billable_default, requires_project, allows_future, category) VALUES
  ('client_work', 'Client Work', 1, true, true, true, false, 'client'),
  ('overtime', 'Overtime', 2, true, true, true, false, 'client'),
  ('internal_project', 'Internal Project', 3, true, false, true, false, 'internal'),
  ('internal_development', 'Internal Development', 4, true, false, true, false, 'internal'),
  ('presales_support', 'Presales Support', 5, true, false, true, false, 'internal'),
  ('support', 'Support', 6, true, false, true, false, 'internal'),
  ('customer_outreach', 'Customer Outreach', 7, true, false, true, false, 'internal'),
  ('resource_assignment_sales', 'Resource Assignment Sales', 8, true, false, true, false, 'internal'),
  ('travel', 'Travel', 9, true, false, true, false, 'internal'),
  ('vacation', 'Vacation', 10, true, false, false, true, 'absence'),
  ('absence', 'Absence', 11, true, false, false, true, 'absence')
ON CONFLICT (key) DO UPDATE SET label = EXCLUDED.label, sort_order = EXCLUDED.sort_order,
  active = EXCLUDED.active, billable_default = EXCLUDED.billable_default,
  requires_project = EXCLUDED.requires_project, allows_future = EXCLUDED.allows_future,
  category = EXCLUDED.category;

-- Countries (6). key = iso2.
INSERT INTO country (key, label, sort_order, active, iso2) VALUES
  ('SE', 'Sweden', 1, true, 'SE'),
  ('IT', 'Italy', 2, true, 'IT'),
  ('US', 'USA', 3, true, 'US'),
  ('AE', 'UAE', 4, true, 'AE'),
  ('ES', 'Spain', 5, true, 'ES'),
  ('NO', 'Norway', 6, true, 'NO')
ON CONFLICT (key) DO UPDATE SET label = EXCLUDED.label, sort_order = EXCLUDED.sort_order,
  active = EXCLUDED.active, iso2 = EXCLUDED.iso2;

-- Currencies (6). key = iso3, SEK is the base currency.
INSERT INTO currency (key, label, sort_order, active, iso3, decimals) VALUES
  ('SEK', 'Swedish krona', 1, true, 'SEK', 2),
  ('EUR', 'Euro', 2, true, 'EUR', 2),
  ('USD', 'US dollar', 3, true, 'USD', 2),
  ('NOK', 'Norwegian krone', 4, true, 'NOK', 2),
  ('AED', 'UAE dirham', 5, true, 'AED', 2),
  ('GBP', 'Pound sterling', 6, true, 'GBP', 2)
ON CONFLICT (key) DO UPDATE SET label = EXCLUDED.label, sort_order = EXCLUDED.sort_order,
  active = EXCLUDED.active, iso3 = EXCLUDED.iso3, decimals = EXCLUDED.decimals;
