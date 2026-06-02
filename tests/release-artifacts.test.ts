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
  assert.ok(existsSync(join(ROOT, 'apps/client-desktop/src-tauri/Cargo.lock')), 'Cargo.lock committed');
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
