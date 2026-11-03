// Writes a synthetic, deliberately messy, Dynamics-shaped export (one CSV per table plus manifest.json) for the import (US-184).
// Usage: node tools/fake-dynamics-export/generate.mjs [--seed 42] [--out ./out] [--scale 1]
// Same seed and scale give byte-identical files. All names come from names.json; nothing is read from the network or a clock.
import { closeSync, mkdirSync, openSync, readFileSync, writeFileSync, writeSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const names = JSON.parse(readFileSync(path.join(here, 'names.json'), 'utf8'));

const DAY = 86400000;
const TODAY = Date.UTC(2026, 9, 31); // fixed reference date, so output does not change from day to day
const INACTIVE_CUTOFF = Date.UTC(2023, 9, 31); // 36 months before TODAY
const WINDOW_DAYS = 730; // leads and activities are spread over 24 months

// Row counts at scale 1. Accounts include the duplicate rows. The total is 161,000.
const VOLUMES = { account: 3442, contact: 8323, lead: 6000, opportunity: 1195, project: 385, timeentry: 105000, activity: 36000, skill: 60, userskill: 595 };
const CONSULTANTS = 100;

// Deliberate mess, as exact counts at scale 1 (each scales with --scale), so the manifest is deterministic.
const MESS = {
  duplicateAccounts: 69, // 2% of the account rows repeat a GUID with slightly different values
  blankCountry: 1825, // 53% of the account rows
  countryTypos: 150,
  outsideSixUnits: 78,
  inactiveAccounts: 400, // no activity, opportunity or project, last change before the 36-month cutoff
  blankLeadSource: 600, // 10% of the leads
  messyStages: 420, // about 35% of the opportunities
  orphanContacts: 40,
  orphanOpportunities: 12,
  orphanTimeEntries: 105,
};

// The six units are an assumption until the field mapping spec lists them.
export const SIX_UNITS = ['Sweden', 'Norway', 'Denmark', 'Finland', 'Germany', 'United Kingdom'];
export const OUTSIDE_COUNTRIES = ['Italy', 'United States', 'France', 'Spain', 'Netherlands'];
export const COUNTRY_TYPOS = ['Sverige', 'Swedn', 'Norwey', 'Danmark', 'Finlnad', 'Itally', 'U.S.A.'];
export const STAGES = ['Qualify', 'Develop', 'Propose', 'Close'];
const MESSY_STAGES = ['qualify', 'QUALIFY', 'Qualification', '1 - Qualify', 'develop', 'Developing', 'Development', 'propose', 'Proposal', 'PROPOSE', 'close', 'Closing', 'Closed won', 'Won', 'closed-lost'];
const LEAD_SOURCES = ['Web', 'Referral', 'Event', 'Partner', 'Cold call', 'LinkedIn'];
const LEAD_STATUSES = ['New', 'Contacted', 'Qualified', 'Disqualified'];
const TOPICS = ['CRM upgrade', 'Data platform', 'Cloud migration', 'Support agreement', 'Analytics rollout', 'Integration project'];
const SUBJECTS = ['Follow-up call, pricing', 'Meeting: scope review', 'Email "Proposal" sent', 'Workshop planning', 'Contract renewal, next steps'];
const ACTIVITY_TYPES = ['email', 'phonecall', 'appointment'];
const TIME_TYPES = ['Billable', 'Internal', 'Absence'];

function mulberry32(seed) {
  let a = seed | 0;
  return () => {
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function csvField(value) {
  const text = value === null || value === undefined ? '' : String(value);
  return /[",\r\n]/.test(text) ? `"${text.replaceAll('"', '""')}"` : text;
}

const ascii = (text) => text.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase().replace(/[^a-z0-9]+/g, '');
const dateOf = (ms) => new Date(ms).toISOString().slice(0, 10);
const timeOf = (ms) => `${new Date(ms).toISOString().slice(0, 19)}Z`;

export function generate({ seed = 42, scale = 1, out = './out' } = {}) {
  const rand = mulberry32(seed);
  const int = (n) => Math.floor(rand() * n);
  const pick = (list) => list[int(list.length)];
  const between = (low, high) => low + int(high - low + 1);
  const ago = (days) => Math.floor((TODAY - rand() * days * DAY) / 1000) * 1000;
  const count = (base) => Math.round(base * scale);
  const rows = (name) => Math.max(1, count(VOLUMES[name]));
  const shuffle = (list) => {
    for (let i = list.length - 1; i > 0; i--) {
      const j = int(i + 1);
      [list[i], list[j]] = [list[j], list[i]];
    }
    return list;
  };
  // n labels with exactly the given number of each label in random order; the rest get the fallback label.
  const assign = (n, spec, fallback) => {
    const labels = new Array(n).fill(fallback);
    let at = 0;
    for (const [label, wanted] of spec) for (let k = 0; k < wanted && at < n; k++) labels[at++] = label;
    return shuffle(labels);
  };
  const guid = () => {
    let id = '';
    for (let i = 0; i < 32; i++) {
      const digit = i === 12 ? 4 : i === 16 ? 8 + int(4) : int(16);
      id += digit.toString(16);
      if (i === 7 || i === 11 || i === 15 || i === 19) id += '-';
    }
    return id;
  };
  const fullName = () => [pick(names.givenNames), pick(names.familyNames)];
  const phone = () => `+46-${between(10, 99)}-${between(100000, 999999)}`;
  const company = () => `${pick(names.companyStems)} ${pick(names.companyWords)} ${pick(names.companyForms)}`;

  const outDir = path.resolve(out);
  mkdirSync(outDir, { recursive: true });
  const files = {};
  const table = (name, columns) => {
    const fd = openSync(path.join(outDir, `${name}.csv`), 'w');
    let buffer = [columns.join(',')];
    let written = 0;
    const flush = () => {
      if (buffer.length > 0) writeSync(fd, `${buffer.join('\n')}\n`);
      buffer = [];
    };
    return {
      add(...values) {
        buffer.push(values.map(csvField).join(','));
        written++;
        if (buffer.length >= 2000) flush();
      },
      close() {
        flush();
        closeSync(fd);
        files[`${name}.csv`] = { rows: written };
      },
    };
  };
  const mess = Object.fromEntries(Object.keys(MESS).map((key) => [key, 0]));

  // Accounts: unique accounts first, then duplicate rows that repeat a GUID, all shuffled together.
  const accountRows = rows('account');
  const duplicates = Math.min(count(MESS.duplicateAccounts), Math.floor(accountRows / 2));
  const uniqueCount = accountRows - duplicates;
  const inactiveFlags = assign(uniqueCount, [['inactive', Math.min(count(MESS.inactiveAccounts), uniqueCount - 1)]], 'active');
  const used = new Set();
  const unique = [];
  for (let i = 0; i < uniqueCount; i++) {
    let name = company();
    for (let tries = 0; used.has(name) && tries < 1000; tries++) name = company();
    used.add(name);
    const inactive = inactiveFlags[i] === 'inactive';
    const modified = inactive ? INACTIVE_CUTOFF - between(30, 1000) * DAY : ago(WINDOW_DAYS);
    unique.push({
      id: guid(),
      name,
      city: pick(names.cities),
      phone: phone(),
      email: `info@${ascii(name)}.example`,
      inactive,
      created: modified - between(30, 1500) * DAY,
      modified,
    });
  }
  const accounts = unique.slice();
  for (let k = 0; k < duplicates; k++) {
    const source = pick(unique);
    accounts.push({ ...source, name: rand() < 0.5 ? source.name.toUpperCase() : source.name.toLowerCase(), city: pick(names.cities), phone: phone() });
  }
  shuffle(accounts);
  const countryKinds = assign(
    accountRows,
    [['blank', count(MESS.blankCountry)], ['typo', count(MESS.countryTypos)], ['outside', count(MESS.outsideSixUnits)]],
    'unit',
  );
  const accountTable = table('account', ['accountid', 'name', 'sirocco_countrychoice', 'address1_city', 'telephone1', 'emailaddress1', 'statecode', 'createdon', 'modifiedon']);
  accounts.forEach((account, i) => {
    const kind = countryKinds[i];
    let country = '';
    if (kind === 'unit') country = rand() < 0.6 ? 'Sweden' : pick(SIX_UNITS);
    if (kind === 'typo') country = pick(COUNTRY_TYPOS);
    if (kind === 'outside') country = pick(OUTSIDE_COUNTRIES);
    mess.blankCountry += kind === 'blank' ? 1 : 0;
    mess.countryTypos += kind === 'typo' ? 1 : 0;
    mess.outsideSixUnits += kind === 'outside' ? 1 : 0;
    accountTable.add(account.id, account.name, country, account.city, account.phone, account.email, 0, timeOf(account.created), timeOf(account.modified));
  });
  accountTable.close();
  mess.duplicateAccounts = duplicates;
  mess.inactiveAccounts = unique.filter((a) => a.inactive).length;
  const active = unique.filter((a) => !a.inactive);

  // Skills and the consultants who hold them.
  const staff = new Set();
  while (staff.size < CONSULTANTS) staff.add(fullName().join(' '));
  const consultants = [...staff];
  const skillTable = table('skill', ['skillid', 'name']);
  const skillIds = [];
  for (let i = 0; i < rows('skill'); i++) {
    const round = Math.floor(i / names.skills.length);
    skillIds.push(guid());
    skillTable.add(skillIds[i], round === 0 ? names.skills[i] : `${names.skills[i % names.skills.length]} ${round + 1}`);
  }
  skillTable.close();
  const userSkillTable = table('userskill', ['userskillid', 'skillid', 'sirocco_consultant', 'sirocco_level']);
  const pairs = new Set();
  while (pairs.size < rows('userskill')) {
    const pair = `${int(skillIds.length)}|${int(consultants.length)}`;
    if (pairs.has(pair)) continue;
    pairs.add(pair);
    const [skill, consultant] = pair.split('|').map(Number);
    userSkillTable.add(guid(), skillIds[skill], consultants[consultant], between(1, 5));
  }
  userSkillTable.close();

  // Contacts, leads, opportunities, projects.
  const contactOrphans = assign(rows('contact'), [['orphan', count(MESS.orphanContacts)]], 'linked');
  const contactTable = table('contact', ['contactid', 'firstname', 'lastname', 'emailaddress1', 'telephone1', 'jobtitle', 'parentcustomerid', 'createdon']);
  for (let i = 0; i < rows('contact'); i++) {
    const [first, last] = fullName();
    const orphan = contactOrphans[i] === 'orphan';
    mess.orphanContacts += orphan ? 1 : 0;
    contactTable.add(guid(), first, last, `${ascii(first)}.${ascii(last)}.${i + 1}@demo.example`, phone(), pick(names.jobTitles), orphan ? guid() : pick(unique).id, timeOf(ago(1500)));
  }
  contactTable.close();

  const leadRows = rows('lead');
  const leadSources = assign(leadRows, [['blank', count(MESS.blankLeadSource)]], 'set');
  const leadTable = table('lead', ['leadid', 'firstname', 'lastname', 'companyname', 'emailaddress1', 'leadsourcecode', 'statuscode', 'createdon']);
  for (let i = 0; i < leadRows; i++) {
    const [first, last] = fullName();
    const blank = leadSources[i] === 'blank';
    mess.blankLeadSource += blank ? 1 : 0;
    leadTable.add(guid(), first, last, company(), `${ascii(first)}.${ascii(last)}.${i + 1}@demo.example`, blank ? '' : pick(LEAD_SOURCES), pick(LEAD_STATUSES), timeOf(ago(WINDOW_DAYS)));
  }
  leadTable.close();

  const opportunityRows = rows('opportunity');
  const stages = assign(opportunityRows, [['messy', count(MESS.messyStages)]], 'clean');
  const opportunityOrphans = assign(opportunityRows, [['orphan', count(MESS.orphanOpportunities)]], 'linked');
  const opportunityTable = table('opportunity', ['opportunityid', 'name', 'parentaccountid', 'estimatedvalue', 'stepname', 'estimatedclosedate', 'statecode', 'createdon']);
  for (let i = 0; i < opportunityRows; i++) {
    const orphan = opportunityOrphans[i] === 'orphan';
    const account = orphan ? null : pick(active);
    mess.orphanOpportunities += orphan ? 1 : 0;
    const stage = stages[i] === 'messy' ? pick(MESSY_STAGES) : pick(STAGES);
    mess.messyStages += stages[i] === 'messy' ? 1 : 0;
    opportunityTable.add(guid(), `${account ? account.name : company()} - ${pick(TOPICS)}`, account ? account.id : guid(), between(50, 5000) * 1000, stage, dateOf(TODAY + between(-300, 300) * DAY), between(0, 2), timeOf(ago(WINDOW_DAYS)));
  }
  opportunityTable.close();

  const projects = [];
  const projectTable = table('project', ['projectid', 'name', 'sirocco_accountid', 'sirocco_contractingcountry', 'startdate', 'enddate', 'statecode']);
  for (let i = 0; i < rows('project'); i++) {
    const account = pick(active);
    const start = TODAY - between(30, 1400) * DAY;
    const end = start + between(90, 540) * DAY;
    projects.push({ id: guid(), start, end });
    projectTable.add(projects[i].id, `${account.name} ${pick(TOPICS)}`, account.id, rand() < 0.6 ? 'Sweden' : pick(SIX_UNITS), dateOf(start), dateOf(end), end < TODAY ? 1 : 0);
  }
  projectTable.close();

  // Time entries stream straight to the file; only the orphan flags are held in memory.
  const timeRows = rows('timeentry');
  const timeOrphans = assign(timeRows, [['orphan', count(MESS.orphanTimeEntries)]], 'linked');
  const timeTable = table('timeentry', ['timeentryid', 'sirocco_projectid', 'sirocco_consultant', 'sirocco_workdate', 'sirocco_hours', 'sirocco_timetype']);
  for (let i = 0; i < timeRows; i++) {
    const orphan = timeOrphans[i] === 'orphan';
    mess.orphanTimeEntries += orphan ? 1 : 0;
    let projectId = guid();
    let workDate = TODAY - int(1000) * DAY;
    if (!orphan) {
      const project = pick(projects);
      projectId = project.id;
      workDate = project.start + int(Math.floor((Math.min(project.end, TODAY) - project.start) / DAY) + 1) * DAY;
    }
    timeTable.add(guid(), projectId, pick(consultants), dateOf(workDate), between(1, 16) / 2, pick(TIME_TYPES));
  }
  timeTable.close();

  const activityTable = table('activity', ['activityid', 'activitytypecode', 'subject', 'regardingobjectid', 'scheduledstart', 'statecode']);
  for (let i = 0; i < rows('activity'); i++) {
    activityTable.add(guid(), pick(ACTIVITY_TYPES), pick(SUBJECTS), pick(active).id, timeOf(ago(WINDOW_DAYS)), between(0, 1));
  }
  activityTable.close();

  const manifest = {
    seed,
    scale,
    referenceDate: dateOf(TODAY),
    files,
    totalRows: Object.values(files).reduce((sum, file) => sum + file.rows, 0),
    mess,
  };
  writeFileSync(path.join(outDir, 'manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`);
  return manifest;
}

function fail(message) {
  console.error(`fake-dynamics-export: ${message}`);
  process.exit(1);
}

function parseArgs(argv) {
  const options = { seed: 42, scale: 1, out: './out' };
  for (let i = 0; i < argv.length; i += 2) {
    const name = argv[i];
    const value = argv[i + 1];
    if (!['--seed', '--scale', '--out'].includes(name)) fail(`unknown option ${name}. Use --seed, --out and --scale.`);
    if (value === undefined || value.startsWith('--')) fail(`${name} needs a value`);
    if (name === '--out') options.out = value;
    if (name === '--seed') {
      if (!/^-?\d+$/.test(value)) fail('--seed must be a whole number, for example 42');
      options.seed = Number(value);
    }
    if (name === '--scale') {
      if (!(Number(value) > 0) || !Number.isFinite(Number(value))) fail('--scale must be a number above 0, for example 1 or 0.1');
      options.scale = Number(value);
    }
  }
  return options;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const options = parseArgs(process.argv.slice(2));
  const manifest = generate(options);
  console.log(`Wrote ${Object.keys(manifest.files).length} files, ${manifest.totalRows} rows, to ${path.resolve(options.out)}`);
  for (const [file, { rows }] of Object.entries(manifest.files)) console.log(`  ${file}: ${rows}`);
  console.log('Mess:');
  for (const [name, value] of Object.entries(manifest.mess)) console.log(`  ${name}: ${value}`);
}
