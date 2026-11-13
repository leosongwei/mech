// Parse every module in `src` without running it.
//
// A plain `node --check file.js` reads the file as CommonJS and dies on the first
// `import`, and webpack is the only other thing that parses the client - which
// means a syntax error is found three seconds into a production build instead of
// immediately.  `vm.SourceTextModule` compiles module syntax and touches nothing:
//
//   node --experimental-vm-modules scripts/syntax.mjs
//
// `npm run check:syntax` wraps the flag.  It is a pre-flight, not a test: the only
// thing it proves is that every file in `src` is a module that parses.

import { readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../src');

function walk(dir, out = []) {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    if (statSync(p).isDirectory()) walk(p, out);
    else if (name.endsWith('.js')) out.push(p);
  }
  return out;
}

const files = walk(root);
const bad = [];
for (const f of files) {
  const rel = relative(root, f).split(sep).join('/');
  try {
    new vm.SourceTextModule(readFileSync(f, 'utf8'), { identifier: rel });
  } catch (err) {
    bad.push(`${rel}: ${err.message}`);
  }
}

for (const b of bad) console.error(b);
console.log(`syntax: ${files.length} files, ${bad.length} bad`);
process.exit(bad.length ? 1 : 0);
