/**
 * Session authentication for the live relay protocol (audit findings 1–3, 6). Every table envelope
 * (join / commit / reveal / action) is SIGNED by the seat's session key so a relay participant
 * cannot forge another seat's message. Browser-safe: uses Web Crypto Ed25519 (no node:crypto), so it
 * runs in the webview and in Node. The signed message binds tableId + hand + seat + the payload, and
 * receivers verify the signature against the public key REGISTERED to the acting seat.
 */

import { bytesToHex, sha256, hexToBytes } from '@bsv-poker/protocol-types';

// Minimal structural view of Web Crypto subtle (avoids depending on the DOM lib; the runtime object
// exists in Node 24 + modern browsers). Ed25519 sign/verify only.
interface MinimalSubtle {
  generateKey(alg: unknown, extractable: boolean, usages: string[]): Promise<{ publicKey: unknown; privateKey: unknown }>;
  exportKey(format: string, key: unknown): Promise<ArrayBuffer | { x?: string }>;
  importKey(format: string, data: Uint8Array, alg: unknown, extractable: boolean, usages: string[]): Promise<unknown>;
  sign(alg: unknown, key: unknown, data: Uint8Array): Promise<ArrayBuffer>;
  verify(alg: unknown, key: unknown, sig: Uint8Array, data: Uint8Array): Promise<boolean>;
}
const subtle = (globalThis as unknown as { crypto: { subtle: MinimalSubtle } }).crypto.subtle;
const ALG = { name: 'Ed25519' } as const;
// PKCS8 DER prefix for an Ed25519 private key (header ‖ 0x0420 ‖ 32-byte seed).
const ED25519_PKCS8_PREFIX = hexToBytes('302e020100300506032b657004220420');

function b64urlToBytes(s: string): Uint8Array {
  const b64 = s.replace(/-/g, '+').replace(/_/g, '/');
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

/** Derive a 32-byte sub-seed from a root secret for a labelled purpose (domain separation). */
export function deriveSeatSeed(root: Uint8Array, label = 'bsv-poker/seat-ed25519'): Uint8Array {
  const lab = new TextEncoder().encode(label);
  const buf = new Uint8Array(lab.length + root.length);
  buf.set(lab);
  buf.set(root, lab.length);
  return sha256(buf);
}

export interface SessionAuth {
  /** Raw Ed25519 public key, hex — the seat identity used for seating + signature checks. */
  readonly pub: string;
  sign(msg: string): Promise<string>;
}

/** Create a fresh session signing key (the player's relay identity for this session). */
export async function createSessionAuth(): Promise<SessionAuth> {
  const kp = await subtle.generateKey(ALG, true, ['sign', 'verify']);
  const pub = bytesToHex(new Uint8Array((await subtle.exportKey('raw', kp.publicKey)) as ArrayBuffer));
  return {
    pub,
    async sign(msg: string): Promise<string> {
      const sig = await subtle.sign(ALG, kp.privateKey, new TextEncoder().encode(msg));
      return bytesToHex(new Uint8Array(sig));
    },
  };
}

/**
 * Derive the session signing key DETERMINISTICALLY from a 32-byte seed (the Ed25519 private seed).
 * Linking this seed to the wallet master root (via deriveSeatSeed(walletRoot)) makes the seat key and
 * the wallet key share one root: a valid seat signature proves control of the same root that funds
 * the buy-in (audit 3). The public key is read from the imported key's JWK.
 */
export async function sessionAuthFromSeed(seed: Uint8Array): Promise<SessionAuth> {
  if (seed.length !== 32) throw new Error('Ed25519 seed must be 32 bytes');
  const der = new Uint8Array(ED25519_PKCS8_PREFIX.length + 32);
  der.set(ED25519_PKCS8_PREFIX);
  der.set(seed, ED25519_PKCS8_PREFIX.length);
  const priv = await subtle.importKey('pkcs8', der, ALG, true, ['sign']);
  const jwk = (await subtle.exportKey('jwk', priv)) as { x?: string };
  if (!jwk.x) throw new Error('could not derive Ed25519 public key from seed');
  const pub = bytesToHex(b64urlToBytes(jwk.x));
  return {
    pub,
    async sign(msg: string): Promise<string> {
      const sig = await subtle.sign(ALG, priv, new TextEncoder().encode(msg));
      return bytesToHex(new Uint8Array(sig));
    },
  };
}

/** Verify a signature against a raw Ed25519 public key (hex). False on any malformed input. */
export async function verifySig(pubHex: string, msg: string, sigHex: string): Promise<boolean> {
  try {
    const key = await subtle.importKey('raw', hexToBytes(pubHex), ALG, false, ['verify']);
    return await subtle.verify(ALG, key, hexToBytes(sigHex), new TextEncoder().encode(msg));
  } catch {
    return false;
  }
}

/**
 * Canonical signed message for a table envelope — binds tableId, hand, seat, kind/phase, and the
 * payload fields, so a signature for one (table, hand, seat, action) cannot be replayed elsewhere.
 */
export function envelopeMessage(
  tableId: string,
  e: { t: string; seat: number; hand: number; kind?: string; amount?: number; c?: string; r?: string; discard?: readonly number[]; prev?: string },
): string {
  // `prev` binds the action to the prior state hash (audit 8) — a signed action cannot be replayed
  // against a different transcript position.
  return JSON.stringify([tableId, e.t, e.seat, e.hand, e.kind ?? '', e.amount ?? 0, e.c ?? '', e.r ?? '', e.discard ?? [], e.prev ?? '']);
}
