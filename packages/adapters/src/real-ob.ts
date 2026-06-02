/**
 * Binds the REAL `overlay-broadcast` custody implementation (REQ-DEP-004, core §16/§19) — the
 * source of Mode B threshold group keys and the revocation registry. We drive its prebuilt CLI by
 * subprocess (same pattern as the BSV node): `custody keygen --threshold t --shares n` produces a
 * genuine t-of-n threshold group public key (no single party holds the whole private key — Mode B),
 * and `custody revoke` exercises the real revocation path. Not a conformant fake.
 */

import { execFileSync } from 'node:child_process';
import { join } from 'node:path';

const OB_DIR = process.env.BSV_OB_DIR ?? 'D:\\claude\\overlay-broadcast';
const OB_BIN = process.env.BSV_OB_BIN ?? join(OB_DIR, 'target', 'release', 'overlay-broadcast.exe');

function ob(args: string[]): string {
  return execFileSync(OB_BIN, args, { encoding: 'utf8' }).trim();
}

const SECP256K1_P = BigInt('0xfffffffffffffffffffffffffffffffffffffffffffffffffffffffefffffc2f');

function modpow(b: bigint, e: bigint, m: bigint): bigint {
  let r = 1n;
  b %= m;
  while (e > 0n) {
    if (e & 1n) r = (r * b) % m;
    b = (b * b) % m;
    e >>= 1n;
  }
  return r;
}

/** True if a 33-byte SEC-1 compressed key decodes to a real point on secp256k1 (y² = x³ + 7). */
export function isOnCurveCompressed(pub: Uint8Array): boolean {
  if (pub.length !== 33 || (pub[0] !== 0x02 && pub[0] !== 0x03)) return false;
  let x = 0n;
  for (let i = 1; i < 33; i++) x = (x << 8n) | BigInt(pub[i]!);
  if (x >= SECP256K1_P) return false;
  const rhs = (modpow(x, 3n, SECP256K1_P) + 7n) % SECP256K1_P;
  const y = modpow(rhs, (SECP256K1_P + 1n) / 4n, SECP256K1_P); // p ≡ 3 (mod 4)
  return (y * y) % SECP256K1_P === rhs;
}

export class RealOb {
  /** A real t-of-n threshold group public key (Mode B custody key), 33-byte compressed. */
  thresholdGroupKey(threshold: number, shares: number): Uint8Array {
    const hex = ob(['custody', 'keygen', '--threshold', String(threshold), '--shares', String(shares)]);
    if (!/^[0-9a-f]{66}$/.test(hex)) throw new Error(`unexpected OB keygen output: ${hex}`);
    return Uint8Array.from(Buffer.from(hex, 'hex'));
  }

  /** Exercise the real revocation path; returns whether the key is revoked. */
  revoke(): boolean {
    return ob(['custody', 'revoke']).includes('revoked=true');
  }

  /**
   * Mode B online threshold signing: a t-of-n quorum produces a standard ECDSA (DER, low-S)
   * signature over `prehash` (a 32-byte digest) under the group key — the group private key is
   * never reconstructed (GG20). Returns the group public key + the signature.
   */
  thresholdSign(threshold: number, shares: number, prehash: Uint8Array): { groupKey: Uint8Array; sig: Uint8Array } {
    if (prehash.length !== 32) throw new Error('prehash must be 32 bytes');
    const out = ob(['custody', 'sign', '--threshold', String(threshold), '--shares', String(shares), '--message', Buffer.from(prehash).toString('hex')]);
    const m = out.match(/pubkey=([0-9a-f]{66})\s+sig=([0-9a-f]+)/);
    if (!m) throw new Error(`unexpected OB sign output: ${out}`);
    return { groupKey: Uint8Array.from(Buffer.from(m[1]!, 'hex')), sig: Uint8Array.from(Buffer.from(m[2]!, 'hex')) };
  }
}
