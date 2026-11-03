// US-184: the synthetic Dynamics export has the stated volumes and mess, is deterministic and uses fake names only.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { after, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { COUNTRY_TYPOS, OUTSIDE_COUNTRIES, STAGES } from './generate.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const script = path.join(here, 'generate.mjs');
const names = JSON.parse(readFileSync(path.join(here, 'names.json'), 'utf8'));
const dirs = [];
after(() => dirs.forEach((dir) => rmSync(dir, { recursive: true, force: true })));

function run(args) {
  const out = mkdtempSync(path.join(tmpdir(), 'fake-dynamics-'));
  dirs.push(out);
  const result = spawnSync('node', [script, '--out', out, ...args], { encoding: 'utf8' });
  return { out, result };
}

function generated(args) {
  const { out, result } = run(args);
  assert.equal(result.status, 0, result.stderr);
  return out;
}

let fullScale;
const full = () => (fullScale ??= generated(['--seed', '42', '--scale', '1']));
const manifestOf = (dir) => JSON.parse(readFileSync(path.join(dir, 'manifest.json'), 'utf8'));

// RFC 4180 reader: quoted fields may hold commas, quotes ("") and newlines.
function readCsv(dir, name) {
  const text = readFileSync(path.join(dir, `${name}.csv`), 'utf8');
  assert.ok(!text.includes('\r'), `${name}.csv uses \\n line endings only`);
  const records = [];
  let record = [];
  let field = '';
  let quoted = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (quoted) {
      if (c !== '"') field += c;
      else if (text[i + 1] === '"') (field += '"', i++);
      else quoted = false;
    } else if (c === '"') quoted = true;
    else if (c === ',') (record.push(field), (field = ''));
    else if (c === '\n') (record.push(field), records.push(record), (record = []), (field = ''));
    else field += c;
  }
  const [header, ...data] = records;
  return { header, rows: data.map((r) => Object.fromEntries(header.map((h, k) => [h, r[k]]))) };
}

const hashes = (dir) =>
  Object.fromEntries(readdirSync(dir).sort().map((name) => [name, createHash('sha256').update(readFileSync(path.join(dir, name))).digest('hex')]));

const TARGETS = { account: 3442, contact: 8323, lead: 6000, opportunity: 1195, project: 385, timeentry: 105000, activity: 36000, skill: 60, userskill: 595 };

test('AC 1: a file for every mapped table with the stated volumes (±1%), and the total is about 161,000', () => {
  const dir = full();
  const manifest = manifestOf(dir);
  for (const [table, target] of Object.entries(TARGETS)) {
    const { rows } = readCsv(dir, table);
    assert.equal(manifest.files[`${table}.csv`].rows, rows.length, `${table}: manifest matches the file`);
    assert.ok(Math.abs(rows.length - target) <= target * 0.01, `${table}: ${rows.length} is not within 1% of ${target}`);
  }
  assert.ok(Math.abs(manifest.totalRows - 161000) <= 1610, `total ${manifest.totalRows}`);
  assert.equal(manifest.seed, 42);
  assert.ok(readCsv(dir, 'account').header.includes('sirocco_countrychoice'));
});

test('AC 1: --scale multiplies the volumes', () => {
  const manifest = manifestOf(generated(['--scale', '0.1']));
  assert.equal(manifest.files['timeentry.csv'].rows, 10500);
  assert.equal(manifest.files['account.csv'].rows, 344);
});

test('AC 2: mess counts in the manifest are above zero and equal the counts recomputed from the CSVs', () => {
  const dir = full();
  const { mess } = manifestOf(dir);
  const accounts = readCsv(dir, 'account').rows;
  const ids = new Set(accounts.map((a) => a.accountid));
  const inactive = new Set(accounts.filter((a) => a.modifiedon < '2023-10-31').map((a) => a.accountid));
  const projectIds = new Set(readCsv(dir, 'project').rows.map((p) => p.projectid));
  const opportunities = readCsv(dir, 'opportunity').rows;
  const projects = readCsv(dir, 'project').rows;
  const activities = readCsv(dir, 'activity').rows;
  const recomputed = {
    duplicateAccounts: accounts.length - ids.size,
    blankCountry: accounts.filter((a) => a.sirocco_countrychoice === '').length,
    countryTypos: accounts.filter((a) => COUNTRY_TYPOS.includes(a.sirocco_countrychoice)).length,
    outsideSixUnits: accounts.filter((a) => OUTSIDE_COUNTRIES.includes(a.sirocco_countrychoice)).length,
    inactiveAccounts: inactive.size,
    blankLeadSource: readCsv(dir, 'lead').rows.filter((l) => l.leadsourcecode === '').length,
    messyStages: opportunities.filter((o) => !STAGES.includes(o.stepname)).length,
    orphanContacts: readCsv(dir, 'contact').rows.filter((c) => !ids.has(c.parentcustomerid)).length,
    orphanOpportunities: opportunities.filter((o) => !ids.has(o.parentaccountid)).length,
    orphanTimeEntries: readCsv(dir, 'timeentry').rows.filter((t) => !projectIds.has(t.sirocco_projectid)).length,
  };
  assert.deepEqual(mess, recomputed);
  for (const [name, value] of Object.entries(mess)) assert.ok(value > 0, `${name} is zero`);
  assert.equal(mess.blankCountry, 1825);
  assert.equal(mess.outsideSixUnits, 78);
  assert.equal(mess.blankLeadSource, 600);
  // Inactive accounts have no activity, opportunity or project, so the import filter has something to drop.
  const referenced = [...activities.map((a) => a.regardingobjectid), ...opportunities.map((o) => o.parentaccountid), ...projects.map((p) => p.sirocco_accountid)];
  assert.ok(!referenced.some((id) => inactive.has(id)));
  // A duplicate GUID repeats with different values.
  const first = new Map();
  const differing = accounts.filter((a) => {
    const earlier = first.get(a.accountid);
    first.set(a.accountid, earlier ?? a);
    return earlier && (earlier.name !== a.name || earlier.telephone1 !== a.telephone1);
  });
  assert.equal(differing.length, mess.duplicateAccounts);
});

test('AC 2: quoting survives commas and quotes in values', () => {
  const subjects = new Set(readCsv(full(), 'activity').rows.map((a) => a.subject));
  assert.ok(subjects.has('Follow-up call, pricing'));
  assert.ok(subjects.has('Email "Proposal" sent'));
});

test('AC 3: the same seed gives identical files, another seed does not', () => {
  const a = hashes(generated(['--seed', '7', '--scale', '0.1']));
  const b = hashes(generated(['--seed', '7', '--scale', '0.1']));
  const c = hashes(generated(['--seed', '8', '--scale', '0.1']));
  assert.equal(Object.keys(a).length, 10);
  assert.deepEqual(a, b);
  for (const name of Object.keys(a).filter((n) => n !== 'skill.csv')) assert.notEqual(a[name], c[name], `${name} should differ for another seed`);
});

test('AC 3: the source uses no random or clock calls', () => {
  assert.doesNotMatch(readFileSync(script, 'utf8'), /Math\.random|Date\.now|new Date\(\)|node:crypto/);
});

test('AC 4: every account, contact and lead name token comes from names.json', () => {
  const dir = full();
  const allowed = new Set(
    [names.givenNames, names.familyNames, names.companyStems, names.companyWords, names.companyForms].flat().map((n) => n.toLowerCase()),
  );
  const check = (label, text) => {
    for (const token of text.split(/\s+/)) assert.ok(allowed.has(token.toLowerCase()), `${label}: "${token}" is not in names.json`);
  };
  for (const a of readCsv(dir, 'account').rows) check('account', a.name);
  for (const c of readCsv(dir, 'contact').rows) check('contact', `${c.firstname} ${c.lastname}`);
  for (const l of readCsv(dir, 'lead').rows) check('lead', `${l.firstname} ${l.lastname} ${l.companyname}`);
  for (const t of readCsv(dir, 'timeentry').rows.slice(0, 2000)) check('consultant', t.sirocco_consultant);
  for (const email of readCsv(dir, 'contact').rows.map((c) => c.emailaddress1)) assert.match(email, /@demo\.example$/);
});

test('bad options are refused in plain language and write nothing', () => {
  for (const [args, message] of [
    [['--seed', 'abc'], /--seed must be a whole number/],
    [['--scale', '0'], /--scale must be a number above 0/],
    [['--scale', 'x'], /--scale must be a number above 0/],
    [['--bogus', '1'], /unknown option --bogus/],
    [['--seed'], /--seed needs a value/],
  ]) {
    const { out, result } = run(args);
    assert.equal(result.status, 1, args.join(' '));
    assert.match(result.stderr, message);
    assert.deepEqual(readdirSync(out), []);
  }
});
