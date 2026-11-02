// Applies Seed/*.sql in filename order, one transaction per file.
// Usage: node scripts/seed.mjs [--dir <seed_dir>] [--volume]
// --volume applies the deterministic volume seed (US-131) instead.
import { existsSync } from 'node:fs';
import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import dotenv from 'dotenv';
import pg from 'pg';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);

function fail(message) {
  console.error(`seed: ${message}`);
  process.exit(1);
}

async function seedFiles() {
  if (args.includes('--volume')) {
    const file = path.join(root, 'tools', 'search-benchmark', 'generate.sql');
    if (!existsSync(file)) fail(`volume seed not found at ${file}`);
    return [file];
  }
  const dirIndex = args.indexOf('--dir');
  const dir = dirIndex < 0 ? path.join(root, 'Seed') : args[dirIndex + 1];
  if (!dir) fail('--dir needs a directory');
  if (!existsSync(dir)) fail(`seed directory not found: ${dir}`);
  const names = (await readdir(dir)).filter((n) => n.endsWith('.sql')).sort();
  if (names.length === 0) fail(`no .sql files in ${dir}`);
  return names.map((n) => path.join(dir, n));
}

dotenv.config({ path: path.join(root, '.env') });
const connectionString = process.env.DATABASE_URL;
if (!connectionString) fail('DATABASE_URL is not set. Set it in the environment or in .env (see .env.example).');

const files = await seedFiles();
const client = new pg.Client({ connectionString });
try {
  await client.connect();
} catch (err) {
  fail(`cannot connect to the database (${err.message}). Is it running and migrated?`);
}
try {
  for (const file of files) {
    const sql = await readFile(file, 'utf8');
    try {
      await client.query('BEGIN');
      await client.query(sql);
      await client.query('COMMIT');
      console.log(`seed: applied ${path.basename(file)}`);
    } catch (err) {
      await client.query('ROLLBACK');
      fail(`${path.basename(file)} failed and was rolled back: ${err.message}`);
    }
  }
} finally {
  await client.end();
}
