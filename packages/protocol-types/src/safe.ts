/**
 * Hardened, defect-class-eliminating primitives shared across the whole stack
 * (SANS/CWE Top 25 + NASA/JPL Power-of-10 + Microsoft SDL). Pure TypeScript, browser-safe
 * (no `node:crypto`), deterministic. These exist so the corresponding defect CLASSES are
 * impossible by construction rather than patched per call-site:
 *
 *  - {@link tryHexToBytes} / {@link constantTimeEqualHex}: strict hex handling and timing-safe
 *    comparison. The canonical strict hex *decoder* lives in serialize.ts ({@link hexToBytes});
 *    here we add the non-throwing boundary variant and the constant-time comparators.
 *  - {@link constantTimeEqualBytes} / {@link constantTimeEqualHex}: length-folded timing-safe
 *    comparison for secrets, commitments, MACs and capability tokens (CWE-208 Observable Timing
 *    Discrepancy, CWE-697 Incorrect Comparison). Never `===` on secret-dependent material.
 *  - {@link safeJsonParse}: bounded JSON parse for hostile network/file boundaries — rejects
 *    oversize input and over-deep / over-wide structures and NEVER throws (CWE-400 Uncontrolled
 *    Resource Consumption). Returns a discriminated result, never partial trust.
 *  - {@link cryptoRandomBytes} / {@link randomId}: CSPRNG-only randomness. Math.random() is
 *    banned for any value an adversary could benefit from predicting (CWE-338).
 *
 * Every function validates its arguments and treats all external bytes as hostile until proven
 * well-formed (SANS/CWE), checks every return value, and runs only bounded loops with provable
 * termination (NASA P10).
 */

import { hexToBytes } from './serialize.ts';

// ---- Strict hex (non-throwing boundary variant) -----------------------------

/**
 * Decode hex WITHOUT throwing — the variant for hostile boundaries (relay frames, files, UI).
 * Returns null on any malformed input (odd length, non-hex nibble, non-string). The strict
 * throwing decoder {@link hexToBytes} is for trusted/internal callers; this one never escapes a
 * parse error to the caller (SANS/CWE: parsers must reject malformed input without panicking).
 */
export function tryHexToBytes(h: unknown): Uint8Array | null {
  if (typeof h !== 'string') return null;
  if ((h.length & 1) !== 0) return null;
  try {
    return hexToBytes(h);
  } catch {
    return null;
  }
}

// ---- Constant-time comparison (CWE-208 / CWE-697) ---------------------------

/**
 * Timing-safe byte comparison. Length difference is folded into the accumulator so the function
 * neither early-returns nor branches on secret-dependent content. Bounded loop over the longer
 * length (NASA P10). Returns true iff the inputs are byte-identical.
 */
export function constantTimeEqualBytes(a: Uint8Array, b: Uint8Array): boolean {
  // Precondition assertions (NASA P10: >=2 assertions/fn).
  if (!(a instanceof Uint8Array) || !(b instanceof Uint8Array)) return false;
  const n = a.length > b.length ? a.length : b.length;
  if (n > 1 << 20) return false; // attack-surface bound: nothing legitimate is >1 MiB
  let diff = a.length ^ b.length;
  for (let i = 0; i < n; i++) {
    // Out-of-range reads on a Uint8Array yield undefined; coerce to 0 deterministically.
    const x = i < a.length ? a[i]! : 0;
    const y = i < b.length ? b[i]! : 0;
    diff |= x ^ y;
  }
  return diff === 0;
}

/**
 * Timing-safe comparison of two hex strings (case-insensitive). Used for commitment/reveal hash
 * equality, MAC checks and token checks. Malformed hex compares unequal. The timing-sensitive
 * path (two valid equal-length hashes) is constant-time.
 */
export function constantTimeEqualHex(aHex: string, bHex: string): boolean {
  const a = tryHexToBytes(typeof aHex === 'string' ? aHex.toLowerCase() : aHex);
  const b = tryHexToBytes(typeof bHex === 'string' ? bHex.toLowerCase() : bHex);
  if (a === null || b === null) return false;
  return constantTimeEqualBytes(a, b);
}

// ---- Bounded JSON parse (CWE-400) -------------------------------------------

export type JsonParseResult =
  | { readonly ok: true; readonly value: unknown }
  | { readonly ok: false; readonly reason: string };

export interface JsonParseLimits {
  /** Max input length in UTF-16 code units (cheap, conservative upper bound on bytes). */
  readonly maxBytes?: number;
  /** Max nesting depth of the parsed structure. */
  readonly maxDepth?: number;
  /** Max total nodes (object/array/leaf) in the parsed structure. */
  readonly maxNodes?: number;
}

const DEFAULT_MAX_BYTES = 1 << 20; // 1 MiB
const DEFAULT_MAX_DEPTH = 64;
const DEFAULT_MAX_NODES = 100_000;

/**
 * Parse JSON from a hostile source with hard resource bounds, never throwing. Size is checked
 * BEFORE JSON.parse so a huge frame is rejected without buffering work; depth/width are checked
 * AFTER on the parsed value with an explicit iterative walk (no recursion — NASA P10). On any
 * violation or malformed JSON, returns { ok:false } — callers must fail closed.
 */
export function safeJsonParse(text: unknown, limits: JsonParseLimits = {}): JsonParseResult {
  const maxBytes = limits.maxBytes ?? DEFAULT_MAX_BYTES;
  const maxDepth = limits.maxDepth ?? DEFAULT_MAX_DEPTH;
  const maxNodes = limits.maxNodes ?? DEFAULT_MAX_NODES;
  if (typeof text !== 'string') return { ok: false, reason: 'not a string' };
  if (text.length === 0) return { ok: false, reason: 'empty' };
  if (text.length > maxBytes) return { ok: false, reason: `oversize: ${text.length} > ${maxBytes}` };
  let value: unknown;
  try {
    value = JSON.parse(text);
  } catch {
    return { ok: false, reason: 'malformed JSON' };
  }
  if (!withinStructuralBounds(value, maxDepth, maxNodes)) {
    return { ok: false, reason: 'structure exceeds depth/node bounds' };
  }
  return { ok: true, value };
}

/** Iterative depth/node-count guard (explicit stack; bounded by maxNodes — NASA P10). */
function withinStructuralBounds(root: unknown, maxDepth: number, maxNodes: number): boolean {
  const stack: { v: unknown; d: number }[] = [{ v: root, d: 0 }];
  let nodes = 0;
  // Bound: each pop increments nodes; we abort once nodes exceeds maxNodes, so the loop is
  // guaranteed to terminate in at most maxNodes+1 iterations.
  while (stack.length > 0) {
    const top = stack.pop();
    if (top === undefined) break; // unreachable (length>0), keeps the type checker honest
    nodes++;
    if (nodes > maxNodes) return false;
    if (top.d > maxDepth) return false;
    const v = top.v;
    if (Array.isArray(v)) {
      if (v.length > maxNodes) return false;
      for (let i = 0; i < v.length; i++) stack.push({ v: v[i], d: top.d + 1 });
    } else if (v !== null && typeof v === 'object') {
      const keys = Object.keys(v as Record<string, unknown>);
      if (keys.length > maxNodes) return false;
      for (let i = 0; i < keys.length; i++) stack.push({ v: (v as Record<string, unknown>)[keys[i]!], d: top.d + 1 });
    }
  }
  return true;
}

// ---- CSPRNG-only randomness (CWE-338) ---------------------------------------

interface MinimalCrypto {
  getRandomValues<T extends ArrayBufferView>(array: T): T;
}
function webCrypto(): MinimalCrypto {
  const c = (globalThis as unknown as { crypto?: MinimalCrypto }).crypto;
  if (!c || typeof c.getRandomValues !== 'function') {
    // Fail closed: there is no safe fallback for security randomness (Math.random is banned).
    throw new Error('no CSPRNG available (globalThis.crypto.getRandomValues missing)');
  }
  return c;
}

/** Cryptographically secure random bytes. Throws (fail-closed) if no CSPRNG is present. */
export function cryptoRandomBytes(n: number): Uint8Array {
  if (!Number.isInteger(n) || n < 0 || n > 1 << 16) throw new RangeError(`cryptoRandomBytes: ${n}`);
  const out = new Uint8Array(n);
  webCrypto().getRandomValues(out);
  return out;
}

/** Unpredictable identifier (CSPRNG) — for table ids, nonces, anything an adversary may guess. */
export function randomId(byteLen = 16): string {
  const b = cryptoRandomBytes(byteLen);
  let s = '';
  for (let i = 0; i < b.length; i++) s += b[i]!.toString(16).padStart(2, '0');
  return s;
}

// ---- Bounds-checked DER ECDSA reader (CWE-125 / CWE-129) ---------------------

/**
 * Parse a short-form DER ECDSA signature `30 len 02 rlen <r> 02 slen <s>` and return (r,s),
 * or null if the structure is malformed. Every read is bounds-checked against the buffer length
 * BEFORE indexing, so a crafted/truncated signature can never read past the end (no `der[i]!`
 * non-null-assertion reads into undefined). secp256k1 signatures are always < 128 bytes total, so
 * only short-form length octets are accepted; long-form is rejected. Trailing bytes are rejected.
 */
export function readDerEcdsaSig(der: Uint8Array): { r: Uint8Array; s: Uint8Array } | null {
  if (!(der instanceof Uint8Array)) return null;
  if (der.length < 8 || der.length > 72) return null; // 6-byte frame + at least 1 byte each r,s
  let i = 0;
  const remaining = (): number => der.length - i;
  if (der[i++] !== 0x30) return null;
  const bodyLen = der[i++]!;
  if (bodyLen !== der.length - 2) return null; // exact frame, no trailing/short data
  // r INTEGER
  if (remaining() < 2 || der[i++] !== 0x02) return null;
  const rlen = der[i++]!;
  if (rlen === 0 || rlen > remaining()) return null;
  const r = der.slice(i, i + rlen);
  i += rlen;
  // s INTEGER
  if (remaining() < 2 || der[i++] !== 0x02) return null;
  const slen = der[i++]!;
  if (slen === 0 || slen > remaining()) return null;
  const s = der.slice(i, i + slen);
  i += slen;
  if (i !== der.length) return null; // no trailing bytes
  return { r, s };
}
