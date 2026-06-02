/**
 * Script template families (core §6.6), scaled from GB2616862's 2-party worked examples.
 * Every commitment/anchor is carried as PUSHDATA in a live script (`<data> OP_DROP`), NEVER
 * OP_RETURN (core P11/§6.5, REQ-TX-010). Timing is transaction-level (nLockTime/nSequence,
 * REQ-TX-002) — there is NO CLTV/CSV in any locking script (REQ-TX-001).
 *
 * Each template ships with a positive spend and a negative battery that fail INSIDE the
 * interpreter (REQ-TX-011, P9 — see test/templates.test.ts), plus a measurable wire-byte size
 * (scriptSizeBytes) recorded as a reproducible vector (§19.C).
 */

import { createHash } from 'node:crypto';
import { sha256, hexToBytes, type BranchBinding, ByteWriter } from '@bsv-poker/protocol-types';
import { OP } from './opcodes.ts';
import type { Script } from './script.ts';

function ripemd160(b: Uint8Array): Uint8Array {
  return new Uint8Array(createHash('ripemd160').update(Buffer.from(b)).digest());
}

/**
 * Canonical branch-binding bytes (core §6.3, REQ-TX-005). All fields are fixed-width so they
 * are written RAW (no length prefixes): gid(8) ‖ rulesetHash(32) ‖ round(u32) ‖ stateHash(32)
 * ‖ actingSeat(u8) ‖ successorCommitment(32) = 109 bytes.
 */
export function bindingBytes(b: BranchBinding): Uint8Array {
  const w = new ByteWriter();
  const raw = (hex: string): void => {
    for (const x of hexToBytes(hex)) w.u8(x);
  };
  raw(b.gid);
  raw(b.rulesetHash);
  w.u32(b.round);
  raw(b.stateHash);
  w.u8(b.actingSeat < 0 ? 0xff : b.actingSeat);
  raw(b.successorCommitment);
  return w.toBytes();
}

/** `<binding> OP_DROP` — anti-replay binding as pushdata in a LIVE script (never OP_RETURN). */
export function branchBindingPrefix(b: BranchBinding): Script {
  return [bindingBytes(b), OP.OP_DROP];
}

/** Funding: N-of-N multisig over player buy-ins; binds gid+rulesetHash (core §6.6). */
export function fundingLocking(b: BranchBinding, pubKeys: readonly Uint8Array[]): Script {
  const n = pubKeys.length;
  if (n < 1 || n > 3) throw new Error('Phase-1 funding supports 1..3 of N via small ints');
  const nOp = [OP.OP_1, OP.OP_2, OP.OP_3][n - 1]!;
  return [...branchBindingPrefix(b), nOp, ...pubKeys, nOp, OP.OP_CHECKMULTISIG];
}
/** Unlocking for the N-of-N funding multisig: OP_0 (legacy dummy) then the N signatures. */
export function fundingUnlocking(sigs: readonly Uint8Array[]): Script {
  return [OP.OP_0, ...sigs];
}

/**
 * Reveal-or-timeout (core §6.6): the IF branch accepts a valid reveal opening
 * (SHA-256(preimage)=cmt) before maturity; the ELSE branch is the refund path that becomes
 * spendable only after maturity — maturity is enforced at the TRANSACTION level (nLockTime),
 * NOT in-script (REQ-TX-001/002).
 */
export function revealOrTimeoutLocking(
  b: BranchBinding,
  commitment: Uint8Array,
  revealPub: Uint8Array,
  refundPub: Uint8Array,
): Script {
  return [
    ...branchBindingPrefix(b),
    OP.OP_IF,
    OP.OP_SHA256,
    commitment,
    OP.OP_EQUALVERIFY,
    revealPub,
    OP.OP_CHECKSIG,
    OP.OP_ELSE,
    refundPub,
    OP.OP_CHECKSIG,
    OP.OP_ENDIF,
  ];
}
export function revealUnlocking(sig: Uint8Array, preimage: Uint8Array): Script {
  return [sig, preimage, OP.OP_1];
}
export function timeoutRefundUnlocking(sig: Uint8Array): Script {
  return [sig, OP.OP_0];
}

/**
 * Fold (core §6.6, P5): proves the player controls their concealed outputs and surrenders them
 * to a dead-hand state WITHOUT disclosing face values — it is just a control proof + binding.
 */
export function foldLocking(b: BranchBinding, playerPub: Uint8Array): Script {
  return [...branchBindingPrefix(b), playerPub, OP.OP_CHECKSIG];
}
export function foldUnlocking(sig: Uint8Array): Script {
  return [sig];
}

/** Settlement (core §6.6): pays the winner on a valid signature + binding. */
export function settlementLocking(b: BranchBinding, winnerPub: Uint8Array): Script {
  return [...branchBindingPrefix(b), winnerPub, OP.OP_CHECKSIG];
}
export function settlementUnlocking(sig: Uint8Array): Script {
  return [sig];
}

/**
 * Fair-play (core §4.7, §6.6, REQ-CRYPTO-006/009): an in-script proof that the key a party USED
 * derives from what it COMMITTED — a mismatch forfeits the bonded funds (honest play is the
 * rational outcome with no referee). The claim branch reveals the public key, requires
 * HASH160(pub) == the committed key-commitment, then a signature under that key; a party who
 * used a different key cannot satisfy the hash check and cannot redeem.
 *
 * REQ-CRYPTO-009 / §19.C: this is the per-card/per-batch fair-play structure (the measured-size
 * fallback to a single 52-card N-party EC-derivation script). The full GB2616862 in-script
 * EC-point-derivation proof (pages 55–60) is the §19.C upgrade once the embedded node's
 * interpreter provides the EC numeric opcodes — TRACKED ASSUMPTION on byte size until then.
 */
export function fairPlayCommitment(pub: Uint8Array): Uint8Array {
  // HASH160(pub) = RIPEMD160(SHA256(pub)).
  const inner = sha256(pub);
  // reuse the interpreter's hash via a local ripemd path
  return ripemd160(inner);
}

export function fairPlayLocking(
  b: BranchBinding,
  keyCommitment: Uint8Array,
  refundPub: Uint8Array,
): Script {
  return [
    ...branchBindingPrefix(b),
    OP.OP_IF,
    OP.OP_DUP,
    OP.OP_HASH160,
    keyCommitment,
    OP.OP_EQUALVERIFY,
    OP.OP_CHECKSIG,
    OP.OP_ELSE,
    refundPub,
    OP.OP_CHECKSIG,
    OP.OP_ENDIF,
  ];
}
/** Claim the fair-play funds by revealing the committed key + a signature under it. */
export function fairPlayClaimUnlocking(sig: Uint8Array, pub: Uint8Array): Script {
  return [sig, pub, OP.OP_1];
}
/** Forfeit/refund branch (after maturity; tx-level, never in-script). */
export function fairPlayForfeitUnlocking(sig: Uint8Array): Script {
  return [sig, OP.OP_0];
}

/** The hiding-commitment preimage SHA-256(face‖blind) for reveal-or-timeout (core §4.5/§4.6). */
export function revealPreimage(face: number, blind: Uint8Array): Uint8Array {
  const w = new ByteWriter();
  w.u8(face);
  for (const x of blind) w.u8(x);
  return w.toBytes();
}
export function revealCommitment(face: number, blind: Uint8Array): Uint8Array {
  return sha256(revealPreimage(face, blind));
}
