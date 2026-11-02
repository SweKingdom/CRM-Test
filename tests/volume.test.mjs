// US-131: the declared targets are consistent and seed.mjs rejects bad volume options before touching a database.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readdirSync, readFileSync } from 'node:fs';
import { test } from 'node:test';

const generate = 'tools/search-benchmark/generate';
const targets = JSON.parse(readFileSync(`${generate}/targets.json`, 'utf8'));
const entities = Object.entries(targets.entities);
const entityFiles = readdirSync(generate).filter((n) => /^\d\d_.+\.sql$/.test(n)).sort();

function seed(...args) {
  const env = { ...process.env };
  delete env.DATABASE_URL;
  return spawnSync('node', ['scripts/seed.mjs', ...args], { encoding: 'utf8', env });
}

test('AC 4: targets.json declares the totals', () => {
  const searchable = entities.filter(([name]) => name !== 'time_entry').reduce((sum, [, e]) => sum + e.count, 0);
  assert.equal(searchable, 56000);
  assert.equal(targets.totals.searchable, searchable);
  assert.equal(targets.entities.time_entry.count, 105000);
  assert.equal(targets.totals.time_entries, 105000);
  assert.equal(targets.entities.time_entry.trailing_12_months, 18495);
  assert.equal(targets.totals.time_entries_trailing_12_months, 18495);
});

test('AC 1: every entity in targets.json has exactly one NN_<entity>.sql file, in dependency order', () => {
  assert.deepEqual(entityFiles.map((n) => n.slice(3, -4)), entities.map(([name]) => name));
});

test('AC 3: every generated insert is idempotent on id and carries the synthetic marker', () => {
  assert.match(readFileSync(`${generate}/_helpers.sql`, 'utf8'), /jsonb_build_object\('synthetic', true\)/);
  for (const name of entityFiles) {
    const sql = readFileSync(`${generate}/${name}`, 'utf8');
    assert.match(sql, /ON CONFLICT \(id\) DO NOTHING/, name);
    assert.match(sql, /pg_temp\.marker\(|'synthetic', true/, name);
  }
});

test('--seed must be a whole number', () => {
  const r = seed('--volume', '--seed', 'abc');
  assert.equal(r.status, 1);
  assert.match(r.stderr, /--seed must be a whole number/);
});

test('--anchor must be a real date', () => {
  const r = seed('--volume', '--anchor', '2026-02-30');
  assert.equal(r.status, 1);
  assert.match(r.stderr, /--anchor must be a date/);
});

test('--reset without --volume is refused', () => {
  const r = seed('--reset');
  assert.equal(r.status, 1);
  assert.match(r.stderr, /only work together with --volume/);
});
