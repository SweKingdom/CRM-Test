import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('..', import.meta.url));
const read = (path) => readFileSync(new URL(`../${path}`, import.meta.url), 'utf8');

const STEPS = ['lint', 'tests', 'seeds', 'openapi-regenerate', 'spectral', 'oasdiff', 'schemathesis', 'vulnerabilities'];
const bashAvailable = process.platform !== 'win32' && spawnSync('bash', ['--version']).status === 0;
const bash = { skip: bashAvailable ? false : 'needs bash on a POSIX platform' };

// Sources scripts/check without running it, replaces every step (and the optional ui step) with a stub, and runs main.
const stub = (name, body) => `step_${name.replaceAll('-', '_')}() { ${body}; }`;
function runCheck(bodies = {}, env = {}, args = '') {
  const stubs = [...STEPS, 'ui'].map((name) => stub(name, bodies[name] ?? 'true')).join('\n');
  const { REQUIRE_ALL, CI, ...clean } = process.env;
  const run = spawnSync('bash', ['-c', `source scripts/check\n${stubs}\nmain ${args}`], {
    cwd: root,
    encoding: 'utf8',
    env: { ...clean, ...env },
  });
  const summary = (run.stdout.split('=== Summary')[1] ?? '').split('\n');
  const rows = summary.map((l) => /^(PASS|FAIL|SKIP)\s+(\S+)/.exec(l)).filter(Boolean).map((m) => [m[1], m[2]]);
  return { status: run.status, stdout: run.stdout, rows };
}

test('AC 1: scripts/check and check.ps1 run the same eight steps in the same order', () => {
  const bashSteps = /^STEPS=\(([^)]*)\)/m.exec(read('scripts/check'))[1].trim().split(/\s+/);
  const psSteps = [.../^\$Steps = @\(([^)]*)\)/m.exec(read('scripts/check.ps1'))[1].matchAll(/'([^']+)'/g)].map((m) => m[1]);
  assert.deepEqual(bashSteps, STEPS);
  assert.deepEqual(psSteps, STEPS);
  for (const name of STEPS) {
    assert.match(read('scripts/check'), new RegExp(`^step_${name.replaceAll('-', '_')}\\(\\)`, 'm'));
    assert.match(read('scripts/check.ps1'), new RegExp(`^function Step-${name} `, 'm'));
  }
});

test('AC 1: the summary lists the eight steps in order and exits 0 when none failed', bash, () => {
  const { status, rows, stdout } = runCheck();
  assert.equal(status, 0);
  assert.deepEqual(rows, STEPS.map((name) => ['PASS', name]));
  assert.match(stdout, /^OK: no step failed/m);
});

test('AC 2: a failing step makes scripts/check exit non-zero, and the later steps still run', bash, () => {
  const { status, rows, stdout } = runCheck({ tests: 'false' });
  assert.equal(status, 1);
  assert.deepEqual(rows.map((r) => r[0]), ['PASS', 'FAIL', 'PASS', 'PASS', 'PASS', 'PASS', 'PASS', 'PASS']);
  assert.match(stdout, /^FAILED: 1 of 8 steps failed/m);
});

test('a SKIP is listed with its reason and does not fail the run', bash, () => {
  const { status, rows, stdout } = runCheck({ 'openapi-regenerate': 'skip na "not yet applicable: no generator"' });
  assert.equal(status, 0);
  assert.deepEqual(rows[3], ['SKIP', 'openapi-regenerate']);
  assert.match(stdout, /^SKIP\s+openapi-regenerate\s+not yet applicable: no generator/m);
  assert.match(stdout, /\(1 skipped\)/);
});

test('REQUIRE_ALL=1 fails a skipped step whose tool is missing, but not a "needs Keycloak" or not-applicable skip', bash, () => {
  const bodies = {
    oasdiff: 'skip tool "oasdiff is not installed"',
    schemathesis: 'skip keycloak "needs Keycloak"',
    spectral: 'skip na "not yet applicable"',
  };
  assert.equal(runCheck(bodies).status, 0);
  const strict = runCheck(bodies, { REQUIRE_ALL: '1' });
  assert.equal(strict.status, 1);
  assert.deepEqual(strict.rows.filter((r) => r[0] === 'FAIL'), [['FAIL', 'oasdiff']]);
  assert.deepEqual(strict.rows.filter((r) => r[0] === 'SKIP').map((r) => r[1]), ['spectral', 'schemathesis']);
});

test('US-146 AC 5: both scripts add the ui step only on request, and it is not one of the eight', () => {
  const script = read('scripts/check');
  assert.doesNotMatch(/^STEPS=\(([^)]*)\)/m.exec(script)[1], /\bui\b/);
  assert.match(script, /--ui\) steps\+=\(ui\)/);
  assert.match(script, /^step_ui\(\)/m);
  assert.match(read('scripts/check.ps1'), /^function Step-ui /m);
  assert.match(read('scripts/check.ps1'), /\$Ui -or \(\$args -contains '--ui'\)/);
});

test('US-146 AC 5: --ui runs a ninth step after the eight and its failure fails the check', bash, () => {
  const passing = runCheck({}, {}, '--ui');
  assert.equal(passing.status, 0);
  assert.deepEqual(passing.rows, [...STEPS, 'ui'].map((name) => ['PASS', name]));
  const failing = runCheck({ ui: 'false' }, {}, '--ui');
  assert.equal(failing.status, 1);
  assert.deepEqual(failing.rows.at(-1), ['FAIL', 'ui']);
  assert.match(failing.stdout, /^FAILED: 1 of 9 steps failed/m);
});

test('US-146 AC 5: an unknown argument is refused and runs nothing', bash, () => {
  const { status, stdout, rows } = runCheck({}, {}, '--bogus');
  assert.equal(status, 1);
  assert.match(stdout, /^Usage: scripts\/check \[--ui\]/m);
  assert.deepEqual(rows, []);
});

// Once the Playwright suite exists this test no longer applies: its own run is the proof.
const noE2e = { skip: !bashAvailable ? 'needs bash on a POSIX platform' : existsSync(new URL('../src/frontend/e2e', import.meta.url)) ? 'the e2e suite exists' : false };
test('US-146 AC 5: the real ui step fails with a clear message while src/frontend/e2e does not exist', noE2e, () => {
  const run = spawnSync('bash', ['-c', 'source scripts/check; step_ui'], { cwd: root, encoding: 'utf8' });
  assert.equal(run.status, 1);
  assert.match(run.stdout, /--ui was requested but src\/frontend\/e2e does not exist/);
});

test('PO answer #124: both scripts use the one API description path docs/api/openapi.json and the metadata generator', () => {
  assert.match(read('scripts/check'), /^OPENAPI_FILE=docs\/api\/openapi\.json$/m);
  assert.match(read('scripts/check.ps1'), /^\$OpenApiFile = 'docs\/api\/openapi\.json'$/m);
  for (const script of ['scripts/check', 'scripts/check.ps1']) {
    assert.match(read(script), /tools\/openapi-from-metadata\/generate\.mjs/);
    assert.doesNotMatch(read(script), /(^|[^/])openapi\/openapi\.json/, script);
  }
  assert.doesNotMatch(read('README.md'), /(^|[^/])openapi\/openapi\.json/);
});

test('AC 3: the workflow runs scripts/check on pull requests with read-only access and a Postgres service, and releases nothing', () => {
  const workflow = read('.github/workflows/check.yml');
  assert.match(workflow, /^on:\s*\n\s+pull_request:/m);
  assert.doesNotMatch(workflow, /pull_request_target/);
  assert.match(workflow, /^permissions:\s*\n\s+contents: read\s*$/m);
  assert.match(workflow, /run: bash scripts\/check\s*$/m);
  assert.match(workflow, /services:\s*\n\s+postgres:\s*\n\s+image: postgres:16/);
  assert.match(workflow, /--health-cmd/);
  assert.match(workflow, /cp \.env\.example \.env/);
  assert.match(workflow, /REQUIRE_ALL: "1"/);
  assert.doesNotMatch(workflow, /deploy|login|push|secrets\.|docker compose/i);
  for (const [, action] of workflow.matchAll(/uses: (\S+)/g)) assert.match(action, /@v\d+$/, action);
});

test('AC 3: the Postgres service uses the values of .env.example, and the .NET version is the project one', () => {
  const example = Object.fromEntries(
    read('.env.example').split(/\r?\n/).filter((l) => /^[A-Z_]+=/.test(l)).map((l) => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1)]),
  );
  const workflow = read('.github/workflows/check.yml');
  for (const key of ['POSTGRES_DB', 'POSTGRES_USER', 'POSTGRES_PASSWORD']) {
    assert.match(workflow, new RegExp(`^\\s+${key}: ${example[key]}$`, 'm'), key);
  }
  assert.match(workflow, /dotnet-version: 8\.0\.x/);
  assert.match(read('src/backend/Zephyr.Api/Zephyr.Api.csproj'), /<TargetFramework>net8\.0</);
});

test('Spectral is pinned to 6.15.0 in both scripts and extends spectral:oas', () => {
  assert.match(read('scripts/check'), /^SPECTRAL_VERSION=6\.15\.0$/m);
  assert.match(read('scripts/check.ps1'), /^\$SpectralVersion = '6\.15\.0'$/m);
  assert.match(read('.spectral.yaml'), /extends: \["spectral:oas"\]/);
});

test('the gate uses the CI service container in CI and leaves Compose services running', () => {
  const script = read('scripts/check');
  assert.match(script, /if \[ "\$\{CI:-\}" != true \]; then\s*\n\s+docker compose up -d --wait db/);
  assert.doesNotMatch(script, /docker compose (down|stop|rm)/);
});
