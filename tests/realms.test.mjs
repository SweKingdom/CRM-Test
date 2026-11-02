import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { parseEnv, plainSecrets, render } from '../scripts/render-realms.mjs';

const load = (name) => readFileSync(new URL(`../infra/keycloak/${name}`, import.meta.url), 'utf8');
const placeholders = (text) => [...new Set([...text.matchAll(/\$\{([A-Za-z0-9_]+)\}/g)].map((m) => m[1]))];

const allVars = Object.fromEntries(
  placeholders(load('zephyr-realm.template.json') + load('zephyr-realm-b.template.json')).map((name) => [name, `value-of-${name}`]),
);
allVars.ZEPHYR_BOOTSTRAP_ADMIN_SUBJECT = '7c1f0a52-3b8e-4d6a-9f21-5e4b8a0c1d01';

const roles = ['admin', 'salesperson', 'bdr', 'pm', 'consultant', 'finance', 'management'];
const passwordVars = [
  'KC_USER_ADMIN_PASSWORD',
  'KC_USER_SALESPERSON_PASSWORD',
  'KC_USER_BDR_PASSWORD',
  'KC_USER_PROJECT_MANAGER_PASSWORD',
  'KC_USER_CONSULTANT_PASSWORD',
  'KC_USER_FINANCE_PASSWORD',
  'KC_USER_MANAGEMENT_PASSWORD',
];

test('AC 1: realm zephyr renders with seven users, one per role', () => {
  const realm = JSON.parse(render(load('zephyr-realm.template.json'), allVars));
  assert.equal(realm.realm, 'zephyr');
  assert.deepEqual(realm.users.map((u) => u.username), roles.map((r) => `${r}@example.com`));
  for (const user of realm.users) {
    assert.equal(user.enabled, true);
    assert.equal(user.emailVerified, true);
    assert.equal(user.credentials[0].temporary, false);
  }
  const client = realm.clients[0];
  assert.equal(client.clientId, 'zephyr-web');
  assert.equal(client.publicClient, false);
  assert.equal(client.attributes['pkce.code.challenge.method'], 'S256');
});

test('AC 1: user IDs are fixed and unique', () => {
  const ids = JSON.parse(render(load('zephyr-realm.template.json'), allVars)).users.map((u) => u.id);
  assert.equal(new Set(ids).size, 7);
});

test('AC 1: the seven user passwords use exactly the agreed variable names, in role order', () => {
  const realm = JSON.parse(load('zephyr-realm.template.json'));
  assert.deepEqual(realm.users.map((u) => u.credentials[0].value), passwordVars.map((name) => `\${${name}}`));
});

test('AC 2: admin@example.com gets the subject from ZEPHYR_BOOTSTRAP_ADMIN_SUBJECT', () => {
  const vars = { ...allVars, ZEPHYR_BOOTSTRAP_ADMIN_SUBJECT: '11111111-2222-4333-8444-555555555555' };
  const admin = JSON.parse(render(load('zephyr-realm.template.json'), vars)).users.find((u) => u.username === 'admin@example.com');
  assert.equal(admin.id, '11111111-2222-4333-8444-555555555555');
});

test('AC 3: committed templates contain no plain passwords or secrets', () => {
  for (const name of ['zephyr-realm.template.json', 'zephyr-realm-b.template.json']) {
    assert.deepEqual(plainSecrets(JSON.parse(load(name))), [], name);
  }
  assert.deepEqual(plainSecrets({ credentials: [{ type: 'password', value: 'hunter2' }] }), ['$.credentials[0].value']);
});

test('AC 3: rendering fails and names every missing or empty variable', () => {
  const vars = { ...allVars, KC_USER_PROJECT_MANAGER_PASSWORD: '' };
  delete vars.KC_USER_BDR_PASSWORD;
  assert.throws(() => render(load('zephyr-realm.template.json'), vars), /KC_USER_BDR_PASSWORD, KC_USER_PROJECT_MANAGER_PASSWORD/);
});

test('AC 3: passwords with quotes stay valid JSON', () => {
  const realm = JSON.parse(render(load('zephyr-realm.template.json'), { ...allVars, KC_USER_PROJECT_MANAGER_PASSWORD: 'a"b\\c' }));
  assert.equal(realm.users.find((u) => u.username === 'pm@example.com').credentials[0].value, 'a"b\\c');
});

test('AC 3: .env.example values stay empty, so rendering from it unfilled fails; inline comments are not values', () => {
  const example = parseEnv(readFileSync(new URL('../.env.example', import.meta.url), 'utf8'));
  for (const name of passwordVars) assert.equal(example[name], '', name);
  assert.equal(example.OIDC_CLIENT_SECRET, '');
  assert.equal(parseEnv('A=secret # note').A, 'secret');
  assert.equal(parseEnv('A="x # y"').A, 'x # y');
  assert.equal(parseEnv('A=pa#ss').A, 'pa#ss');
});

test('AC 4: realm zephyr-b has its own realm name and one user', () => {
  const b = JSON.parse(render(load('zephyr-realm-b.template.json'), allVars));
  const a = JSON.parse(render(load('zephyr-realm.template.json'), allVars));
  assert.equal(b.realm, 'zephyr-b');
  assert.notEqual(b.realm, a.realm);
  assert.deepEqual(b.users.map((u) => u.username), ['user-b@example.com']);
  assert.equal(b.users[0].credentials[0].value, 'value-of-KC_USER_B_PASSWORD');
});

test('.env.example lists every variable the templates need and no retired role names', () => {
  const text = readFileSync(new URL('../.env.example', import.meta.url), 'utf8');
  const example = parseEnv(text);
  for (const name of Object.keys(allVars)) assert.ok(name in example, `${name} missing from .env.example`);
  assert.equal(example.ZEPHYR_BOOTSTRAP_ADMIN_ISSUER, 'http://localhost:8180/realms/zephyr');
  assert.doesNotMatch(text, /SALES_REP|SALES_MANAGER|APPROVER|READONLY|KC_USER_PM_PASSWORD/);
});
