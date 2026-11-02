// AC 5: validate_seeds.py accepts the real seed files and rejects the broken fixtures.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { test } from 'node:test';

// On Windows `python3` can be a Store stub that exits non-zero, so probe each candidate.
const python = [['python3'], ['python'], ['py', '-3']].find(
  ([cmd, ...rest]) => spawnSync(cmd, [...rest, '--version']).status === 0,
);

function validate(dir) {
  assert.ok(python, 'Python 3 is required to run validate_seeds.py');
  const [cmd, ...rest] = python;
  return spawnSync(cmd, [...rest, '-I', 'scripts/validate_seeds.py', dir], { encoding: 'utf8' });
}

test('AC 5: valid seed files exit 0', () => {
  const r = validate('Seed');
  assert.equal(r.status, 0, r.stderr);
});

test('AC 5: INSERT without ON CONFLICT is rejected with file:line', () => {
  const r = validate('tests/seeds_broken/no_conflict');
  assert.equal(r.status, 1);
  assert.match(r.stderr, /0001_reference_data\.sql:2 INSERT without ON CONFLICT/);
});

test('AC 5: reference INSERT using DO NOTHING is rejected with file:line', () => {
  const r = validate('tests/seeds_broken/reference_do_nothing');
  assert.equal(r.status, 1);
  assert.match(r.stderr, /0001_reference_data\.sql:2 reference data INSERT must use DO UPDATE/);
});

test('AC 5: wrong row count is rejected with file:line', () => {
  const r = validate('tests/seeds_broken/wrong_count');
  assert.equal(r.status, 1);
  assert.match(r.stderr, /0001_reference_data\.sql:2 stage has 3 rows, expected 7/);
});
