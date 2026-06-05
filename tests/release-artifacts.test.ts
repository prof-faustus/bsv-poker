/**
 * Build/packaging artifacts present in-repo (REQ-BUILD-002 locked lockfiles; REQ-VM-003 container
 * packaging). Asserts the locked dependency manifests and the container Dockerfiles + CI/CD
 * workflows exist. (Installer signing + recorded artifact hashes are release-pipeline concerns
 * tracked in RT-02 F3, not asserted here.)
 */

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');

test('dependency lockfiles are committed (locked deps — REQ-BUILD-002)', () => {
  assert.ok(existsSync(join(ROOT, 'pnpm-lock.yaml')), 'pnpm-lock.yaml committed');
  // The native desktop host (Win32 + WebView2; Tauri/Rust removed) has no Cargo.lock — its locked
  // build inputs are the in-tree build script and the vendored WebView2 ABI header + static loader.
  assert.ok(existsSync(join(ROOT, 'apps/client-desktop/native/build-native.ps1')), 'native desktop build script committed');
  assert.ok(existsSync(join(ROOT, 'apps/client-desktop/native/include/WebView2.h')), 'vendored WebView2 header committed');
  assert.ok(existsSync(join(ROOT, 'apps/client-desktop/native/lib/x64/WebView2LoaderStatic.lib')), 'vendored WebView2 loader committed');
});

test('container packaging exists: web image + the VM service images (REQ-VM-003)', () => {
  assert.ok(existsSync(join(ROOT, 'apps/client-web/Dockerfile')), 'web container');
  for (const d of ['Dockerfile.node', 'Dockerfile.relay', 'Dockerfile.indexer', 'Dockerfile.client']) {
    assert.ok(existsSync(join(ROOT, 'vm', d)), `vm/${d}`);
  }
});

test('CI/CD release workflows are present (REQ-VM-003 one-liner pipeline)', () => {
  const wf = readdirSync(join(ROOT, '.github', 'workflows'));
  assert.ok(wf.includes('publish-web.yml') && wf.includes('release-desktop.yml') && wf.includes('ci.yml'));
});
