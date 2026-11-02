import assert from "node:assert/strict";
import { existsSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { renderEnv, setupEnv } from "./setup-env.mjs";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const read = (path) => readFileSync(join(root, path), "utf8");
const parseEnv = (text) =>
  Object.fromEntries(
    text
      .split(/\r?\n/)
      .filter((line) => /^[A-Za-z0-9_]+=/.test(line))
      .map((line) => [line.slice(0, line.indexOf("=")), line.slice(line.indexOf("=") + 1)]),
  );

const compose = read("docker-compose.yml");
const example = read(".env.example");

test("AC 1: compose defines the five services with healthchecks", () => {
  for (const service of ["db", "api", "web", "keycloak", "mailpit"]) {
    assert.match(compose, new RegExp(`^  ${service}:`, "m"), `${service} missing`);
  }
  assert.equal((compose.match(/healthcheck:/g) ?? []).length, 5);
  assert.match(read("src/backend/Zephyr.Api/Program.cs"), /MapHealthChecks\("\/health"/);
  assert.match(compose, /127\.0\.0\.1:8080:8080/);
});

test("AC 2: mail is caught by Mailpit and no relay is configured", () => {
  assert.match(compose, /127\.0\.0\.1:1025:1025/);
  assert.match(compose, /127\.0\.0\.1:8025:8025/);
  assert.doesNotMatch(compose, /MP_SMTP_RELAY/);
  assert.equal(parseEnv(example).SMTP_HOST, "mailpit");
});

test("every published port is bound to 127.0.0.1", () => {
  const unbound = [...compose.matchAll(/^\s+- "(?!127\.0\.0\.1:)\d[^"]*"/gm)];
  assert.equal(unbound.length, 0);
  assert.equal([...compose.matchAll(/^\s+- "127\.0\.0\.1:[^"]+"/gm)].length, 6);
});

test("AC 3: .env is ignored, .env.example is not, and contains placeholders only", () => {
  const ignore = read(".gitignore").split(/\r?\n/);
  assert.ok(ignore.includes(".env"));
  assert.ok(!ignore.some((l) => l === ".env.example" || l === ".env*"));
  const vars = parseEnv(example);
  const secrets = Object.entries(vars).filter(
    ([k]) => /PASSWORD|SECRET|SUBJECT/.test(k) && k !== "ConnectionStrings__Default",
  );
  assert.ok(secrets.length >= 10);
  for (const [key, value] of secrets) assert.equal(value, "change-me", key);
  assert.match(vars.ConnectionStrings__Default, /Password=change-me$/);
  assert.equal(Object.keys(vars).filter((k) => /^KC_USER_.*_PASSWORD$/.test(k)).length, 7);
});

test("AC 3: every variable compose interpolates is listed in .env.example", () => {
  const vars = parseEnv(example);
  for (const [, name] of compose.matchAll(/(?<!\$)\$\{(\w+)\}/g)) {
    assert.ok(name in vars, `${name} missing from .env.example`);
  }
});

test("AC 4: API logs JSON to stdout, reads env config, and references no cloud SDK", () => {
  assert.match(read("src/backend/Zephyr.Api/Program.cs"), /AddJsonConsole/);
  assert.match(read("src/backend/Zephyr.Api/Dockerfile"), /ASPNETCORE_URLS=http:\/\/\+:8080/);
  assert.match(compose, /env_file: \.env/);
  assert.doesNotMatch(read("src/backend/Zephyr.Api/Zephyr.Api.csproj"), /Azure|AWSSDK|Google\.Cloud/);
  assert.doesNotMatch(compose, /privileged|docker\.sock/);
});

test("setup-env: generates random passwords and keeps the DB password in sync", () => {
  let n = 0;
  const { text, generated } = renderEnv(example, () => `pw${++n}`);
  const vars = parseEnv(text);
  assert.doesNotMatch(text, /(PASSWORD|SECRET)=change-me/);
  assert.equal(vars.POSTGRES_PASSWORD, "pw1");
  assert.match(vars.ConnectionStrings__Default, /Password=pw1$/);
  assert.equal(vars.ZEPHYR_BOOTSTRAP_ADMIN_SUBJECT, "change-me");
  const kc = Object.entries(vars)
    .filter(([k]) => /^KC_USER_/.test(k))
    .map(([, v]) => v);
  assert.equal(new Set(kc).size, 7);
  assert.ok(generated.includes("KEYCLOAK_ADMIN_PASSWORD"));
});

test("setup-env: creates .env once and never overwrites an existing one", () => {
  const dir = mkdtempSync(join(tmpdir(), "zephyr-env-"));
  writeFileSync(join(dir, ".env.example"), example);
  assert.ok(Array.isArray(setupEnv(dir)));
  assert.ok(existsSync(join(dir, ".env")));
  writeFileSync(join(dir, ".env"), "KEEP=me\n");
  assert.equal(setupEnv(dir), null);
  assert.equal(readFileSync(join(dir, ".env"), "utf8"), "KEEP=me\n");
});
