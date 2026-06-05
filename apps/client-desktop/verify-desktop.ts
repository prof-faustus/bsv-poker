/**
 * Desktop render verification — proves the NATIVE Win32 + WebView2 host actually renders the audited
 * web client (not merely that a process starts).
 *
 * WHAT: runs build/bsv-poker.exe in --selftest mode, which creates the WebView2, maps + navigates to
 * the locally-built client (apps/client-web/dist/esm), reads `#root.innerText` back out of the live
 * DOM, and writes it to a file. We assert the lobby's landmark text is present — the SAME render bar
 * the web client meets (verify-render.ts), enforced for the native shell.
 *
 * HOW: a GUI-subsystem exe has no stdout, so the self-test communicates via the --out file; we wait
 * for the process to exit (exit 0 = rendered + extracted; 2 = navigation watchdog; other = init
 * failure) and read the file.
 *
 * Skips (exit 0) when the native exe has not been built on this host (build needs MSVC); the desktop
 * build stage gates that separately. On a Windows host with the exe + WebView2 runtime it runs.
 *
 * Run: `node verify-desktop.ts` (from apps/client-desktop).
 */
import { spawn } from 'node:child_process';
import { readFile, rm } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const APP_DIR = dirname(fileURLToPath(import.meta.url));
const EXE = join(APP_DIR, 'build', 'bsv-poker.exe');
const CONTENT = join(APP_DIR, '..', 'client-web', 'dist', 'esm');

const REQUIRED_MARKERS = ['Lobby', 'REGTEST', 'Create a table', 'Wallet balance', 'Open tables'];

async function main(): Promise<void> {
  if (process.platform !== 'win32') {
    console.log('verify-desktop: SKIP — native Win32 host is Windows-only.');
    process.exit(0);
  }
  if (!existsSync(EXE)) {
    console.log(`verify-desktop: SKIP — native exe not built (${EXE}); run native/build-native.ps1.`);
    process.exit(0);
  }
  if (!existsSync(join(CONTENT, 'index.html'))) {
    console.error(`verify-desktop: FAILED — web client not built at ${CONTENT} (run client-web build.ts).`);
    process.exit(1);
  }

  const out = join(tmpdir(), `bsv-desktop-render-${process.pid}.txt`);
  await rm(out, { force: true }).catch(() => {});

  const code = await new Promise<number>((resolve, reject) => {
    const child = spawn(EXE, ['--selftest', '--out', out, '--content', CONTENT], { stdio: 'ignore' });
    const timer = setTimeout(() => { child.kill(); reject(new Error('native host timed out (60s)')); }, 60_000);
    child.on('error', (e) => { clearTimeout(timer); reject(e); });
    child.on('close', (c) => { clearTimeout(timer); resolve(c ?? -1); });
  }).catch((e: unknown) => { console.error(`verify-desktop: ${(e as Error).message}`); return -1; });

  if (code !== 0) {
    console.error(`verify-desktop: FAILED — host exited ${code} (2=nav watchdog, 4-7=WebView2 init).`);
    process.exit(1);
  }

  const dom = existsSync(out) ? await readFile(out, 'utf8') : '';
  await rm(out, { force: true }).catch(() => {});
  const missing = REQUIRED_MARKERS.filter((m) => !dom.includes(m));
  if (missing.length > 0) {
    console.error(`verify-desktop: FAILED — rendered DOM missing markers: ${missing.join(', ')}`);
    console.error(`--- extracted (first 1500 chars) ---\n${dom.slice(0, 1500)}`);
    process.exit(1);
  }
  console.log(`verify-desktop: OK — native WebView2 host rendered the lobby (all ${REQUIRED_MARKERS.length} markers, ${dom.length} chars).`);
  process.exit(0);
}

void main();
