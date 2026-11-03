// Search benchmark (US-147 AC 5; the full-volume run is part of US-150).
// 100 searches against a running API over the volume seed (npm run seed -- --volume): a third by whole word without accents,
// a third by 3-letter prefix, a third with one typo. 95 of the 100 must answer within 0.5 s.
// Usage: node tools/search-benchmark/bench.mjs [--explain]
// Needs DATABASE_URL (to draw the sample titles; read from .env like seed.mjs) and ZEPHYR_BENCH_COOKIE, the Cookie header
// of a signed-in Management user, for example "zephyr.session=...". Optional: ZEPHYR_API_URL (default http://localhost:8080).
// --explain also prints the plans of both index predicates, to confirm the GIN indexes are used.
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import dotenv from 'dotenv';
import pg from 'pg';

const QUERIES = 100;
const WITHIN_MS = 500;
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
dotenv.config({ path: path.join(root, '.env') });

const { DATABASE_URL: databaseUrl, ZEPHYR_BENCH_COOKIE: cookie } = process.env;
const apiUrl = process.env.ZEPHYR_API_URL ?? 'http://localhost:8080';
if (!databaseUrl) throw new Error('DATABASE_URL is not set. Set it in the environment or in .env.');
if (!cookie) throw new Error('ZEPHYR_BENCH_COOKIE is not set. Sign in as a Management user and pass the session cookie.');

const plain = (text) => text.normalize('NFD').replace(/\p{M}/gu, '').toLowerCase();

// Deterministic sample: the same titles on every run of the same data.
function queryFor(title, i) {
  const word = plain(title).split(/[^\p{L}\p{N}]+/u).find((w) => w.length >= 4) ?? plain(title);
  if (i % 3 === 0) return word;
  if (i % 3 === 1) return word.slice(0, 3);
  const cut = Math.floor(word.length / 2);
  return word.slice(0, cut) + word.slice(cut + 1); // one letter missing
}

const db = new pg.Client({ connectionString: databaseUrl });
await db.connect();
try {
  const { rows } = await db.query('select title from search_document order by md5(entity_type || entity_id::text) limit $1', [QUERIES]);
  if (rows.length < QUERIES) throw new Error(`search_document has ${rows.length} rows; load the volume seed first.`);

  const times = [];
  for (const [i, { title }] of rows.entries()) {
    const url = `${apiUrl}/api/v1/search?q=${encodeURIComponent(queryFor(title, i))}`;
    const started = performance.now();
    const response = await fetch(url, { headers: { cookie } });
    await response.arrayBuffer();
    times.push(performance.now() - started);
    if (!response.ok) throw new Error(`${url} answered ${response.status}`);
  }

  times.sort((a, b) => a - b);
  const p95 = times[Math.ceil(QUERIES * 0.95) - 1];
  console.log(`bench: ${QUERIES} searches, median ${times[49].toFixed(0)} ms, p95 ${p95.toFixed(0)} ms, max ${times[QUERIES - 1].toFixed(0)} ms`);

  if (process.argv.includes('--explain')) {
    await db.query('set enable_seqscan = off');
    for (const where of ["tsv @@ to_tsquery('simple', 'abc:*')", "'abcd' <% text"]) {
      const plan = await db.query(`explain (analyze, costs off) select 1 from search_document where ${where}`);
      console.log(`bench: ${where}\n${plan.rows.map((r) => `  ${r['QUERY PLAN']}`).join('\n')}`);
    }
  }

  if (p95 > WITHIN_MS) {
    console.error(`bench: FAIL, p95 ${p95.toFixed(0)} ms is above ${WITHIN_MS} ms`);
    process.exitCode = 1;
  } else {
    console.log(`bench: PASS, 95 of ${QUERIES} searches within ${WITHIN_MS} ms`);
  }
} finally {
  await db.end();
}
