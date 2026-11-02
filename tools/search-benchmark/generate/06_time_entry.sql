-- Synthetic time entries (US-131). Assumes time_entry(project_id, work_date, duration_minutes, notes).
-- Exactly trailing_12_months entries fall in [anchor - 12 months, anchor], spread evenly over those days; the rest
-- spread evenly over the 1,460 days before the window, so the split never depends on rounding or random draws.
INSERT INTO time_entry (id, project_id, work_date, duration_minutes, notes, custom)
SELECT pg_temp.uid('time_entry', n),
       pg_temp.uid('project', 1 + pg_temp.h('time_entry', n, 'a') % pg_temp.target('project')),
       CASE WHEN n <= w.recent
            THEN w.start + (((n - 1) * (w.days + 1)) / w.recent)
            ELSE w.start - 1 - (((n - w.recent - 1) * 1460) / (w.total - w.recent))
       END,
       15 * (4 + pg_temp.h('time_entry', n, 'b') % 29),
       pg_temp.pick('notes', pg_temp.h('time_entry', n, 'c')),
       jsonb_build_object('synthetic', true)
FROM (SELECT pg_temp.target('time_entry') AS total,
             pg_temp.target('time_entry', 'trailing_12_months') AS recent,
             (current_setting('zephyr.anchor')::date - interval '12 months')::date AS start,
             current_setting('zephyr.anchor')::date - (current_setting('zephyr.anchor')::date - interval '12 months')::date AS days) w
CROSS JOIN LATERAL generate_series(1, w.total) AS n
ORDER BY n
ON CONFLICT (id) DO NOTHING;
