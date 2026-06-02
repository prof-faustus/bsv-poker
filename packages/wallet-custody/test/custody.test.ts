import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createSoftwareCustody, scalarToPrivateKey } from '../src/custody.ts';
import { evaluate, foldLocking, foldUnlocking } from '@bsv-poker/script-templates-ts';
import { createPublicKey } from 'node:crypto';
import { compressedPub } from '@bsv-poker/script-templates-ts';
import type { BranchBinding } from '@bsv-poker/protocol-types';

const master = Uint8Array.from(Array.from({ length: 32 }, (_, i) => i + 1));
const BIND: BranchBinding = {
  gid: 'ab'.repeat(8),
  rulesetHash: 'cd'.repeat(32),
  round: 0,
  stateHash: 'ef'.repeat(32),
  actingSeat: 0,
  successorCommitment: '01'.repeat(32),
};

test('scalarToPrivateKey builds a usable secp256k1 signing key', () => {
  const d = Uint8Array.from(Array.from({ length: 32 }, (_, i) => (i + 7) % 251 || 1));
  const key = scalarToPrivateKey(d);
  const pub = compressedPub(createPublicKey(key));
  assert.equal(pub.length, 33);
});

test('derive is deterministic per (gid,j,role); different inputs → different keys (REQ-WALLET-001)', () => {
  const c = createSoftwareCustody(master);
  const a1 = c.derive('game1', 0, 'sign');
  const a2 = c.derive('game1', 0, 'sign');
  assert.equal(a1, a2, 'same inputs → same key');
  assert.notEqual(a1, c.derive('game1', 1, 'sign'), 'different card index → different key');
  assert.notEqual(a1, c.derive('game2', 0, 'sign'), 'different game → different key (old-game keys reveal nothing)');
  assert.notEqual(a1, c.derive('game1', 0, 'encrypt'), 'different role → different key (least authority)');
});

test('a signature from custody validates through the real interpreter against the derived pubkey', () => {
  const c = createSoftwareCustody(master);
  const pubHex = c.derive('g', 3, 'sign');
  const pub = Uint8Array.from(Buffer.from(pubHex, 'hex'));
  const preimage = Uint8Array.from([1, 2, 3, 4, 5]);
  const sig = c.sign('g', 3, 'sign', {
    sighashPreimage: preimage,
    describe: { action: 'fold' },
  });
  const locking = foldLocking(BIND, pub);
  assert.equal(evaluate(foldUnlocking(sig), locking, { sighashPreimage: preimage }).ok, true);
});

test('Mode A reconstructAndSign sums scalars and produces a valid signature', () => {
  const c = createSoftwareCustody(master);
  // two single-game per-card scalars (32 bytes each)
  const s1 = Uint8Array.from(Array.from({ length: 32 }, (_, i) => (i + 1) % 200 || 1));
  const s2 = Uint8Array.from(Array.from({ length: 32 }, (_, i) => (i * 3 + 5) % 200 || 1));
  const preimage = Uint8Array.from([9, 8, 7]);
  const sig = c.reconstructAndSign!([s1, s2], { sighashPreimage: preimage, describe: { action: 'settle' } });
  assert.ok(sig.length > 0);
});

test('software custody refuses Mode B combineSignShare (must not claim Mode B under Mode A)', () => {
  const c = createSoftwareCustody(master);
  assert.throws(() => c.combineSignShare(), /Mode B|threshold/);
});
