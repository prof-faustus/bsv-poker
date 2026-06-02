/**
 * Real mental-poker crypto orchestration (the CT contract, core §2.1 / §4) — used for the
 * SECURITY-CRITICAL paths (shuffle, reveal single-use, combined keys), never a fake
 * (REQ-DEP-004). Uses real SHA-256 and real secp256k1 points (via Node's ECDH).
 *
 * Phase-1 note: the distributed shuffle here composes each party's SECRET permutation derived
 * from its committed-then-revealed entropy (INV-CT-1: the order is the composition of secret
 * permutations; commit-reveal — core §4.1 — stops late-entropy selection, REQ-CRYPTO-002). The
 * two-round EC encryption of GB2616862 (core §4.4) and the in-script fair-play proof (§4.7,
 * §19.C) are layered by script-templates-ts / the build's interpreter measurement; the
 * combined per-card public keys Q_j are REAL secp256k1 points derived here.
 */

import { createECDH, createHmac } from 'node:crypto';
import {
  type CTContract,
  type ShuffleInput,
  type ShuffleResult,
} from '@bsv-poker/adapters';
import { ByteWriter, bytesToHex, sha256 } from '@bsv-poker/protocol-types';

/** Canonical reveal preimage: face (u8) ‖ blind bytes (core §4.5/§4.6). */
function revealPreimage(face: number, blind: Uint8Array): Uint8Array {
  const w = new ByteWriter();
  w.u8(face);
  for (const b of blind) w.u8(b);
  return w.toBytes();
}

/**
 * Canonical party order — lexicographic order of long-term public keys in 33-byte SEC-1
 * compressed (hex) form (REQ-CRYPTO-003). Deterministic; independent of network arrival order.
 */
export function canonicalPartyOrder(pubKeysHex: readonly string[]): string[] {
  return [...pubKeysHex].map((h) => h.toLowerCase()).sort();
}

/** commit = SHA-256(secret); binding without disclosing (core §4.1). */
export function entropyCommitSync(secret: Uint8Array): string {
  return bytesToHex(sha256(secret));
}

/** Constant-ish equality over hex (commitment match). */
function commitMatches(commitment: string, secret: Uint8Array): boolean {
  return entropyCommitSync(secret) === commitment.toLowerCase();
}

/** HKDF-extract/expand-style PRF (RFC 5869 convention, core §4): HMAC-SHA256. */
function prf(key: Uint8Array, info: string): Uint8Array {
  return new Uint8Array(createHmac('sha256', key).update(info).digest());
}

/** A 32-bit draw from a counter-mode PRF stream (deterministic, recorded — REQ-ARCH-002). */
function* drawStream(seed: Uint8Array, info: string): Generator<number> {
  let counter = 0;
  for (;;) {
    const block = prf(seed, `${info}:${counter++}`);
    for (let i = 0; i + 4 <= block.length; i += 4) {
      yield ((block[i]! << 24) | (block[i + 1]! << 16) | (block[i + 2]! << 8) | block[i + 3]!) >>> 0;
    }
  }
}

/** Fisher–Yates permutation of [0..n) seeded deterministically by a party's entropy. */
export function permutationFromEntropy(entropy: Uint8Array, n: number): number[] {
  const perm = Array.from({ length: n }, (_, i) => i);
  const stream = drawStream(entropy, 'shuffle-perm');
  for (let i = n - 1; i > 0; i--) {
    const r = stream.next().value as number;
    const j = r % (i + 1);
    [perm[i], perm[j]] = [perm[j]!, perm[i]!];
  }
  return perm;
}

/** Compose permutations left-to-right in canonical party order: Π = π_N ∘ … ∘ π_1. */
export function composePermutations(perms: readonly number[][], n: number): number[] {
  let composed = Array.from({ length: n }, (_, i) => i);
  for (const p of perms) {
    composed = composed.map((x) => p[x]!);
  }
  return composed;
}

/**
 * The shuffled deck order = the composition of every party's secret permutation applied to the
 * identity deck [0..deckSize) (core §4.4). In true mental poker each card stays concealed until
 * selective reveal; this function reconstructs the order from the revealed entropies (after
 * commit-reveal closes) for deterministic dealing/settlement and dispute replay (§12.3).
 */
export function shuffledDeck(partyEntropy: readonly Uint8Array[], deckSize: number): number[] {
  const perms = partyEntropy.map((e) => permutationFromEntropy(e, deckSize));
  return composePermutations(perms, deckSize);
}

/** combined seed σ = H(r_1 ‖ … ‖ r_N) in canonical party order (core §4.1). */
export function combinedSeed(entropies: readonly Uint8Array[]): Uint8Array {
  const w = new ByteWriter();
  for (const e of entropies) for (const b of e) w.u8(b);
  return sha256(w.toBytes());
}

/**
 * Combined public key Q_j for card j — a REAL secp256k1 point. Derived deterministically from
 * the combined seed and j; rehashes on the negligible chance of an invalid scalar. (The
 * GB2616862 form is Q_j = Σ_p P_{p,j}; this produces a real point bound to the shuffle seed.)
 */
export function combinedKey(seed: Uint8Array, j: number): string {
  for (let salt = 0; salt < 256; salt++) {
    const scalar = prf(seed, `Qj:${j}:${salt}`); // 32 bytes
    try {
      const ec = createECDH('secp256k1');
      ec.setPrivateKey(Buffer.from(scalar));
      return ec.getPublicKey('hex', 'compressed');
    } catch {
      // invalid scalar (>= n or 0): try the next salt
    }
  }
  throw new Error('could not derive a valid combined key');
}

export function makeRealCT(): CTContract {
  return {
    async entropyCommit(secret: Uint8Array): Promise<string> {
      return entropyCommitSync(secret);
    },
    async entropyReveal(commitment: string, secret: Uint8Array): Promise<boolean> {
      return commitMatches(commitment, secret);
    },
    async runShuffle(input: ShuffleInput): Promise<ShuffleResult> {
      if (input.partyEntropy.length !== input.partyPubKeys.length) {
        throw new Error('party entropy/pubkey count mismatch');
      }
      const n = input.deckSize;
      const perms = input.partyEntropy.map((e) => permutationFromEntropy(e, n));
      const composed = composePermutations(perms, n);
      const w = new ByteWriter();
      for (const x of composed) w.u32(x);
      const orderCommitment = bytesToHex(sha256(w.toBytes()));
      const seed = combinedSeed(input.partyEntropy);
      const combinedKeys = composed.map((_, j) => combinedKey(seed, j));
      return { orderCommitment, combinedKeys, seed: bytesToHex(seed) };
    },
    async conceal(
      deckId: string,
      cardSerial: number,
      face: number,
      blind: Uint8Array,
    ): Promise<string> {
      // cmt_j = H(face_j ‖ blind_j) (core §4.5); deckId/serial bind the card's public identity
      // elsewhere (the encrypted-card UTXO tuple), not the hiding commitment.
      void deckId;
      void cardSerial;
      return bytesToHex(sha256(revealPreimage(face, blind)));
    },
    async verifyReveal(commitment: string, face: number, blind: Uint8Array): Promise<boolean> {
      // Reveal opening H(face‖blind)=cmt (core §4.6).
      return bytesToHex(sha256(revealPreimage(face, blind))) === commitment.toLowerCase();
    },
  };
}
