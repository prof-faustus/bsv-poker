/**
 * CI pipeline (core §16, app §A14.2, REQ-BUILD-003). Stages run in order; a red stage blocks
 * merge. Stages: typecheck → lint (OP_RETURN absence) → unit+property+interpreter tests →
 * reproduce → traceability → Go vet+test. The E2E-in-image and accessibility/security stages
 * are wired by the VM bootstrap (vm/) and later phases.
 *
 * Run: `node tools/ci.ts`. Exits non-zero on the first failing stage.
 */

import { spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { join } from 'node:path';

const ROOT = process.cwd();

interface Stage {
  name: string;
  cmd: string;
  args: string[];
  cwd?: string;
  skipIf?: () => boolean;
}

const stages: Stage[] = [
  { name: 'typecheck (tsc --strict)', cmd: 'node', args: ['node_modules/typescript/bin/tsc', '-p', 'tsconfig.json', '--noEmit'] },
  { name: 'lint: OP_RETURN absence', cmd: 'node', args: ['tools/lint-opreturn.ts'] },
  // Security lint: bans Math.random in source (CWE-338) and enforces the doc trio on reference-
  // standard targets — keeps the standard self-policing as surfaces are converted.
  { name: 'lint: security (randomness + doc presence)', cmd: 'node', args: ['tools/lint-security.ts'] },
  // The TS unit/property suite includes the parser/primitive FUZZ tests (hex, JSON, DER, ByteReader,
  // parseTxWire, interpreter) — 200k+ iterations each — so active fuzzing of the TS surface is part
  // of every CI run, not a separate job.
  { name: 'tests (unit+property+interpreter+fuzz)', cmd: 'node', args: ['--test', 'packages/*/test/**/*.test.ts', 'tests/**/*.test.ts'] },
  { name: 'reproduce (vectors)', cmd: 'node', args: ['tools/reproduce.ts'] },
  { name: 'traceability', cmd: 'node', args: ['tools/traceability.ts'] },
  {
    // The web client is its own Vite build (excluded from the root tsconfig); build it in CI so a
    // broken client bundle fails the pipeline (audit 8).
    name: 'web client build (vite)',
    cmd: 'node',
    args: ['node_modules/vite/bin/vite.js', 'build'],
    cwd: join(ROOT, 'apps/client-web'),
    // Missing Vite FAILS CI (audit 10) — a misinstalled env must not report green without building
    // the web client. Skip only under an explicit local-dev flag.
    skipIf: () => process.env.BSV_CI_SKIP_WEB === '1',
  },
  {
    name: 'go vet+test relay-go',
    cmd: 'go',
    args: ['test', './...'],
    cwd: join(ROOT, 'apps/relay-go'),
    skipIf: () => !hasGo(),
  },
  {
    name: 'go vet+test indexer-go',
    cmd: 'go',
    args: ['test', './...'],
    cwd: join(ROOT, 'apps/indexer-go'),
    skipIf: () => !hasGo(),
  },
  // Short ACTIVE fuzzing of the two security-critical Go boundaries (capability-token verify and
  // validated-envelope ingest). `go test` already replays each fuzz seed corpus; this stage adds a
  // brief live fuzz so new crashers surface in CI. Kept short to bound CI time.
  {
    name: 'go fuzz: relay capability verify (8s)',
    cmd: 'go',
    args: ['test', './relay/', '-run=^$', '-fuzz=FuzzCapabilityVerify', '-fuzztime=8s'],
    cwd: join(ROOT, 'apps/relay-go'),
    skipIf: () => !hasGo(),
  },
  {
    name: 'go fuzz: indexer validate (8s)',
    cmd: 'go',
    args: ['test', './indexer/', '-run=^$', '-fuzz=FuzzValidateEnvelopeRecord', '-fuzztime=8s'],
    cwd: join(ROOT, 'apps/indexer-go'),
    skipIf: () => !hasGo(),
  },
];

function hasGo(): boolean {
  const r = spawnSync('go', ['version'], { stdio: 'ignore' });
  return r.status === 0;
}

function main(): void {
  // requirements.yaml must exist for traceability; regenerate it first (idempotent).
  if (!existsSync(join(ROOT, 'spec/requirements.yaml'))) {
    spawnSync('node', ['tools/extract-requirements.ts'], { cwd: ROOT, stdio: 'inherit' });
  }
  let failed = false;
  for (const stage of stages) {
    if (stage.skipIf?.()) {
      console.log(`\n=== SKIP: ${stage.name} (precondition not met) ===`);
      continue;
    }
    console.log(`\n=== ${stage.name} ===`);
    const r = spawnSync(stage.cmd, stage.args, {
      cwd: stage.cwd ?? ROOT,
      stdio: 'inherit',
      shell: false,
    });
    if (r.status !== 0) {
      console.error(`STAGE FAILED: ${stage.name} (exit ${r.status})`);
      failed = true;
      break; // a red stage blocks the pipeline
    }
  }
  if (failed) {
    console.error('\nCI FAILED.');
    process.exit(1);
  }
  console.log('\nCI GREEN — all stages passed.');
}

main();
