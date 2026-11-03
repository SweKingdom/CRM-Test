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
const TEST_USERS = ["ADMIN", "SALESPERSON", "BDR", "PROJECT_MANAGER", "CONSULTANT", "FINANCE", "MANAGEMENT"];
const PLACEHOLDER = "change-me";
const isCredentialKey = (key) => /PASSWORD|SECRET/.test(key);
const passwordSuffix = (value) => ["Password", value].join("=");

test("AC 1: compose defines the five services with healthchecks", () => {
  for (const service of ["db", "api", "web", "keycloak", "mailpit"]) {
    assert.match(compose, new RegExp(`^  ${service}:`, "m"), `${service} missing`);
  }
  assert.equal((compose.match(/healthcheck:/g) ?? []).length, 5);
  assert.match(read("src/backend/Zephyr.Api/Program.cs"), /MapHealthChecks\("\/health"/);
  assert.match(read("src/backend/Zephyr.Api/Program.cs"), /AddNpgSql/);
  assert.match(compose, /127\.0\.0\.1:8080:8080/);
});

test("AC 1: keycloak imports the rendered realms read-only", () => {
  assert.match(compose, /\.\/infra\/keycloak\/generated:\/opt\/keycloak\/data\/import:ro/);
  assert.match(compose, /--import-realm/);
  assert.match(read(".gitignore"), /^infra\/keycloak\/generated\/$/m);
});

test("AC 1: frontend image uses npm ci when a lockfile exists, npm install otherwise", () => {
  const dockerfile = read("src/frontend/Dockerfile");
  assert.match(dockerfile, /COPY package\.json package-lock\.json\* \.\//);
  assert.match(dockerfile, /if \[ -f package-lock\.json \]; then npm ci; else npm install; fi/);
});

test("AC 1: the API image targets the same .NET version as the project", () => {
  const framework = /<TargetFramework>net(\d+\.\d+)<\/TargetFramework>/.exec(read("src/backend/Zephyr.Api/Zephyr.Api.csproj"))[1];
  const dockerfile = read("src/backend/Zephyr.Api/Dockerfile");
  assert.match(dockerfile, new RegExp(`dotnet/sdk:${framework.replace(".", "\\.")} `));
  assert.match(dockerfile, new RegExp(`dotnet/aspnet:${framework.replace(".", "\\.")}\\s`));
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
  const credentialEntries = Object.entries(vars).filter(([k]) => isCredentialKey(k) && k !== "ZEPHYR_DB");
  assert.ok(credentialEntries.length >= 10);
  for (const [key, value] of credentialEntries) assert.equal(value, PLACEHOLDER, key);
  assert.ok(vars.ZEPHYR_DB.endsWith(passwordSuffix(PLACEHOLDER)));
});

test("AC 3: .env.example has one KC_USER_<ROLE>_PASSWORD per role, plus the realm-b user, and no retired names", () => {
  const names = Object.keys(parseEnv(example)).filter((k) => /^KC_USER_/.test(k) && k !== "KC_USER_B_PASSWORD");
  assert.deepEqual(names.sort(), TEST_USERS.map((r) => `KC_USER_${r}_PASSWORD`).sort());
  assert.doesNotMatch(example, /SALES_REP|SALES_MANAGER|APPROVER|READONLY/);
});

test("AC 3: every variable compose interpolates is listed in .env.example", () => {
  const vars = parseEnv(example);
  for (const [, name] of compose.matchAll(/(?<!\$)\$\{(\w+)(?::\?[^}]*)?\}/g)) {
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

test("setup-env: generates random values and keeps the DB credential in sync", () => {
  let n = 0;
  const fakeValue = () => `fake-value-${++n}`;
  const { text, generated } = renderEnv(example, fakeValue);
  const vars = parseEnv(text);
  // Active variables only: the commented second-realm block (US-140) is left as a template.
  assert.ok(!Object.entries(vars).some(([key, value]) => isCredentialKey(key) && value === PLACEHOLDER));
  assert.equal(vars.POSTGRES_PASSWORD, "fake-value-1");
  assert.ok(vars.ZEPHYR_DB.endsWith(passwordSuffix(vars.POSTGRES_PASSWORD)));
  assert.equal(vars.ZEPHYR_BOOTSTRAP_ADMIN_SUBJECT, parseEnv(example).ZEPHYR_BOOTSTRAP_ADMIN_SUBJECT);
  const kc = TEST_USERS.map((r) => vars[`KC_USER_${r}_PASSWORD`]);
  assert.ok(kc.every(Boolean));
  assert.equal(new Set(kc).size, 7);
  assert.ok(generated.includes("KC_BOOTSTRAP_ADMIN_PASSWORD"));
  assert.ok(generated.includes("KC_USER_B_PASSWORD"));
  for (const r of TEST_USERS) assert.ok(generated.includes(`KC_USER_${r}_PASSWORD`), r);
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
