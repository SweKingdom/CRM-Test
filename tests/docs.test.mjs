import assert from 'node:assert/strict';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { test } from 'node:test';

const read = (path) => readFileSync(new URL(`../${path}`, import.meta.url), 'utf8').replace(/\r\n/g, '\n');
const section = (text, heading) => {
  const start = text.indexOf(`## ${heading}`);
  assert.notEqual(start, -1, `heading "${heading}" not found`);
  const rest = text.slice(start + 3);
  const next = rest.search(/^## /m);
  return next === -1 ? rest : rest.slice(0, next);
};

test('AC 1: README "Run Zephyr locally" lists the commands in order with URLs and a seeded user', () => {
  const body = section(read('README.md'), 'Run Zephyr locally');
  const order = ['git clone', 'npm ci', 'scripts/setup-env.mjs', 'docker compose up', 'DATABASE_URL=', 'npm run seed', '/health', 'npm run typecheck', 'docker compose down'];
  let last = -1;
  for (const command of order) {
    const at = body.indexOf(command, last + 1);
    assert.ok(at > last, `"${command}" missing or out of order`);
    last = at;
  }
  for (const url of ['http://localhost:8080', 'http://localhost:3000', 'http://localhost:8180', 'http://localhost:8025']) {
    assert.ok(body.includes(url), `${url} missing`);
  }
  assert.ok(body.includes('salesperson@example.com'));
});

test('AC 1: README holds no password values', () => {
  const readme = read('README.md');
  assert.doesNotMatch(readme, /^\s*(KC_USER_\w+|POSTGRES_PASSWORD|OIDC_CLIENT_SECRET)=\S/m);
  assert.doesNotMatch(readme, /postgres:\/\/\w+:[^$\s]/);
});

test('AC 2: every BUILD.md section has all six template parts and a review line', () => {
  const template = read('build/prompts/feature-section.md');
  const parts = [...template.matchAll(/^### (.+)$/gm)].map((m) => m[1].trim());
  assert.equal(parts.length, 6, 'the template defines six parts');
  const sections = read('BUILD.md').split(/^## /m).slice(1);
  assert.ok(sections.length >= 1, 'BUILD.md has at least one feature section');
  for (const body of sections) {
    const title = body.split('\n')[0];
    for (const part of parts) assert.match(body, new RegExp(`^### ${part}$`, 'm'), `${title}: missing "${part}"`);
    assert.match(body, /^Reviewed by: \S/m, `${title}: missing review line`);
  }
});

test('AC 3: the local-edition ADR defers ADR-001 and ADR-002 and does not replace them', () => {
  const name = readdirSync(new URL('../docs/adr/', import.meta.url)).find((n) => /^ADR-\d+-local-edition\.md$/.test(n));
  assert.ok(name, 'local-edition ADR file exists');
  const adr = read(`docs/adr/${name}`);
  assert.match(adr, /^Status: Proposed$/m);
  assert.match(adr, /defers ADR-001 and ADR-002/);
  assert.match(adr, /does not supersede or replace/);
});

test('AC 4: the ADR index lists the six ADRs and the local edition, none Accepted without an approval PR', () => {
  const rows = read('docs/adr/README.md')
    .split('\n')
    .filter((line) => /^\| (\[)?ADR-\d+/.test(line))
    .map((line) => line.split('|').map((cell) => cell.trim()));
  const ids = rows.map((cells) => /ADR-\d+/.exec(cells[1])[0]);
  for (const id of ['ADR-004', 'ADR-006', 'ADR-007', 'ADR-008', 'ADR-009', 'ADR-012', 'ADR-013']) {
    assert.ok(ids.includes(id), `${id} is not listed`);
  }
  for (const cells of rows) {
    const [, adr, , status, approver, pr] = cells;
    assert.ok(status && approver && pr, `${adr}: status, approver and approval PR columns are filled`);
    if (/^Accepted$/i.test(status)) assert.notEqual(pr, '—', `${adr} is Accepted without an approval PR`);
  }
  assert.ok(existsSync(new URL('../docs/adr/ADR-013-local-edition.md', import.meta.url)));
});

test('the PR template carries the BUILD.md checklist line', () => {
  assert.ok(read('.github/pull_request_template.md').includes('BUILD.md section added and reviewed by second developer'));
});

test('US-138 AC 3: the PR template has a Claude usage section with tokens in, tokens out and cost fields', () => {
  const body = section(read('.github/pull_request_template.md'), 'Claude usage');
  for (const field of ['Tokens in:', 'Tokens out:', 'Cost (USD):']) {
    assert.ok(body.includes(field), `${field} missing`);
  }
  assert.ok(body.includes('node tools/claude-usage/cli.mjs report'), 'points to the existing report command');
  assert.ok(body.includes('docs/claude-usage/'));
});

test('US-138: no second usage script or {story}.md log format is added next to the existing hook output', () => {
  assert.equal(existsSync(new URL('../scripts/claude-usage.mjs', import.meta.url)), false);
  assert.equal(existsSync(new URL('../docs/claude-usage/', import.meta.url)) && readdirSync(new URL('../docs/claude-usage/', import.meta.url)).some((n) => /^[A-Z]{2}-\d+\.md$/.test(n)), false);
});
