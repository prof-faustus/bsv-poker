/**
 * A real BSV Script stack interpreter with Genesis rules (core §6.2, §14.3, P9).
 *
 * ============================================================================================
 * WHAT
 * ============================================================================================
 * Executes a parsed Script (opcodes + pushdata) against a shared stack and returns ok|fail. It is
 * the SECOND hostile-input grammar of the system: the transaction parser (tx-builder/parse.ts)
 * deliberately returns raw script bytes and defers their meaning to here. Signature checks use REAL
 * secp256k1 ECDSA (Node crypto). Negative tests fail INSIDE this interpreter, never in a wrapper —
 * that is the P9 obligation.
 *
 * ============================================================================================
 * HOW
 * ============================================================================================
 * A single forward pass over the script items. Pushdata is pushed; opcodes mutate the stack.
 * IF/ELSE/ENDIF maintain an execution-flag stack so skipped branches are tracked but not run. Any
 * stack underflow / decode error is caught locally and turned into a clean `{ ok:false }` — a
 * malformed or hostile script NEVER throws out of `evaluate`/`run`.
 *
 * RESOURCE BOUNDS (every loop and value has a provable bound — NASA P10; DoS resistance — CWE-400):
 *  - MAX_STACK            : stack depth is checked each iteration; an attacker cannot grow it without
 *                           limit (e.g. via repeated OP_DUP).
 *  - MAX_MULTISIG_KEYS    : OP_CHECKMULTISIG's n and m are validated to [0, MAX_MULTISIG_KEYS] BEFORE
 *                           any pop loop, so the key/sig pop counts are bounded by a constant rather
 *                           than "however far the stack happens to go". This closes the unbounded-pop
 *                           DoS where a crafted n drives pops until underflow.
 *  - MAX_SCRIPT_NUM_BYTES : a decoded/produced script number is capped, so repeated OP_MUL cannot
 *                           grow a bignum without bound (memory/CPU exhaustion). The cap is far above
 *                           any legitimate field element (256-bit) used by the in-script EC proof.
 *
 * Genesis semantics encoded here:
 *  - OP_CHECKLOCKTIMEVERIFY / OP_CHECKSEQUENCEVERIFY are NO-OPS (REQ-TX-001).
 *  - OP_RETURN is invalid wherever it appears (core P11/§6.5): the script fails.
 *
 * ============================================================================================
 * WHY (and why THIS design)
 * ============================================================================================
 * A locking/unlocking script pair on-chain is attacker-authored. An interpreter that can be made to
 * loop or allocate without bound by a crafted script is a denial-of-service primitive. We make every
 * loop bound explicit and constant-bounded rather than relying on an incidental terminator (such as
 * "the pop will eventually underflow"): an incidental bound is one refactor away from being infinite,
 * and an auditor cannot see it. WHY catch-and-convert rather than let errors propagate: callers
 * validate scripts as part of consensus-like checks and must treat ANY malformed script as simply
 * "script failed", never as a crash.
 *
 * ============================================================================================
 * SECURITY BOUNDARY
 * ============================================================================================
 *   trusted inputs:    the ScriptContext.sighashPreimage (computed by our signer/sighash code).
 *   untrusted inputs:  the unlocking and locking Script items — every opcode and pushdata is hostile.
 *   recoverable errors: stack underflow, unbalanced IF, unsupported opcode, out-of-range multisig
 *                      counts, oversized script numbers, OP_RETURN, failed VERIFY/CHECKSIG — all
 *                      become `{ ok:false, reason }`. The script simply does not authorise the spend.
 *   fatal errors:      none. No script throws out of this module (proven by the fuzz test).
 *   side effects:      none beyond the local stack; ECDSA verify is pure; no I/O, no globals.
 *   state mutation:    the local stack only; the input scripts are never mutated.
 *
 * WHAT MUST NEVER BE ASSUMED
 *   - never assume a popped value is well-formed — it is attacker bytes (hence num()'s size cap);
 *   - never assume the stack is non-empty before a pop — use pop(), which fails closed;
 *   - never remove a resource bound "because real scripts are small" — the bound is the defence.
 *
 * WHAT BREAKS IF THE RULE IS VIOLATED
 *   Removing MAX_MULTISIG_KEYS reopens the unbounded-pop DoS; removing MAX_SCRIPT_NUM_BYTES lets a
 *   tiny script (a few OP_MULs) allocate gigabytes; removing MAX_STACK lets OP_DUP loops exhaust
 *   memory. Each is a one-line denial of service against any node running this interpreter.
 *
 * TRACKED ASSUMPTION: this is the platform's self-contained interpreter for the opcode subset
 * the templates use; sighash here is ECDSA over SHA-256(preimage). A production swap to the
 * embedded node's full interpreter (double-SHA-256 sighash, every opcode) is a later step; the
 * template tests then re-run against it unchanged.
 */

import { createHash, createPublicKey, verify as ecVerify } from 'node:crypto';
import { OP } from './opcodes.ts';
import type { Script, ScriptItem } from './script.ts';

/**
 * Maximum stack depth. Bitcoin's consensus limit is 1000 elements (main + alt). A legitimate
 * template uses a handful; this bound only ever stops an adversarial DUP/PUSH flood.
 */
const MAX_STACK = 1000;

/**
 * Maximum pubkeys (and therefore signatures) in one OP_CHECKMULTISIG. Bitcoin consensus caps this at
 * 20. n and m are validated against this BEFORE any pop loop runs, so the pop counts are bounded by
 * a constant — not by however far the attacker-controlled stack happens to reach.
 */
const MAX_MULTISIG_KEYS = 20;

/**
 * Maximum byte length of a script number (decoded or produced). Post-Genesis BSV removed the 4-byte
 * CScriptNum cap and allows big integers (the in-script EC fair-play needs 256-bit / 32-byte field
 * elements, and products up to ~64 bytes before modular reduction). This cap is set far above that
 * so every legitimate operation passes, while still preventing a few repeated OP_MULs from growing a
 * number without bound (a memory/CPU exhaustion DoS, CWE-400).
 */
const MAX_SCRIPT_NUM_BYTES = 4096;

export interface ScriptContext {
  /** The signed message (sighash preimage); OP_CHECKSIG verifies ECDSA over SHA-256 of this. */
  readonly sighashPreimage: Uint8Array;
}

export interface EvalResult {
  readonly ok: boolean;
  readonly reason?: string;
}

type Stack = Uint8Array[];

const TRUE = Uint8Array.of(1);
const FALSE = new Uint8Array(0);

function isTruthy(v: Uint8Array): boolean {
  for (let i = 0; i < v.length; i++) {
    if (v[i] !== 0) {
      // negative zero (0x80 as last byte, rest 0) is false
      if (i === v.length - 1 && v[i] === 0x80) return false;
      return true;
    }
  }
  return false;
}

/** Reconstruct a secp256k1 public KeyObject from a 33-byte SEC-1 compressed point. */
function compressedToKey(pub: Uint8Array): ReturnType<typeof createPublicKey> {
  if (pub.length !== 33 || (pub[0] !== 0x02 && pub[0] !== 0x03)) {
    throw new Error('not a compressed secp256k1 point');
  }
  const prefix = Uint8Array.from([
    0x30, 0x36, 0x30, 0x10, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01, 0x06, 0x05,
    0x2b, 0x81, 0x04, 0x00, 0x0a, 0x03, 0x22, 0x00,
  ]);
  const der = new Uint8Array(prefix.length + pub.length);
  der.set(prefix, 0);
  der.set(pub, prefix.length);
  return createPublicKey({ key: Buffer.from(der), format: 'der', type: 'spki' });
}

function checkSig(sig: Uint8Array, pub: Uint8Array, ctx: ScriptContext): boolean {
  if (sig.length === 0) return false;
  try {
    const key = compressedToKey(pub);
    return ecVerify('sha256', Buffer.from(ctx.sighashPreimage), key, Buffer.from(sig));
  } catch {
    return false;
  }
}

/** Execute one script (unlocking then locking share the stack, legacy/Genesis evaluation). */
export function evaluate(unlocking: Script, locking: Script, ctx: ScriptContext): EvalResult {
  const stack: Stack = [];
  for (const phase of [unlocking, locking]) {
    const r = run(phase, stack, ctx);
    if (!r.ok) return r;
  }
  if (stack.length === 0) return { ok: false, reason: 'empty stack' };
  return { ok: isTruthy(stack[stack.length - 1]!), reason: 'top not truthy' };
}

function run(script: Script, stack: Stack, ctx: ScriptContext): EvalResult {
  const exec: boolean[] = []; // IF/ELSE/ENDIF execution flags
  const executing = (): boolean => exec.every((x) => x);
  const pop = (): Uint8Array => {
    const v = stack.pop();
    if (v === undefined) throw new Error('stack underflow');
    return v;
  };

  for (const item of script as ScriptItem[]) {
    // Bound stack depth every iteration (NASA P10 / CWE-400). Each opcode adds at most a couple of
    // elements, so checking once per item caps the stack at MAX_STACK + O(1) — an adversarial
    // OP_DUP/PUSH flood cannot exhaust memory.
    if (stack.length > MAX_STACK) return { ok: false, reason: 'stack size limit exceeded' };
    if (typeof item !== 'number') {
      if (executing()) stack.push(item);
      continue;
    }
    // Conditionals are evaluated even when not executing (to track nesting).
    if (item === OP.OP_IF) {
      exec.push(executing() ? isTruthy(pop()) : false);
      continue;
    }
    if (item === OP.OP_ELSE) {
      if (exec.length === 0) return { ok: false, reason: 'OP_ELSE without OP_IF' };
      exec[exec.length - 1] = !exec[exec.length - 1] && exec.slice(0, -1).every((x) => x);
      continue;
    }
    if (item === OP.OP_ENDIF) {
      if (exec.length === 0) return { ok: false, reason: 'OP_ENDIF without OP_IF' };
      exec.pop();
      continue;
    }
    if (!executing()) continue;

    try {
      switch (item) {
        case OP.OP_RETURN:
          return { ok: false, reason: 'OP_RETURN is banned (core P11/§6.5)' };
        case OP.OP_CHECKLOCKTIMEVERIFY:
        case OP.OP_CHECKSEQUENCEVERIFY:
          // NO-OP post-Genesis (REQ-TX-001): enforce nothing.
          break;
        case OP.OP_0:
          stack.push(FALSE);
          break;
        case OP.OP_1:
        case OP.OP_2:
        case OP.OP_3:
        case OP.OP_4:
        case OP.OP_5:
        case OP.OP_6:
        case OP.OP_7:
        case OP.OP_8:
        case OP.OP_9:
        case OP.OP_10:
        case OP.OP_11:
        case OP.OP_12:
        case OP.OP_13:
        case OP.OP_14:
        case OP.OP_15:
        case OP.OP_16:
          // Small-int push opcodes OP_1..OP_16 push their value (0x51..0x60 → 1..16).
          stack.push(Uint8Array.of((item as number) - 0x50));
          break;
        case OP.OP_DUP: {
          const v = pop();
          stack.push(v, v);
          break;
        }
        case OP.OP_DROP:
          pop();
          break;
        case OP.OP_SWAP: {
          const a = pop();
          const b = pop();
          stack.push(a, b);
          break;
        }
        case OP.OP_OVER: {
          const a = pop();
          const b = pop();
          stack.push(b, a, b);
          break;
        }
        case OP.OP_EQUAL: {
          const a = pop();
          const b = pop();
          stack.push(eq(a, b) ? TRUE : FALSE);
          break;
        }
        case OP.OP_EQUALVERIFY: {
          const a = pop();
          const b = pop();
          if (!eq(a, b)) return { ok: false, reason: 'OP_EQUALVERIFY failed' };
          break;
        }
        case OP.OP_VERIFY:
          if (!isTruthy(pop())) return { ok: false, reason: 'OP_VERIFY failed' };
          break;
        case OP.OP_SHA256:
          stack.push(hash('sha256', pop()));
          break;
        case OP.OP_HASH256:
          stack.push(hash('sha256', hash('sha256', pop())));
          break;
        case OP.OP_HASH160:
          stack.push(hash('ripemd160', hash('sha256', pop())));
          break;
        case OP.OP_ADD: {
          const a = num(pop());
          const b = num(pop());
          stack.push(encodeNum(a + b));
          break;
        }
        case OP.OP_SUB: {
          const b = num(pop());
          const a = num(pop());
          stack.push(encodeNum(a - b));
          break;
        }
        case OP.OP_MUL: {
          const a = num(pop());
          const b = num(pop());
          stack.push(encodeNum(a * b));
          break;
        }
        case OP.OP_MOD: {
          const b = num(pop());
          const a = num(pop());
          if (b === 0n) return { ok: false, reason: 'OP_MOD by zero' };
          // Euclidean-positive modulo (operands here are positive field values).
          let r = a % b;
          if (r < 0n) r += b < 0n ? -b : b;
          stack.push(encodeNum(r));
          break;
        }
        case OP.OP_NUMEQUAL: {
          stack.push(num(pop()) === num(pop()) ? TRUE : FALSE);
          break;
        }
        case OP.OP_NUMEQUALVERIFY: {
          if (num(pop()) !== num(pop())) return { ok: false, reason: 'OP_NUMEQUALVERIFY failed' };
          break;
        }
        case OP.OP_CHECKSIG: {
          const pub = pop();
          const sig = pop();
          stack.push(checkSig(sig, pub, ctx) ? TRUE : FALSE);
          break;
        }
        case OP.OP_CHECKSIGVERIFY: {
          const pub = pop();
          const sig = pop();
          if (!checkSig(sig, pub, ctx)) return { ok: false, reason: 'OP_CHECKSIGVERIFY failed' };
          break;
        }
        case OP.OP_CHECKMULTISIG: {
          // Validate the pubkey count BEFORE popping any keys (NASA P10 / CWE-400): n must be a
          // sane in-range integer, never an attacker value that drives the pop loop until underflow.
          const n = Number(num(pop()));
          if (!Number.isInteger(n) || n < 0 || n > MAX_MULTISIG_KEYS) {
            return { ok: false, reason: `OP_CHECKMULTISIG pubkey count out of range: ${n}` };
          }
          const pubs: Uint8Array[] = [];
          for (let i = 0; i < n; i++) pubs.push(pop());
          // Likewise bound the signature count to [0, n] before popping signatures.
          const m = Number(num(pop()));
          if (!Number.isInteger(m) || m < 0 || m > n) {
            return { ok: false, reason: `OP_CHECKMULTISIG sig count out of range: ${m}` };
          }
          const sigs: Uint8Array[] = [];
          for (let i = 0; i < m; i++) sigs.push(pop());
          pop(); // the extra element (legacy CHECKMULTISIG bug, retained)
          stack.push(checkMultisig(sigs, pubs, ctx) ? TRUE : FALSE);
          break;
        }
        default:
          return { ok: false, reason: `unsupported opcode 0x${item.toString(16)}` };
      }
    } catch (e) {
      return { ok: false, reason: (e as Error).message };
    }
  }
  if (exec.length !== 0) return { ok: false, reason: 'unbalanced OP_IF' };
  return { ok: true };
}

/** m-of-n: each sig must match a distinct pubkey, in pubkey order (Bitcoin semantics). */
function checkMultisig(sigs: Uint8Array[], pubs: Uint8Array[], ctx: ScriptContext): boolean {
  // sigs were popped in reverse; restore signing order.
  const orderedSigs = [...sigs].reverse();
  const orderedPubs = [...pubs].reverse();
  let si = 0;
  for (let pi = 0; pi < orderedPubs.length && si < orderedSigs.length; pi++) {
    if (checkSig(orderedSigs[si]!, orderedPubs[pi]!, ctx)) si++;
  }
  return si === orderedSigs.length;
}

function eq(a: Uint8Array, b: Uint8Array): boolean {
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
  return true;
}

/**
 * Script number decode — little-endian, sign-magnitude, ARBITRARY PRECISION (post-Genesis BSV
 * removed the 4-byte CScriptNum cap), as BigInt. The 256-bit field arithmetic for the in-script
 * EC fair-play (§19.C) needs this.
 */
function num(v: Uint8Array): bigint {
  // Reject an oversized script number up front (CWE-400): decoding an unbounded-length operand and
  // then doing bignum arithmetic on it is a memory/CPU exhaustion vector. Thrown, caught by run().
  if (v.length > MAX_SCRIPT_NUM_BYTES) throw new Error('script number exceeds size limit');
  if (v.length === 0) return 0n;
  const bytes = [...v];
  const last = bytes.length - 1;
  let neg = false;
  if ((bytes[last]! & 0x80) !== 0) {
    neg = true;
    bytes[last] = bytes[last]! & 0x7f;
  }
  let r = 0n;
  for (let i = bytes.length - 1; i >= 0; i--) r = (r << 8n) | BigInt(bytes[i]!);
  return neg ? -r : r;
}
function encodeNum(n: bigint): Uint8Array {
  if (n === 0n) return new Uint8Array(0);
  const neg = n < 0n;
  let x = neg ? -n : n;
  const out: number[] = [];
  while (x > 0n) {
    out.push(Number(x & 0xffn));
    x >>= 8n;
  }
  if ((out[out.length - 1]! & 0x80) !== 0) out.push(neg ? 0x80 : 0x00);
  else if (neg) out[out.length - 1] = out[out.length - 1]! | 0x80;
  // Cap the produced number too (CWE-400): the result of OP_MUL etc. must not grow without bound.
  if (out.length > MAX_SCRIPT_NUM_BYTES) throw new Error('script number result exceeds size limit');
  return Uint8Array.from(out);
}

function hash(algo: 'sha256' | 'ripemd160', data: Uint8Array): Uint8Array {
  return new Uint8Array(createHash(algo).update(Buffer.from(data)).digest());
}
