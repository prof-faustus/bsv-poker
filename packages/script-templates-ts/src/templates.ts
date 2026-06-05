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
  if (n < 1 || n > 16) throw new Error('funding supports 1..16-of-N (CHECKMULTISIG small ints)');
  const nOp = 0x50 + n; // OP_1..OP_16 = 0x51..0x60
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
 * Bond reveal-or-FORFEIT (audit finding 3, on-chain half).
 *
 * WHAT: a bond output with two mutually-exclusive spend branches —
 *   - REVEAL (owner reclaims): the bond owner spends by revealing the preimage of their committed
 *     value (`SHA256(preimage) == commitment`) and signing under their reveal key. A player who
 *     responds (reveals) gets their own bond back.
 *   - FORFEIT (beneficiary claims): after maturity (enforced at the TRANSACTION level via nLockTime,
 *     since CLTV is a no-op post-Genesis — REQ-TX-001/002), the POT BENEFICIARY spends the timeout
 *     branch and the bond is FORFEITED to the pot.
 *
 * WHY (and why this differs from `revealOrTimeoutLocking`): `revealOrTimeoutLocking`'s timeout branch
 * refunds the OWNER (no penalty — used for the cooperative stall recovery). This template makes the
 * timeout branch pay a DIFFERENT key (the beneficiary), turning "no response" into a real penalty: an
 * absent player loses their bond to the players who did respond. That is the accountability audit-3
 * asks for. It is structurally the reveal-or-timeout shape with the timeout key set to the pot.
 *
 * WHY this is the safe on-chain MECHANISM (the part that can be built and tested in isolation): each
 * branch is spendable UNILATERALLY by exactly one party — the owner (iff they can reveal) or the
 * beneficiary (iff maturity has passed). No multi-party pre-signing is required, and the owner cannot
 * be robbed: as long as the owner reveals before maturity, only they can spend (the beneficiary's
 * branch is not yet valid). The HARD part audit-3 still leaves open is the OFF-CHAIN agreement on the
 * maturity/deadline so both honest clients drop-and-continue at the same logical point — see
 * docs/audit-response-03.md. This template is the on-chain settlement that off-chain decision drives.
 *
 * SECURITY BOUNDARY: trusted — the branch binding and the commitment (our own). untrusted — every
 * unlocking witness; a wrong preimage fails `OP_EQUALVERIFY` and a wrong key fails `OP_CHECKSIG`,
 * INSIDE the interpreter (P9). Maturity is NOT enforced in-script (CLTV is a no-op) — the beneficiary
 * branch is gated by the spending transaction's nLockTime, which the node enforces.
 *
 * WHAT MUST NEVER BE ASSUMED: never assume the beneficiary cannot claim before maturity from the
 * SCRIPT alone — maturity lives in the transaction's nLockTime; the forfeit-claim transaction MUST
 * carry the agreed nLockTime or the node will reject it as premature.
 */
export function bondRevealOrForfeitLocking(
  b: BranchBinding,
  commitment: Uint8Array,
  ownerRevealPub: Uint8Array,
  beneficiaryPub: Uint8Array,
): Script {
  // Same opcode structure as revealOrTimeoutLocking, but the ELSE (timeout) key is the BENEFICIARY,
  // so the timeout branch forfeits the bond to the pot rather than refunding the owner.
  return revealOrTimeoutLocking(b, commitment, ownerRevealPub, beneficiaryPub);
}

/** Owner reclaims the bond by revealing the committed preimage + signing (REVEAL branch). */
export function bondReclaimByRevealUnlocking(sig: Uint8Array, preimage: Uint8Array): Script {
  return [sig, preimage, OP.OP_1];
}

/** Beneficiary claims the FORFEITED bond after maturity (FORFEIT/timeout branch). The maturity is
 *  enforced by the spending transaction's nLockTime (set by the caller), not in-script. */
export function bondForfeitClaimUnlocking(beneficiarySig: Uint8Array): Script {
  return [beneficiarySig, OP.OP_0];
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

// ---- In-script EC fair-play (GB2616862 §19.C, post-Genesis opcodes) ---------
// secp256k1 field prime p (y² = x³ + 7 mod p). p ≡ 3 (mod 4), so √a = a^((p+1)/4) mod p.
export const SECP256K1_P = BigInt(
  '0xfffffffffffffffffffffffffffffffffffffffffffffffffffffffefffffc2f',
);

function modpow(base: bigint, exp: bigint, mod: bigint): bigint {
  let result = 1n;
  let b = base % mod;
  let e = exp;
  while (e > 0n) {
    if (e & 1n) result = (result * b) % mod;
    b = (b * b) % mod;
    e >>= 1n;
  }
  return result;
}

/**
 * The shuffle-key point P' = (s, √(s³+7)) for scalar `s` (GB2616862 §4.2): the private key is
 * the x-coordinate `s`. Returns the point if s³+7 is a quadratic residue (a valid curve x), else
 * null (the caller picks another s — a genuine shuffle key is chosen so this holds).
 */
export function shuffleKeyPoint(s: bigint): { x: bigint; y: bigint } | null {
  const a = (((s * s % SECP256K1_P) * s) % SECP256K1_P + 7n) % SECP256K1_P;
  const y = modpow(a, (SECP256K1_P + 1n) / 4n, SECP256K1_P);
  if ((y * y) % SECP256K1_P !== a) return null; // a is not a QR → s is not a valid shuffle-key x
  return { x: s, y };
}

/** Script-number encoding (little-endian, sign-magnitude) matching the interpreter's `num`. */
export function encodeScriptNum(n: bigint): Uint8Array {
  if (n === 0n) return new Uint8Array(0);
  const neg = n < 0n;
  let x = neg ? -n : n;
  const out: number[] = [];
  while (x > 0n) {
    out.push(Number(x & 0xffn));
    x >>= 8n;
  }
  if ((out[out.length - 1]! & 0x80) !== 0) out.push(neg ? 0x80 : 0x00);
  else if (neg) out[out.length - 1]! |= 0x80;
  return Uint8Array.from(out);
}

/** Commitment to a shuffle-key scalar x: SHA-256(encodeScriptNum(x)) — hides x until reveal. */
export function shuffleKeyCommitment(x: bigint): Uint8Array {
  return sha256(encodeScriptNum(x));
}

/**
 * Fair-play (real, in-script EC — GB2616862 §4.7/§19.C). Proves the party used the shuffle key
 * it committed: the unlocking reveals the scalar `x` (= the key's x-coordinate) and `y`; the
 * script verifies (a) SHA-256(x) equals the commitment (the party did not swap keys), and (b) the
 * point is genuinely on secp256k1: y² ≡ x³ + 7 (mod p). A mismatched key fails INSIDE the
 * interpreter and the funds are forfeited (honest play is the rational outcome, no referee).
 * Uses the post-Genesis big-integer opcodes (OP_MUL/OP_MOD/OP_ADD/OP_NUMEQUALVERIFY) — NOW
 * available, replacing the earlier HASH160-only fallback.
 */
export function fairPlayEcLocking(b: BranchBinding, xCommitment: Uint8Array): Script {
  const p = encodeScriptNum(SECP256K1_P);
  const seven = encodeScriptNum(7n);
  return [
    ...branchBindingPrefix(b),
    // stack from unlocking: [x, y]
    OP.OP_OVER, // [x, y, x]
    OP.OP_SHA256, // [x, y, H(x)]
    xCommitment,
    OP.OP_EQUALVERIFY, // verify H(x)==commitment → [x, y]
    OP.OP_SWAP, // [y, x]
    OP.OP_DUP,
    OP.OP_DUP,
    OP.OP_MUL,
    OP.OP_MUL, // [y, x^3]
    seven,
    OP.OP_ADD, // [y, x^3+7]
    p,
    OP.OP_MOD, // [y, (x^3+7) mod p] = rhs
    OP.OP_SWAP, // [rhs, y]
    OP.OP_DUP,
    OP.OP_MUL, // [rhs, y^2]
    p,
    OP.OP_MOD, // [rhs, y^2 mod p]
    OP.OP_NUMEQUALVERIFY, // verify y^2 mod p == rhs → []
    OP.OP_1, // success
  ];
}
export function fairPlayEcUnlocking(x: bigint, y: bigint): Script {
  return [encodeScriptNum(x), encodeScriptNum(y)];
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
