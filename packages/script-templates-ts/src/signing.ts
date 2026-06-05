/**
 * Real secp256k1 key + signature helpers used by the templates and the interpreter (core §6.7,
 * P9). Signatures are real ECDSA over SHA-256(preimage) (the interpreter's sighash convention).
 */

import {
  type KeyObject,
  generateKeyPairSync,
  sign as ecSign,
} from 'node:crypto';
import { readDerEcdsaSig } from '@bsv-poker/protocol-types';

export interface KeyPair {
  readonly priv: KeyObject;
  readonly pub: KeyObject;
  /** 33-byte SEC-1 compressed public key. */
  readonly pubCompressed: Uint8Array;
}

function b64urlToBytes(s: string): Uint8Array {
  const b64 = s.replace(/-/g, '+').replace(/_/g, '/');
  return new Uint8Array(Buffer.from(b64, 'base64'));
}

/** Compressed SEC-1 encoding from a public KeyObject's JWK (x,y). */
export function compressedPub(pub: KeyObject): Uint8Array {
  const jwk = pub.export({ format: 'jwk' }) as { x?: string; y?: string };
  if (!jwk.x || !jwk.y) throw new Error('not an EC public key');
  const x = b64urlToBytes(jwk.x);
  const y = b64urlToBytes(jwk.y);
  const x32 = new Uint8Array(32);
  x32.set(x, 32 - x.length);
  const prefix = (y[y.length - 1]! & 1) === 0 ? 0x02 : 0x03;
  const out = new Uint8Array(33);
  out[0] = prefix;
  out.set(x32, 1);
  return out;
}

export function genKeyPair(): KeyPair {
  const { privateKey, publicKey } = generateKeyPairSync('ec', { namedCurve: 'secp256k1' });
  return { priv: privateKey, pub: publicKey, pubCompressed: compressedPub(publicKey) };
}

/**
 * Sign a sighash preimage; returns a LOW-S (BIP-62) DER ECDSA signature — what OP_CHECKSIG
 * verifies and what the BSV node requires (it rejects high-S signatures).
 */
export function signPreimage(preimage: Uint8Array, priv: KeyObject): Uint8Array {
  return normalizeLowS(new Uint8Array(ecSign('sha256', Buffer.from(preimage), priv)));
}

const SECP256K1_N = BigInt('0xfffffffffffffffffffffffffffffffebaaedce6af48a03bbfd25e8cd0364141');

function bytesToBig(b: Uint8Array): bigint {
  let v = 0n;
  for (const x of b) v = (v << 8n) | BigInt(x);
  return v;
}
function bigToMinimalBE(n: bigint): Uint8Array {
  const out: number[] = [];
  let x = n;
  while (x > 0n) {
    out.unshift(Number(x & 0xffn));
    x >>= 8n;
  }
  if (out.length === 0) out.push(0);
  if (out[0]! & 0x80) out.unshift(0x00); // DER positive-integer sign byte
  return Uint8Array.from(out);
}
function derInt(b: Uint8Array): Uint8Array {
  return Uint8Array.from([0x02, b.length, ...b]);
}

/**
 * Re-encode a DER ECDSA signature with S in the lower half of the curve order (BIP-62). Parsing is
 * fully bounds-checked (readDerEcdsaSig) so a malformed/truncated DER input is returned unchanged
 * rather than read out of bounds (CWE-125/CWE-129); a structurally invalid signature is never
 * silently reshaped into a different value.
 */
export function normalizeLowS(der: Uint8Array): Uint8Array {
  const parsed = readDerEcdsaSig(der);
  if (parsed === null) return der; // not a well-formed DER ECDSA frame — leave untouched
  let sv = bytesToBig(parsed.s);
  if (sv > SECP256K1_N / 2n) sv = SECP256K1_N - sv;
  const rEnc = derInt(bigToMinimalBE(bytesToBig(parsed.r)));
  const sEnc = derInt(bigToMinimalBE(sv));
  const body = Uint8Array.from([...rEnc, ...sEnc]);
  return Uint8Array.from([0x30, body.length, ...body]);
}
