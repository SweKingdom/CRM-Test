// Applies Seed/*.sql in filename order, one transaction per file.
// Usage: node scripts/seed.mjs [--dir <seed_dir>]
//        node scripts/seed.mjs --volume [--seed N] [--anchor YYYY-MM-DD] [--reset]
// --volume loads the deterministic volume seed (US-131) from tools/search-benchmark/generate instead:
// one file per entity, skipped with a notice while its table does not exist yet. It ends with ANALYZE of the loaded
// tables and, once the search index exists (US-147), REINDEX + ANALYZE of search_document.
import { existsSync, readFileSync } from 'node:fs';
import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import dotenv from 'dotenv';
import pg from 'pg';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const benchmarkDir = path.join(root, 'tools', 'search-benchmark');
const generateDir = path.join(benchmarkDir, 'generate');
const args = process.argv.slice(2);

function fail(message) {
  console.error(`seed: ${message}`);
  process.exit(1);
}

function option(name) {
  const i = args.indexOf(name);
  if (i < 0) return undefined;
  const value = args[i + 1];
  if (!value || value.startsWith('--')) fail(`${name} needs a value`);
  return value;
}

function isRealDate(text) {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(text);
  if (!match) return false;
  const [year, month, day] = match.slice(1).map(Number);
  const date = new Date(Date.UTC(year, month - 1, day));
  return date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day;
}

function volumeOptions() {
  const seed = option('--seed') ?? String(JSON.parse(readFileSync(path.join(generateDir, 'targets.json'), 'utf8')).seed);
  if (!/^\d{1,9}$/.test(seed)) fail(`--seed must be a whole number, got "${seed}"`);
  // Default anchor: today in Europe/Stockholm (the business time zone).
  const anchor = option('--anchor') ?? new Intl.DateTimeFormat('sv-SE', { timeZone: 'Europe/Stockholm' }).format(new Date());
  if (!isRealDate(anchor)) fail(`--anchor must be a date like 2026-12-11, got "${anchor}"`);
  return { seed, anchor, reset: args.includes('--reset') };
}

async function seedFiles() {
  const dirIndex = args.indexOf('--dir');
  const dir = dirIndex < 0 ? path.join(root, 'Seed') : args[dirIndex + 1];
  if (!dir) fail('--dir needs a directory');
  if (!existsSync(dir)) fail(`seed directory not found: ${dir}`);
  const names = (await readdir(dir)).filter((n) => n.endsWith('.sql')).sort();
  if (names.length === 0) fail(`no .sql files in ${dir}`);
  return names.map((n) => path.join(dir, n));
}

async function inTransaction(client, label, work) {
  try {
    await client.query('BEGIN');
    const result = await work();
    await client.query('COMMIT');
    return result;
  } catch (err) {
    await client.query('ROLLBACK');
    return fail(`${label} failed and was rolled back: ${err.message}`);
  }
}

async function applySeedFiles(client) {
  for (const file of await seedFiles()) {
    const sql = await readFile(file, 'utf8');
    await inTransaction(client, path.basename(file), () => client.query(sql));
    console.log(`seed: applied ${path.basename(file)}`);
  }
}

async function loadVolume(client, { seed, anchor, reset }) {
  const targets = JSON.parse(await readFile(path.join(generateDir, 'targets.json'), 'utf8'));
  const data = {};
  const dataDir = path.join(benchmarkDir, 'data');
  for (const name of (await readdir(dataDir)).filter((n) => n.endsWith('.json')).sort()) {
    Object.assign(data, JSON.parse(await readFile(path.join(dataDir, name), 'utf8')));
  }
  const helpers = await readFile(path.join(generateDir, '_helpers.sql'), 'utf8');

  const entities = [];
  for (const name of (await readdir(generateDir)).filter((n) => /^\d\d_.+\.sql$/.test(n)).sort()) {
    const entity = name.slice(3, -4);
    const table = targets.entities[entity]?.table;
    if (!table || !/^[a-z_][a-z0-9_]*$/.test(table)) fail(`${name}: no valid table for "${entity}" in targets.json`);
    const { rows } = await client.query('select to_regclass($1) is not null as present', [`public.${table}`]);
    if (rows[0].present) entities.push({ name, entity, table });
    else console.log(`SKIP ${entity}: table missing`);
  }

  const loaded = [];
  for (const { entity, table } of entities) {
    const { rows } = await client.query(`select exists (select 1 from ${table} where custom->>'synthetic' = 'true') as found`);
    if (rows[0].found) loaded.push(entity);
  }
  if (loaded.length > 0 && !reset) {
    fail(`synthetic rows already exist (${loaded.join(', ')}). Run again with --reset to remove them first.`);
  }
  if (reset) {
    // Children before parents; only rows carrying the synthetic marker are touched.
    await inTransaction(client, 'reset', async () => {
      for (const { table } of [...entities].reverse()) {
        const result = await client.query(`delete from ${table} where custom->>'synthetic' = 'true'`);
        console.log(`seed: removed ${result.rowCount} synthetic rows from ${table}`);
      }
    });
  }

  for (const { name, entity } of entities) {
    const sql = await readFile(path.join(generateDir, name), 'utf8');
    const result = await inTransaction(client, name, async () => {
      await client.query(
        `select set_config('zephyr.seed', $1, true), set_config('zephyr.anchor', $2, true),
                set_config('zephyr.data', $3, true), set_config('zephyr.targets', $4, true)`,
        [seed, anchor, JSON.stringify(data), JSON.stringify(targets)],
      );
      await client.query(helpers);
      return client.query(sql);
    });
    console.log(`seed: loaded ${result.rowCount} ${entity} rows`);
  }
  for (const { table } of entities) await client.query(`ANALYZE ${table}`);

  // The search triggers (US-147) index every row as it is inserted; a bulk load leaves the GIN indexes bloated and the
  // statistics stale, so rebuild and analyse them for the benchmark.
  const { rows } = await client.query("select to_regclass('public.search_document') is not null as present");
  if (rows[0].present) {
    await client.query('REINDEX TABLE search_document');
    await client.query('ANALYZE search_document');
    console.log('seed: reindexed and analysed search_document');
  }
}

const volume = args.includes('--volume');
if (!volume && ['--seed', '--anchor', '--reset'].some((a) => args.includes(a))) {
  fail('--seed, --anchor and --reset only work together with --volume');
}
const volumeSettings = volume ? volumeOptions() : undefined;

dotenv.config({ path: path.join(root, '.env') });
const connectionString = process.env.DATABASE_URL;
if (!connectionString) fail('DATABASE_URL is not set. Set it in the environment or in .env (see .env.example).');

const client = new pg.Client({ connectionString });
try {
  await client.connect();
} catch (err) {
  fail(`cannot connect to the database (${err.message}). Is it running and migrated?`);
}
try {
  if (volumeSettings) await loadVolume(client, volumeSettings);
  else await applySeedFiles(client);
} finally {
  await client.end();
}
