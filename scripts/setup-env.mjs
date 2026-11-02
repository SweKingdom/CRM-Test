// Creates .env from .env.example with random passwords and renders the Keycloak realm files.
// Never overwrites an existing .env. Usage: node scripts/setup-env.mjs
import { randomBytes } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { renderAll } from "./render-realms.mjs";

const PLACEHOLDER = "change-me";
const isSecretKey = (key) => /PASSWORD|SECRET/.test(key);

export const randomPassword = () => randomBytes(18).toString("base64url");

/** Replaces every `change-me` secret with a random value. Returns the new text and the keys generated. */
export function renderEnv(template, random = randomPassword) {
  const generated = [];
  let dbPassword = random();
  const lines = template.split(/\r?\n/).map((line) => {
    const match = /^([A-Za-z0-9_]+)=(.*)$/.exec(line);
    if (!match) return line;
    const [, key, value] = match;
    if (key === "POSTGRES_PASSWORD" && value === PLACEHOLDER) {
      generated.push(key);
      return `${key}=${dbPassword}`;
    }
    if (key === "ZEPHYR_DB") {
      return `${key}=${value.replace(`Password=${PLACEHOLDER}`, `Password=${dbPassword}`)}`;
    }
    if (isSecretKey(key) && value === PLACEHOLDER) {
      generated.push(key);
      return `${key}=${random()}`;
    }
    return line;
  });
  return { text: lines.join("\n"), generated };
}

/** Creates <root>/.env from <root>/.env.example. Returns null when .env already exists. */
export function setupEnv(root) {
  const { text, generated } = renderEnv(readFileSync(join(root, ".env.example"), "utf8"));
  try {
    writeFileSync(join(root, ".env"), text, { flag: "wx", mode: 0o600 });
  } catch (error) {
    if (error.code === "EEXIST") return null;
    throw error;
  }
  return generated;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const root = join(dirname(fileURLToPath(import.meta.url)), "..");
  const generated = setupEnv(root);
  if (generated === null) {
    console.log(".env already exists; left unchanged.");
  } else {
    console.log(`Created .env with ${generated.length} generated passwords.`);
    console.log("Find the test-user passwords (KC_USER_*_PASSWORD) and admin passwords in .env. Do not commit it.");
  }
  try {
    const files = renderAll(join(root, ".env"));
    console.log(`Rendered ${files.length} Keycloak realm file(s) to infra/keycloak/generated/.`);
  } catch (error) {
    console.error(error.message);
    process.exit(1);
  }
}
