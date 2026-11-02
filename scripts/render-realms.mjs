// Renders infra/keycloak/*.template.json to infra/keycloak/generated/*.json,
// replacing ${VAR} placeholders with values from .env (or the process environment).
// Fails without writing anything when a variable is missing or empty.
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const placeholder = /\$\{([A-Za-z0-9_]+)\}/g;
const onlyPlaceholder = /^\$\{[A-Za-z0-9_]+\}$/;

export function parseEnv(text) {
  const vars = {};
  for (const line of text.split(/\r?\n/)) {
    const match = /^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$/.exec(line);
    if (!match) continue;
    let value = match[2];
    if (/^(".*"|'.*')$/.test(value)) value = value.slice(1, -1);
    else value = value.replace(/(^|\s)#.*$/, '').trim(); // inline comment, as docker compose does
    vars[match[1]] = value;
  }
  return vars;
}

export function render(template, vars) {
  const missing = new Set();
  const out = template.replace(placeholder, (_, name) => {
    if (!vars[name]) {
      missing.add(name);
      return '';
    }
    // Escape for use inside a JSON string.
    return JSON.stringify(vars[name]).slice(1, -1);
  });
  if (missing.size > 0) {
    throw new Error(`Missing values in .env: ${[...missing].sort().join(', ')}. Copy .env.example to .env and fill them in.`);
  }
  return out;
}

// Returns the paths of credential values that are not a ${VAR} placeholder.
export function plainSecrets(node, path = '$') {
  if (Array.isArray(node)) return node.flatMap((item, i) => plainSecrets(item, `${path}[${i}]`));
  if (node === null || typeof node !== 'object') return [];
  const found = [];
  const isPasswordCredential = node.type === 'password' && 'value' in node;
  for (const [key, value] of Object.entries(node)) {
    const isSensitive = (isPasswordCredential && key === 'value') || key === 'secret';
    if (isSensitive && !onlyPlaceholder.test(String(value))) found.push(`${path}.${key}`);
    else found.push(...plainSecrets(value, `${path}.${key}`));
  }
  return found;
}

export function renderAll(envFile = join(root, '.env')) {
  if (!existsSync(envFile)) throw new Error(`${envFile} not found. Copy .env.example to .env and fill it in.`);
  const vars = { ...parseEnv(readFileSync(envFile, 'utf8')) };
  for (const [key, value] of Object.entries(process.env)) if (value && !vars[key]) vars[key] = value;
  const dir = join(root, 'infra', 'keycloak');
  const outputs = readdirSync(dir)
    .filter((name) => name.endsWith('.template.json'))
    .map((name) => [name.replace('.template.json', '.json'), render(readFileSync(join(dir, name), 'utf8'), vars)]);
  mkdirSync(join(dir, 'generated'), { recursive: true });
  for (const [name, content] of outputs) writeFileSync(join(dir, 'generated', name), content);
  return outputs.map(([name]) => name);
}

if (process.argv[1] && basename(process.argv[1]) === 'render-realms.mjs') {
  try {
    const files = renderAll(process.argv[2]);
    console.log(`Rendered ${files.length} realm file(s) to infra/keycloak/generated/`);
  } catch (error) {
    console.error(error.message);
    process.exit(1);
  }
}
