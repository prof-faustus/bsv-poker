import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  OP,
  evaluate,
  serializeScript,
  scriptSizeBytes,
  containsOpReturn,
  genKeyPair,
  signPreimage,
  bindingBytes,
  branchBindingPrefix,
  fundingLocking,
  fundingUnlocking,
  revealOrTimeoutLocking,
  revealUnlocking,
  timeoutRefundUnlocking,
  foldLocking,
  foldUnlocking,
  settlementLocking,
  settlementUnlocking,
  revealCommitment,
  revealPreimage,
  type Script,
} from '../src/index.ts';
import type { BranchBinding } from '@bsv-poker/protocol-types';

const BIND: BranchBinding = {
  gid: 'aa'.repeat(8),
  rulesetHash: 'bb'.repeat(32),
  round: 4,
  stateHash: 'cc'.repeat(32),
  actingSeat: 1,
  successorCommitment: 'dd'.repeat(32),
};

// A fixed sighash preimage stands in for the tx sighash in these interpreter-level tests.
const SIGHASH = Uint8Array.from([0xde, 0xad, 0xbe, 0xef, 1, 2, 3, 4]);
const ctx = { sighashPreimage: SIGHASH };

test('fold: valid signature spend is ACCEPTED by the interpreter (positive, P9)', () => {
  const k = genKeyPair();
  const locking = foldLocking(BIND, k.pubCompressed);
  const unlocking = foldUnlocking(signPreimage(SIGHASH, k.priv));
  assert.equal(evaluate(unlocking, locking, ctx).ok, true);
});

test('fold: wrong key fails INSIDE the interpreter (negative, P9 — not a wrapper guard)', () => {
  const k = genKeyPair();
  const wrong = genKeyPair();
  const locking = foldLocking(BIND, k.pubCompressed);
  const unlocking = foldUnlocking(signPreimage(SIGHASH, wrong.priv));
  const r = evaluate(unlocking, locking, ctx);
  assert.equal(r.ok, false);
});

test('fold: tampered sighash fails inside the interpreter', () => {
  const k = genKeyPair();
  const locking = foldLocking(BIND, k.pubCompressed);
  const unlocking = foldUnlocking(signPreimage(SIGHASH, k.priv));
  const tampered = { sighashPreimage: Uint8Array.from([9, 9, 9, 9]) };
  assert.equal(evaluate(unlocking, locking, tampered).ok, false);
});

test('funding N-of-N multisig: full set of signatures accepted; missing one rejected', () => {
  const ks = [genKeyPair(), genKeyPair()];
  const locking = fundingLocking(BIND, ks.map((k) => k.pubCompressed));
  const sigs = ks.map((k) => signPreimage(SIGHASH, k.priv));
  assert.equal(evaluate(fundingUnlocking(sigs), locking, ctx).ok, true);
  // only one signature for a 2-of-2 → fails inside CHECKMULTISIG
  assert.equal(evaluate(fundingUnlocking([sigs[0]!]), locking, ctx).ok, false);
});

test('reveal-or-timeout: correct opening spends the reveal branch; wrong preimage fails inside', () => {
  const reveal = genKeyPair();
  const refund = genKeyPair();
  const blind = Uint8Array.from([7, 7, 7, 7]);
  const face = 42;
  const cmt = revealCommitment(face, blind);
  const locking = revealOrTimeoutLocking(BIND, cmt, reveal.pubCompressed, refund.pubCompressed);

  // positive: valid opening + reveal-key signature
  const good = revealUnlocking(signPreimage(SIGHASH, reveal.priv), revealPreimage(face, blind));
  assert.equal(evaluate(good, locking, ctx).ok, true);

  // negative: wrong preimage → OP_EQUALVERIFY fails inside the interpreter
  const badPre = revealUnlocking(signPreimage(SIGHASH, reveal.priv), revealPreimage(43, blind));
  assert.equal(evaluate(badPre, locking, ctx).ok, false);

  // timeout/refund branch: refund key signs the ELSE branch (maturity enforced at tx level)
  const refundSpend = timeoutRefundUnlocking(signPreimage(SIGHASH, refund.priv));
  assert.equal(evaluate(refundSpend, locking, ctx).ok, true);
  // refund branch with the reveal key (wrong) fails
  const badRefund = timeoutRefundUnlocking(signPreimage(SIGHASH, reveal.priv));
  assert.equal(evaluate(badRefund, locking, ctx).ok, false);
});

test('fair-play: committed key claims; a mismatched key fails INSIDE the interpreter (REQ-CRYPTO-006)', async () => {
  const { fairPlayCommitment, fairPlayLocking, fairPlayClaimUnlocking, fairPlayForfeitUnlocking } =
    await import('../src/templates.ts');
  const honest = genKeyPair();
  const cheat = genKeyPair();
  const refund = genKeyPair();
  const commitment = fairPlayCommitment(honest.pubCompressed);
  const locking = fairPlayLocking(BIND, commitment, refund.pubCompressed);

  // honest party reveals the committed key + a valid signature → claims
  const ok = fairPlayClaimUnlocking(signPreimage(SIGHASH, honest.priv), honest.pubCompressed);
  assert.equal(evaluate(ok, locking, ctx).ok, true);

  // a party who USED a different key cannot match HASH160(commitment) → fails inside (forfeits)
  const bad = fairPlayClaimUnlocking(signPreimage(SIGHASH, cheat.priv), cheat.pubCompressed);
  assert.equal(evaluate(bad, locking, ctx).ok, false);

  // forfeit/refund branch (maturity at tx level) pays the refund key
  const forfeit = fairPlayForfeitUnlocking(signPreimage(SIGHASH, refund.priv));
  assert.equal(evaluate(forfeit, locking, ctx).ok, true);
});

test('settlement: winner signature accepted', () => {
  const w = genKeyPair();
  const locking = settlementLocking(BIND, w.pubCompressed);
  assert.equal(evaluate(settlementUnlocking(signPreimage(SIGHASH, w.priv)), locking, ctx).ok, true);
});

test('OP_RETURN is banned: serialize throws, lint detects, interpreter fails', () => {
  const bad: Script = [Uint8Array.from([1, 2, 3]), OP.OP_RETURN];
  assert.equal(containsOpReturn(bad), true);
  assert.throws(() => serializeScript(bad), /OP_RETURN/);
  // even if it reached the interpreter, it fails inside it
  assert.equal(evaluate([], [OP.OP_1, OP.OP_RETURN], ctx).ok, false);
});

test('no template produces an OP_RETURN in its script (rule 2)', () => {
  const k = genKeyPair();
  const templates: Script[] = [
    branchBindingPrefix(BIND),
    fundingLocking(BIND, [k.pubCompressed]),
    revealOrTimeoutLocking(BIND, revealCommitment(1, Uint8Array.of(0)), k.pubCompressed, k.pubCompressed),
    foldLocking(BIND, k.pubCompressed),
    settlementLocking(BIND, k.pubCompressed),
  ];
  for (const t of templates) assert.equal(containsOpReturn(t), false);
});

test('CLTV/CSV are NO-OPS post-Genesis (REQ-TX-001): they enforce nothing', () => {
  const k = genKeyPair();
  // A script with a leading CLTV/CSV still validates purely on the signature.
  const locking: Script = [OP.OP_CHECKLOCKTIMEVERIFY, OP.OP_CHECKSEQUENCEVERIFY, k.pubCompressed, OP.OP_CHECKSIG];
  assert.equal(evaluate(foldUnlocking(signPreimage(SIGHASH, k.priv)), locking, ctx).ok, true);
});

test('byte-size measurement (REQ-TX-011 / §19.C): sizes are computed, not asserted from memory', () => {
  const k = genKeyPair();
  const sizes = {
    binding: scriptSizeBytes(branchBindingPrefix(BIND)),
    fold: scriptSizeBytes(foldLocking(BIND, k.pubCompressed)),
    funding2of2: scriptSizeBytes(fundingLocking(BIND, [k.pubCompressed, genKeyPair().pubCompressed])),
    revealOrTimeout: scriptSizeBytes(
      revealOrTimeoutLocking(BIND, revealCommitment(1, Uint8Array.of(0)), k.pubCompressed, k.pubCompressed),
    ),
  };
  // binding prefix = push(133 bytes binding) + OP_DROP. gid8+rh32+round4+sh32+seat1+succ32 = 109
  assert.equal(bindingBytes(BIND).length, 8 + 32 + 4 + 32 + 1 + 32);
  for (const [, v] of Object.entries(sizes)) assert.ok(v > 0 && Number.isInteger(v));
});
