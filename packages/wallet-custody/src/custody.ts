/**
 * Wallet custody (core §9). One long-term secp256k1 key per player; per-game/per-card scalars
 * derived deterministically via HKDF bound to (gid, j[, role]) — REQ-WALLET-001/002: the device
 * stores ONE key, derivation is deterministic and auditable, old-game keys reveal nothing.
 *
 * The Custody interface (REQ-WALLET-003) abstracts where keys live and signing happens; the
 * DEFAULT software backend holds keys in-process and never exposes scalars to the UI beyond the
 * viewer path. Mode A (Phase-1 default, core §4.3/§9.3): per-card scalars are single-game and
 * `reconstructAndSign` sums disclosed scalars to sign the combined-key spend; Mode B's
 * `combineSignShare` is present in the interface but the software backend marks it unsupported.
 */

import {
  type KeyObject,
  createPrivateKey,
  createPublicKey,
  hkdfSync,
} from 'node:crypto';
import { bytesToHex } from '@bsv-poker/protocol-types';
import { compressedPub, signPreimage } from '@bsv-poker/script-templates-ts';

export interface SignIntent {
  /** The exact bytes being signed (the sighash preimage). */
  readonly sighashPreimage: Uint8Array;
  /** Human-readable description surfaced in the signing prompt (no silent signing, §11.6). */
  readonly describe: { action: string; amounts?: string; potOrState?: string };
}

export interface Custody {
  /** HKDF-derived PUBLIC key for (gid, j, role); never returns the scalar (REQ-APP-025). */
  derive(gid: string, j: number, role: string): string; // compressed pubkey hex
  /** Sign exactly the intent's bytes with the (gid,j,role) key. */
  sign(gid: string, j: number, role: string, intent: SignIntent): Uint8Array;
  /** Decrypt a concealed card into the controlled viewer path (returns a viewer token). */
  decryptToViewer(commitmentHex: string): string;
  /** Mode B threshold share (core §6.7); software backend does not support it. */
  combineSignShare(): never;
  /** Mode A (core §4.3): reconstruct w_j = Σ scalars and sign (scoped, single-game, audited). */
  reconstructAndSign?(scalars: readonly Uint8Array[], intent: SignIntent): Uint8Array;
}

/** secp256k1 group order n. */
const N = BigInt('0xfffffffffffffffffffffffffffffffebaaedce6af48a03bbfd25e8cd0364141');

/** Build a PKCS8 DER private key for secp256k1 from a 32-byte scalar `d`. */
export function scalarToPrivateKey(d: Uint8Array): KeyObject {
  if (d.length !== 32) throw new Error('scalar must be 32 bytes');
  const ecPrivateKey = Uint8Array.from([0x30, 0x25, 0x02, 0x01, 0x01, 0x04, 0x20, ...d]);
  const algId = Uint8Array.from([
    0x30, 0x10, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01, 0x06, 0x05, 0x2b, 0x81,
    0x04, 0x00, 0x0a,
  ]);
  const inner = Uint8Array.from([0x04, ecPrivateKey.length, ...ecPrivateKey]);
  const body = Uint8Array.from([0x02, 0x01, 0x00, ...algId, ...inner]);
  const der = Uint8Array.from([0x30, body.length, ...body]);
  return createPrivateKey({ key: Buffer.from(der), format: 'der', type: 'pkcs8' });
}

/** Derive a valid secp256k1 scalar (in [1, n-1]) deterministically from the master + info. */
function deriveScalar(master: Uint8Array, info: string): Uint8Array {
  for (let salt = 0; salt < 256; salt++) {
    const out = new Uint8Array(
      hkdfSync('sha256', Buffer.from(master), Buffer.alloc(0), `${info}:${salt}`, 32) as ArrayBuffer,
    );
    let v = 0n;
    for (const b of out) v = (v << 8n) | BigInt(b);
    if (v >= 1n && v < N) return out;
  }
  throw new Error('could not derive a valid scalar');
}

/** Software custody backend (DEFAULT, AD7). Holds one master key in process. */
export function createSoftwareCustody(masterKey: Uint8Array): Custody {
  if (masterKey.length < 16) throw new Error('master key too short');
  const cache = new Map<string, KeyObject>();

  function priv(gid: string, j: number, role: string): KeyObject {
    const key = `${gid}:${j}:${role}`;
    let k = cache.get(key);
    if (!k) {
      k = scalarToPrivateKey(deriveScalar(masterKey, key));
      cache.set(key, k);
    }
    return k;
  }

  return {
    derive(gid, j, role) {
      const pub = createPublicKey(priv(gid, j, role));
      return bytesToHex(compressedPub(pub));
    },
    sign(gid, j, role, intent) {
      return signPreimage(intent.sighashPreimage, priv(gid, j, role));
    },
    decryptToViewer(commitmentHex) {
      // Viewer-path stand-in: the rendered face never leaves this boundary as raw key material.
      return `viewer:${commitmentHex.slice(0, 16)}`;
    },
    combineSignShare(): never {
      throw new Error('software custody does not support Mode B threshold signing (use OB.custody)');
    },
    reconstructAndSign(scalars, intent) {
      // Mode A: w_j = Σ s_{p,j} mod n; sign with the reconstructed combined key (core §4.3).
      let w = 0n;
      for (const s of scalars) {
        let v = 0n;
        for (const b of s) v = (v << 8n) | BigInt(b);
        w = (w + v) % N;
      }
      if (w === 0n) throw new Error('combined scalar is zero');
      const d = new Uint8Array(32);
      let x = w;
      for (let i = 31; i >= 0; i--) {
        d[i] = Number(x & 0xffn);
        x >>= 8n;
      }
      return signPreimage(intent.sighashPreimage, scalarToPrivateKey(d));
    },
  };
}
