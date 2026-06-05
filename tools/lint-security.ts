/**
 * Security lint — a CI gate that keeps the reference-infrastructure standard self-enforcing.
 *
 * WHAT it checks (each failure blocks the pipeline):
 *   1. BANNED RANDOMNESS: no `Math.random(` in any package/app SOURCE file. Security values must use
 *      the CSPRNG (`cryptoRandomBytes`/`randomId`); `Math.random` is predictable (CWE-338). Comments
 *      are ignored; an explicit `// lint-security: allow-random` marker on the line opts a specific
 *      non-security use out (none currently).
 *   2. DOC PRESENCE: every package/app declared "at standard" (REFERENCE_DOC_TARGETS) must carry the
 *      full doc trio (README.md + SECURITY.md + INVARIANTS.md). The list GROWS as surfaces are
 *      converted; a converted surface can never silently lose its docs.
 *
 * WHY a custom lint rather than eslint: this repo is dependency-light (no eslint), runs TS directly
 * on Node 24, and these two rules are precise and cheap to check by hand. Keeping the gate in-tree
 * means an auditor can read exactly what is enforced.
 *
 * Run: `node tools/lint-security.ts` (exits non-zero on any violation).
 */

import { readdirSync, readFileSync, statSync, existsSync } from 'node:fs';
import { join } from 'node:path';

const ROOT = process.cwd();

/** Packages/apps that have been brought to the reference standard and MUST keep the doc trio. */
const REFERENCE_DOC_TARGETS: string[] = [
  'packages/protocol-types',
  'packages/tx-builder',
  'packages/script-templates-ts',
  'apps/indexer-go',
];

/** Doc files required for a reference-standard target. */
const REQUIRED_DOCS = ['README.md', 'SECURITY.md', 'INVARIANTS.md'];

/** Source roots scanned for banned randomness (SOURCE only — tests may use seeded PRNGs). */
const SRC_GLOBS = [
  'packages', // each packages/<name>/src
  'apps/client-web/src',
  'apps/client-desktop/src',
];

let failures = 0;
function fail(msg: string): void {
  console.error(`  ✗ ${msg}`);
  failures++;
}

/** Recursively collect *.ts/*.tsx files under dir, excluding test/ and node_modules/. */
function tsFiles(dir: string): string[] {
  const out: string[] = [];
  if (!existsSync(dir)) return out;
  // Bounded recursion is acceptable here (filesystem depth is small and developer-controlled), but
  // we guard against runaway depth defensively.
  const stack: { d: string; depth: number }[] = [{ d: dir, depth: 0 }];
  while (stack.length > 0) {
    const top = stack.pop()!;
    if (top.depth > 32) continue;
    for (const ent of readdirSync(top.d)) {
      if (ent === 'node_modules' || ent === 'test' || ent === 'dist') continue;
      const p = join(top.d, ent);
      const st = statSync(p);
      if (st.isDirectory()) stack.push({ d: p, depth: top.depth + 1 });
      else if (ent.endsWith('.ts') || ent.endsWith('.tsx')) out.push(p);
    }
  }
  return out;
}

/** Source files for the randomness scan: packages/<name>/src plus the explicit app src roots. */
function securitySourceFiles(): string[] {
  const files: string[] = [];
  const pkgRoot = join(ROOT, 'packages');
  if (existsSync(pkgRoot)) {
    for (const pkg of readdirSync(pkgRoot)) {
      files.push(...tsFiles(join(pkgRoot, pkg, 'src')));
    }
  }
  for (const g of SRC_GLOBS.slice(1)) files.push(...tsFiles(join(ROOT, g)));
  return files;
}

function checkBannedRandomness(): void {
  console.log('• banned randomness (no Math.random in source)');
  const re = /Math\.random\s*\(/;
  for (const file of securitySourceFiles()) {
    const lines = readFileSync(file, 'utf8').split('\n');
    for (let i = 0; i < lines.length; i++) {
      const line = lines[i]!;
      const trimmed = line.trim();
      if (trimmed.startsWith('//') || trimmed.startsWith('*')) continue; // comment line
      if (line.includes('lint-security: allow-random')) continue; // explicit opt-out
      if (re.test(line)) fail(`${file.replace(ROOT + '\\', '').replace(ROOT + '/', '')}:${i + 1} uses Math.random (use cryptoRandomBytes/randomId — CWE-338)`);
    }
  }
}

function checkDocPresence(): void {
  console.log('• doc presence (reference-standard targets keep README+SECURITY+INVARIANTS)');
  for (const target of REFERENCE_DOC_TARGETS) {
    for (const doc of REQUIRED_DOCS) {
      const p = join(ROOT, target, doc);
      if (!existsSync(p)) fail(`${target}/${doc} is missing (a reference-standard target must keep its doc trio)`);
    }
  }
}

function main(): void {
  console.log('security lint:');
  checkBannedRandomness();
  checkDocPresence();
  if (failures > 0) {
    console.error(`\nsecurity lint FAILED with ${failures} violation(s).`);
    process.exit(1);
  }
  console.log('security lint OK.');
}

main();
