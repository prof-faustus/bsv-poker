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
    // The framework-free view layer (vanilla DOM, no React) is typechecked here with the DOM lib —
    // it is intentionally outside the Node-side root tsconfig. This is the in-tree replacement for
    // the React/Vite typecheck as the view layer migrates off the framework.
    name: 'typecheck: view layer (vanilla DOM, tsconfig.web)',
    cmd: 'node',
    args: ['node_modules/typescript/bin/tsc', '-p', 'tsconfig.web.json', '--noEmit'],
  },
  {
    // The web client is built by the IN-TREE builder (tsc emit + import map, NO bundler — Vite/React
    // are removed). A broken client emit fails the pipeline (audit 8): build.ts exits non-zero if the
    // entry module is not emitted.
    name: 'web client build (in-tree: tsc emit + import map)',
    cmd: 'node',
    args: ['build.ts'],
    cwd: join(ROOT, 'apps/client-web'),
  },
  {
    // Prove the built client actually RENDERS in a real DOM (not just "process alive"): load
    // dist/esm/index.html in headless Chrome via CDP and assert the lobby mounted. Skips only if no
    // headless browser is discoverable on this host (the build stage above still gates the bundle).
    name: 'web client render check (headless DOM)',
    cmd: 'node',
    args: ['verify-render.ts'],
    cwd: join(ROOT, 'apps/client-web'),
    skipIf: () => process.env.BSV_CI_SKIP_RENDER === '1',
  },
  {
    // Browser-executed tests for the in-tree DOM view core (dom.ts): el semantics, the no-innerHTML
    // XSS guarantee (positive + negative), event handlers, and mount() focus/caret preservation.
    name: 'web view-core DOM tests (headless)',
    cmd: 'node',
    args: ['verify-dom.ts'],
    cwd: join(ROOT, 'apps/client-web'),
    skipIf: () => process.env.BSV_CI_SKIP_RENDER === '1',
  },
  {
    // Render tests for the table-screen vanilla components (pokerTable / actionBar / signingModal /
    // showdown / settlement / timer) from fixture view-models — the part the lobby render does not
    // reach — plus a negative XSS case on a component-supplied label.
    name: 'web component render tests (headless)',
    cmd: 'node',
    args: ['verify-components.ts'],
    cwd: join(ROOT, 'apps/client-web'),
    skipIf: () => process.env.BSV_CI_SKIP_RENDER === '1',
  },
  {
    // Native Windows desktop host (Win32 + WebView2; Tauri/Rust removed). Compiles bsv-poker.exe +
    // test-lifecycle.exe with cl.exe. Skips on non-Windows / when MSVC Build Tools are absent.
    name: 'desktop native build (cl.exe)',
    cmd: 'pwsh',
    args: ['-NoProfile', '-File', 'native/build-native.ps1'],
    cwd: join(ROOT, 'apps/client-desktop'),
    skipIf: () => !hasMsvc(),
  },
  {
    // Pure lifecycle-policy unit tests (ordered start, bounded restart, network guard, ports, data
    // dir) — positive + negative. Skips when the exe was not built on this host.
    name: 'desktop lifecycle unit tests',
    cmd: join(ROOT, 'apps/client-desktop/build/test-lifecycle.exe'),
    args: [],
    cwd: join(ROOT, 'apps/client-desktop'),
    skipIf: () => !existsSync(join(ROOT, 'apps/client-desktop/build/test-lifecycle.exe')),
  },
  {
    // Prove the native host RENDERS the client in WebView2 (reads #root text from the live DOM).
    // verify-desktop.ts self-skips on non-Windows / missing exe / missing WebView2 runtime.
    name: 'desktop render check (WebView2)',
    cmd: 'node',
    args: ['verify-desktop.ts'],
    cwd: join(ROOT, 'apps/client-desktop'),
    skipIf: () => process.env.BSV_CI_SKIP_RENDER === '1',
  },
  // NOTE: the Go relay-go / indexer-go servers have been DELETED — bsv-poker is fully peer-to-peer
  // (no relay, no indexer, no central server). The serverless transport + lobby + multi-hand + recovery
  // paths are exercised by the P2P e2es below instead of the old Go server vet/test/fuzz stages.
  { name: 'p2p transport + serverless lobby e2e', cmd: 'node', args: ['tools/p2p-lobby-e2e.ts'] },
  { name: 'multiplayer e2e (peer-to-peer)', cmd: 'node', args: ['tools/multiplayer-e2e.ts'] },
  { name: 'multi-variant e2e (peer-to-peer)', cmd: 'node', args: ['tools/multi-e2e.ts'] },
  { name: 'lobby + seating e2e (peer-to-peer)', cmd: 'node', args: ['tools/lobby-e2e.ts'] },
  { name: 'continuous session e2e (peer-to-peer)', cmd: 'node', args: ['tools/session-e2e.ts'] },
  { name: 'reconnect-from-peer-transcript e2e (peer-to-peer)', cmd: 'node', args: ['tools/reconnect-e2e.ts'] },
  { name: 'validating-peer e2e (authenticate + legality, no server)', cmd: 'node', args: ['tools/validating-indexer-e2e.ts'] },
  { name: 'browser transport over own local node e2e (no server)', cmd: 'node', args: ['tools/browser-transport-e2e.ts'] },
  { name: 'on-chain nLockTime fund recovery e2e (in-tree node)', cmd: 'node', args: ['tools/onchain-nlocktime-recovery-e2e.ts'] },
  { name: 'on-chain settlement e2e (peer-to-peer co-sign, in-tree node)', cmd: 'node', args: ['tools/bot-onchain-e2e.ts'] },
];

/** True when the MSVC C++ Build Tools are discoverable (Windows + vswhere present). The native
 *  desktop build needs cl.exe; on other hosts the desktop stages skip. */
function hasMsvc(): boolean {
  if (process.platform !== 'win32') return false;
  const vswhere = `${process.env['ProgramFiles(x86)']}\\Microsoft Visual Studio\\Installer\\vswhere.exe`;
  return existsSync(vswhere);
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
