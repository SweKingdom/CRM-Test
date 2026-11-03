import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { parseEnv } from '../scripts/render-realms.mjs';
import { renderEnv } from '../scripts/setup-env.mjs';

const text = readFileSync(new URL('../.env.example', import.meta.url), 'utf8');
const example = parseEnv(text);

test('US-140: .env.example configures provider 0 as realm zephyr and keeps the second realm as a commented block', () => {
  assert.equal(example.ZEPHYR_AUTH__PROVIDERS__0__NAME, 'zephyr');
  assert.equal(example.ZEPHYR_AUTH__PROVIDERS__0__AUTHORITY, 'http://localhost:8180/realms/zephyr');
  assert.equal(example.ZEPHYR_AUTH__PROVIDERS__0__CLIENTID, 'zephyr-web');
  assert.equal(example.ZEPHYR_AUTH__PROVIDERS__0__EMAILTRUSTED, 'true');
  assert.equal(example.ZEPHYR_AUTH__PROVIDERS__0__CLIENTSECRET, 'change-me');
  assert.ok(!('ZEPHYR_AUTH__PROVIDERS__1__NAME' in example));
  assert.match(text, /^# ZEPHYR_AUTH__PROVIDERS__1__NAME=zephyr-b$/m);
  assert.match(text, /^# ZEPHYR_AUTH__PROVIDERS__1__AUTHORITY=http:\/\/localhost:8180\/realms\/zephyr-b$/m);
});

test('US-140: setup-env gives the API provider secret the same value as the realm client secret', () => {
  let n = 0;
  const { text: rendered } = renderEnv(text, () => `fake-value-${++n}`);
  const vars = parseEnv(rendered);
  assert.ok(vars.OIDC_CLIENT_SECRET.startsWith('fake-value-'));
  assert.equal(vars.ZEPHYR_AUTH__PROVIDERS__0__CLIENTSECRET, vars.OIDC_CLIENT_SECRET);
  assert.notEqual(vars.OIDC_CLIENT_SECRET, vars.POSTGRES_PASSWORD);
  // The commented second-realm block is left alone.
  assert.match(rendered, /^# ZEPHYR_AUTH__PROVIDERS__1__CLIENTSECRET=change-me$/m);
});

test('US-141: .env.example sets the session limits to 4 h idle, 12 h absolute and a 5 min status recheck', () => {
  assert.equal(example.SESSION_IDLE_SECONDS, '14400');
  assert.equal(example.SESSION_ABSOLUTE_SECONDS, '43200');
  assert.equal(example.USER_STATUS_RECHECK_SECONDS, '300');
});
