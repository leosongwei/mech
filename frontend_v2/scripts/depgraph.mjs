// The client's import graph, checked instead of trusted.
//
// `frontend_v2/src` is four layers and one direction:
//
//   core/   (0)  pure: constants, maths, the fire control law
//   domain  (1)  net/ map/ audio/ render/ input/    - no view, no app
//   view    (2)  scene/ hud/ fx/                    - no app
//   app     (3)  game/ frame/ systems/ panels/      - everything
//
// An edge may point at its own layer or a lower one.  Inside the domain layer
// the only cross-module edge the client has is `render -> map` (a terrain pass
// reads the heightfield's tiles); everything else stays inside its own module.
// An edge that closes a cycle is an error whatever the layers say.
//
// `node scripts/depgraph.mjs` prints the graph's shape and exits 1 on a
// violation, so the rule fails the build (`npm run build`) instead of a review.
// No dependencies: a regex over `import ... from '...'` is enough here, and a
// parser would be the only third-party thing in the client.

import { readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../src');

const LAYER = {
  core: 0,
  net: 1, map: 1, audio: 1, render: 1, input: 1,
  scene: 2, hud: 2, fx: 2,
  app: 3,
};

// the one domain edge the client is allowed to have (a pass reads the map)
const DOMAIN_EDGE = new Set(['render->map']);

function walk(dir, out = []) {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    if (statSync(p).isDirectory()) walk(p, out);
    else if (name.endsWith('.js')) out.push(p);
  }
  return out;
}

// the module a path belongs to: its first directory under src/, or the file
// itself at the top
function moduleOf(abs) {
  const rel = relative(root, abs).split(sep);
  return rel.length > 1 ? rel[0] : null;
}

// The three files that sit at the top of `src` are their own modules with no
// directory to carry the layer: `icons.js` and `shop.js` are leaves (an icon
// glyph table, a pricing law) and `main.js` is the bootstrap.
const FILE_LAYER = { 'icons.js': 0, 'shop.js': 0, 'main.js': 4 };

function layerOf(abs) {
  const m = moduleOf(abs);
  if (m === null) return FILE_LAYER[relative(root, abs)] ?? 3;
  return m in LAYER ? LAYER[m] : 3;
}

const files = walk(root);
const edges = new Map();   // abs -> [abs]
const specRe = /(?:^|\n)\s*(?:import|export)[\s\S]*?from\s*['"]([^'"]+)['"]/g;

for (const f of files) {
  const src = readFileSync(f, 'utf8');
  const deps = [];
  for (const m of src.matchAll(specRe)) {
    const spec = m[1];
    if (!spec.startsWith('.')) continue;          // assets (`icons.json`, `.woff2`)
    if (!spec.endsWith('.js')) continue;          // ditto
    deps.push(resolve(dirname(f), spec));
  }
  edges.set(f, deps);
}

const problems = [];
const rel = (p) => relative(root, p) || 'main.js';

// 1. every edge must exist
for (const [f, deps] of edges) {
  for (const d of deps) {
    if (!files.includes(d)) problems.push(`${rel(f)}: imports a file that is not there: ${rel(d)}`);
  }
}

// 2. direction
for (const [f, deps] of edges) {
  for (const d of deps) {
    const a = layerOf(f), b = layerOf(d);
    if (b > a) {
      problems.push(`${rel(f)} (layer ${a}) imports ${rel(d)} (layer ${b}): an edge only points down`);
      continue;
    }
    if (a === 1 && b === 1) {
      const key = `${moduleOf(f)}->${moduleOf(d)}`;
      if (moduleOf(f) !== moduleOf(d) && !DOMAIN_EDGE.has(key)) {
        problems.push(`${rel(f)}: domain module edge ${key} is not allowed`);
      }
    }
  }
}

// 3. cycles (iterative DFS with a colour map)
const colour = new Map();   // 0 unvisited, 1 on stack, 2 done
const stack = [];
function dfs(f) {
  colour.set(f, 1);
  stack.push(f);
  for (const d of edges.get(f) || []) {
    const c = colour.get(d) || 0;
    if (c === 1) {
      const at = stack.indexOf(d);
      problems.push(`cycle: ${[...stack.slice(at), d].map(rel).join(' -> ')}`);
    } else if (c === 0) dfs(d);
  }
  stack.pop();
  colour.set(f, 2);
}
for (const f of files) if (!colour.get(f)) dfs(f);

// the shape, for a human: one line per module with its file and edge count
const perModule = new Map();
for (const f of files) {
  const m = moduleOf(f) || rel(f);
  const row = perModule.get(m) || { files: 0, lines: 0 };
  row.files += 1;
  row.lines += readFileSync(f, 'utf8').split('\n').length;
  perModule.set(m, row);
}
const order = Object.keys(LAYER).concat(['icons.js', 'shop.js', 'main.js']);
for (const m of order) {
  const row = perModule.get(m);
  if (!row) continue;
  const layer = m in LAYER ? LAYER[m] : FILE_LAYER[m] ?? 3;
  console.log(`${String(layer)}  ${m.padEnd(8)} ${String(row.files).padStart(3)} files ${String(row.lines).padStart(6)} lines`);
}

if (problems.length) {
  console.error('\ndepgraph: ' + problems.length + ' problem(s)');
  for (const p of problems) console.error('  ' + p);
  process.exit(1);
}
console.log(`\ndepgraph: ${files.length} files, ${[...edges.values()].reduce((n, d) => n + d.length, 0)} edges, no cycle, layers clean`);
