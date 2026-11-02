// Lints the committed Keycloak realm templates: valid JSON and no plain passwords or secrets.
import { readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { plainSecrets } from './render-realms.mjs';

const dir = resolve(dirname(fileURLToPath(import.meta.url)), '..', 'infra', 'keycloak');
let failed = false;
for (const name of readdirSync(dir).filter((n) => n.endsWith('.template.json'))) {
  try {
    const plain = plainSecrets(JSON.parse(readFileSync(join(dir, name), 'utf8')));
    if (plain.length > 0) throw new Error(`plain secret at ${plain.join(', ')}`);
  } catch (error) {
    console.error(`${name}: ${error.message}`);
    failed = true;
  }
}
process.exit(failed ? 1 : 0);
