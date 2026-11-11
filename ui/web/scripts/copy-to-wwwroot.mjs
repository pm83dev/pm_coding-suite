// Copia la build Angular (dist/web/browser) in packages/agent-backend/wwwroot,
// rimuovendo prima i vecchi bundle con hash (main-*.js, styles-*.css, ecc.).
import { cpSync, existsSync, mkdirSync, readdirSync, rmSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const src = join(root, 'dist', 'web', 'browser');
const dest = resolve(root, '..', '..', 'packages', 'agent-backend', 'wwwroot');

if (!existsSync(src)) {
  console.error(`[copy-to-wwwroot] Build non trovata: ${src}`);
  process.exit(1);
}
mkdirSync(dest, { recursive: true });
for (const f of readdirSync(dest)) rmSync(join(dest, f), { recursive: true, force: true });
cpSync(src, dest, { recursive: true });
console.log(`[copy-to-wwwroot] ${src} -> ${dest}`);
