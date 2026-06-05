/**
 * parseTxWire — a hardened parser for raw BSV transactions (the inverse of {@link serializeTxWire}).
 *
 * THIS FILE IS THE WORKED REFERENCE for how a hostile-input parser is written in this codebase.
 * Everything that consumes attacker-controlled bytes should be documented and tested to this bar.
 *
 * ============================================================================================
 * WHAT
 * ============================================================================================
 * Decodes the canonical Bitcoin/BSV transaction wire format into a {@link ParsedTx}:
 *
 *     version : u32 LE
 *     vin     : CompactSize count, then for each input:
 *                 prevTxid : 32 bytes (little-endian on the wire; exposed as big-endian display hex)
 *                 vout     : u32 LE
 *                 scriptSig: CompactSize length, then that many bytes (returned raw)
 *                 sequence : u32 LE
 *     vout    : CompactSize count, then for each output:
 *                 value    : u64 LE (satoshis; returned as bigint)
 *                 script   : CompactSize length, then that many bytes (returned raw)
 *     nLockTime: u32 LE
 *
 * It returns a discriminated result — `{ ok: true, tx }` or `{ ok: false, reason, offset }` — and
 * NEVER throws on hostile input. Scripts are returned as raw bytes; turning them into operations is
 * the interpreter's job, not the parser's (see WHY NOT below).
 *
 * ============================================================================================
 * HOW
 * ============================================================================================
 * Reads flow through a single {@link ByteReader}, whose every read is bounds-checked and returns
 * `null` on a short read. After each read we check for `null` and, if so, return a precise failure
 * with the byte offset at which parsing stopped. CompactSize integers are required to be MINIMALLY
 * encoded (the shortest form for their value); a non-minimal encoding is rejected as malformed.
 * After the final field we require the reader to be exactly at the end of the buffer, so trailing
 * bytes are rejected. The total input length is bounded before parsing begins.
 *
 * ============================================================================================
 * WHY (and why THIS design rather than the alternatives)
 * ============================================================================================
 * A transaction arrives from the network, from a node RPC, from a file, or pasted by a user. Every
 * one of those is hostile until proven otherwise. The historical Bitcoin parser bugs cluster around
 * three things this parser closes by construction:
 *   1. attacker-driven length fields (a huge vin/vout/script length) → handled by bounding every
 *      read against the remaining buffer and bounding counts by the buffer length itself, so the
 *      parse loop has a provable upper bound (NASA P10) and never over-allocates;
 *   2. integer truncation of the 8-byte value field → handled by carrying satoshis as a bigint, so a
 *      value above 2^53 is exact (CWE-190); a truncated value could make a client mis-read how much
 *      money an output controls;
 *   3. transaction malleability via non-canonical encodings → handled by REQUIRING minimal
 *      CompactSize and REJECTING trailing bytes, so two distinct byte strings cannot both parse to
 *      the same transaction (a malleability/dup-txid vector).
 *
 * WHY a discriminated result instead of throwing: a parser must never let a malformed input abort
 * the caller via an uncaught exception (SANS/CWE: "reject malformed input WITHOUT panicking"). A
 * `{ ok:false, reason, offset }` makes the failure a value the caller MUST handle, and the offset
 * makes a rejection auditable and reproducible.
 *
 * WHY NOT also parse scripts into ops here: a parser's attack surface should be as small as
 * possible. Decoding pushdata/opcodes is a second, larger grammar with its own failure modes; that
 * lives behind the interpreter, which already validates every opcode before execution. Keeping the
 * tx parser to the transaction envelope and returning raw script bytes minimises this module's
 * surface and keeps the two grammars independently auditable.
 *
 * WHY NOT accept BTC's segwit marker (a 0x00 vin count followed by a flag): BSV has no segwit. A
 * 0x00 first count byte here means EXACTLY "zero inputs", never a marker. Treating it as a marker
 * would be a parser ambiguity an attacker could exploit to make one byte string mean two things.
 *
 * ============================================================================================
 * SECURITY BOUNDARY
 * ============================================================================================
 *   trusted inputs:    none.
 *   untrusted inputs:  the entire input buffer and every count/length it implies.
 *   recoverable errors: malformed/oversize/truncated/non-canonical input → `{ ok:false, ... }`. The
 *                      caller MUST fail closed (reject the transaction); a false result is never a
 *                      partially-parsed transaction.
 *   fatal errors:      none. No input causes a throw or an OOB read (proven by the fuzz test).
 *   rejection conditions (each is a named, tested negative case):
 *                      oversize buffer; truncated version/outpoint/value/sequence/locktime; short
 *                      script run; non-minimal CompactSize; count exceeding the remaining bytes;
 *                      trailing bytes after nLockTime; non-Uint8Array input.
 *   side effects:      none. Parsing is pure: input bytes in, a value out. No I/O, no globals.
 *   state mutation:    none beyond the local ByteReader cursor.
 *   rollback behaviour: not applicable — there is no externally visible state to roll back.
 *   audit evidence:    the `offset` in a failure result pinpoints where a rejected input broke.
 *
 * ============================================================================================
 * WHAT MUST NEVER BE ASSUMED
 * ============================================================================================
 *   - never assume `ok` is true without checking it;
 *   - never treat satoshis as a Number — it is a bigint to stay exact;
 *   - never re-serialize a parsed tx and assume the bytes equal the input unless the input itself
 *     was canonical — that is exactly what this parser enforces, and the identity property is tested;
 *   - never feed the raw script bytes to anything that has not itself validated them.
 *
 * WHAT BREAKS IF THE RULE IS VIOLATED
 * ============================================================================================
 *   Dropping the minimal-CompactSize or no-trailing-bytes checks reopens malleability: an attacker
 *   re-encodes a transaction to a different byte string with the same meaning, breaking any system
 *   that keys on the raw bytes. Truncating the value field can make a wallet under- or over-count an
 *   output and strand or misdirect funds.
 */

import { ByteReader, ByteWriter, bytesToHex, hexToBytes } from '@bsv-poker/protocol-types';

/** One parsed transaction input. Scripts are RAW bytes (see WHY NOT in the file header). */
export interface ParsedTxInput {
  /** Previous txid as big-endian DISPLAY hex (64 chars) — the wire form is little-endian. */
  readonly prevTxid: string;
  /** Previous output index (u32). */
  readonly vout: number;
  /** Unlocking script, raw bytes (never interpreted here). */
  readonly scriptSig: Uint8Array;
  /** Input sequence number (u32). */
  readonly sequence: number;
}

/** One parsed transaction output. */
export interface ParsedTxOutput {
  /** Value in satoshis as a bigint — exact for the full u64 range (never truncated). */
  readonly satoshis: bigint;
  /** Locking script, raw bytes (never interpreted here). */
  readonly script: Uint8Array;
}

/** A structurally parsed transaction. `byteLength` equals the input length on success. */
export interface ParsedTx {
  readonly version: number;
  readonly inputs: readonly ParsedTxInput[];
  readonly outputs: readonly ParsedTxOutput[];
  readonly nLockTime: number;
  readonly byteLength: number;
}

/** Discriminated parse result. A failure carries a human reason and the byte offset of the stop. */
export type TxParseResult =
  | { readonly ok: true; readonly tx: ParsedTx }
  | { readonly ok: false; readonly reason: string; readonly offset: number };

/** Options for {@link parseTxWire}. */
export interface ParseOptions {
  /**
   * Maximum accepted transaction size in bytes (attack-surface bound, CWE-400). Default 10 MiB —
   * larger than any transaction this application produces, small enough to bound work. A caller in a
   * tighter context (e.g. parsing a single funding tx) should pass a smaller bound.
   */
  readonly maxBytes?: number;
}

const DEFAULT_MAX_TX_BYTES = 10 * 1024 * 1024;

/** CompactSize upper bound we will represent as a Number after a remaining-bytes check (well < 2^53). */
type VarIntResult = { readonly value: number } | { readonly error: string };

/**
 * Read a MINIMALLY-encoded CompactSize integer at the cursor. Returns the value (as a Number, which
 * is safe because callers immediately bound it by the remaining buffer length, itself < 2^53) or a
 * specific error. Rejects non-minimal encodings, which are a malleability vector.
 */
function readMinimalVarInt(r: ByteReader): VarIntResult {
  const prefix = r.tryReadU8();
  if (prefix === null) return { error: 'truncated CompactSize prefix' };
  if (prefix < 0xfd) return { value: prefix }; // single-byte form is always minimal
  if (prefix === 0xfd) {
    const v = r.tryReadU16LE();
    if (v === null) return { error: 'truncated 0xfd CompactSize' };
    if (v < 0xfd) return { error: 'non-minimal 0xfd CompactSize' };
    return { value: v };
  }
  if (prefix === 0xfe) {
    const v = r.tryReadU32LE();
    if (v === null) return { error: 'truncated 0xfe CompactSize' };
    if (v <= 0xffff) return { error: 'non-minimal 0xfe CompactSize' };
    return { value: v };
  }
  // prefix === 0xff → 8-byte form
  const v = r.tryReadU64LE();
  if (v === null) return { error: 'truncated 0xff CompactSize' };
  if (v <= 0xffffffffn) return { error: 'non-minimal 0xff CompactSize' };
  // A genuine count/length this large cannot be satisfied by any buffer we accept; the caller's
  // remaining-bytes check will reject it. Convert safely only after that guard, so cap here.
  if (v > BigInt(Number.MAX_SAFE_INTEGER)) return { error: 'CompactSize exceeds safe integer range' };
  return { value: Number(v) };
}

/**
 * Parse a raw transaction. Returns a discriminated result and never throws on hostile input.
 *
 * @param bytes the raw transaction bytes (hostile until validated here)
 * @param opts  optional size bound
 */
export function parseTxWire(bytes: Uint8Array, opts: ParseOptions = {}): TxParseResult {
  const maxBytes = opts.maxBytes ?? DEFAULT_MAX_TX_BYTES;
  // Boundary assertions (NASA P10). A non-Uint8Array is a caller error, surfaced as a clean failure
  // rather than a thrown TypeError, so even a misuse fails closed instead of crashing the caller.
  if (!(bytes instanceof Uint8Array)) return { ok: false, reason: 'input is not a Uint8Array', offset: 0 };
  if (bytes.length > maxBytes) return { ok: false, reason: `oversize: ${bytes.length} > ${maxBytes}`, offset: 0 };
  if (bytes.length < 10) return { ok: false, reason: 'too short to be a transaction', offset: 0 };

  const r = new ByteReader(bytes);

  const version = r.tryReadU32LE();
  if (version === null) return fail(r, 'truncated version');

  // --- inputs ---------------------------------------------------------------
  const vinCount = readMinimalVarInt(r);
  if ('error' in vinCount) return fail(r, vinCount.error);
  // A count cannot exceed the bytes that remain (each input is >= 41 bytes, but >= 1 is enough to
  // make the loop bound provable and to reject an absurd count without doing the work).
  if (vinCount.value > r.remaining) return fail(r, `vin count ${vinCount.value} exceeds remaining bytes`);
  const inputs: ParsedTxInput[] = [];
  for (let i = 0; i < vinCount.value; i++) {
    const prev = r.tryReadBytes(32);
    if (prev === null) return fail(r, `truncated prevTxid for input ${i}`);
    const vout = r.tryReadU32LE();
    if (vout === null) return fail(r, `truncated vout for input ${i}`);
    const scriptLen = readMinimalVarInt(r);
    if ('error' in scriptLen) return fail(r, `${scriptLen.error} (scriptSig len, input ${i})`);
    if (scriptLen.value > r.remaining) return fail(r, `scriptSig length ${scriptLen.value} exceeds remaining bytes (input ${i})`);
    const scriptSig = r.tryReadBytes(scriptLen.value);
    if (scriptSig === null) return fail(r, `truncated scriptSig for input ${i}`);
    const sequence = r.tryReadU32LE();
    if (sequence === null) return fail(r, `truncated sequence for input ${i}`);
    // prevTxid is little-endian on the wire; expose big-endian display hex (reverse the 32 bytes).
    const display = bytesToHex(Uint8Array.from([...prev].reverse()));
    inputs.push({ prevTxid: display, vout, scriptSig, sequence });
  }

  // --- outputs --------------------------------------------------------------
  const voutCount = readMinimalVarInt(r);
  if ('error' in voutCount) return fail(r, voutCount.error);
  if (voutCount.value > r.remaining) return fail(r, `vout count ${voutCount.value} exceeds remaining bytes`);
  const outputs: ParsedTxOutput[] = [];
  for (let i = 0; i < voutCount.value; i++) {
    const satoshis = r.tryReadU64LE();
    if (satoshis === null) return fail(r, `truncated value for output ${i}`);
    const scriptLen = readMinimalVarInt(r);
    if ('error' in scriptLen) return fail(r, `${scriptLen.error} (script len, output ${i})`);
    if (scriptLen.value > r.remaining) return fail(r, `script length ${scriptLen.value} exceeds remaining bytes (output ${i})`);
    const script = r.tryReadBytes(scriptLen.value);
    if (script === null) return fail(r, `truncated script for output ${i}`);
    outputs.push({ satoshis, script });
  }

  // --- locktime + no trailing data -----------------------------------------
  const nLockTime = r.tryReadU32LE();
  if (nLockTime === null) return fail(r, 'truncated nLockTime');
  if (!r.atEnd) return fail(r, `trailing bytes after nLockTime (${r.remaining} extra)`);

  return { ok: true, tx: { version, inputs, outputs, nLockTime, byteLength: r.offset } };
}

/** Build a failure result carrying the reader's current offset (where parsing stopped). */
function fail(r: ByteReader, reason: string): TxParseResult {
  return { ok: false, reason, offset: r.offset };
}

/** Emit a MINIMALLY-encoded CompactSize (the exact form {@link readMinimalVarInt} accepts). */
function writeMinimalVarInt(w: ByteWriter, n: number): void {
  if (!Number.isInteger(n) || n < 0) throw new RangeError(`varint: ${n}`);
  if (n < 0xfd) w.u8(n);
  else if (n <= 0xffff) w.u8(0xfd).u16(n);
  else if (n <= 0xffffffff) w.u8(0xfe).u32(n);
  else w.u8(0xff).u64(BigInt(n));
}

/**
 * Serialize a {@link ParsedTx} back to canonical wire bytes. Provided so the identity invariant
 *   parseTxWire(serializeParsedTx(tx)) deep-equals tx,  and
 *   serializeParsedTx(parseTxWire(canonicalBytes).tx) equals canonicalBytes
 * is EXECUTABLE as a test (see parse.test.ts). Because it always emits minimal CompactSize and no
 * trailing bytes, its output is exactly what the parser accepts — round-tripping is total on valid,
 * canonical transactions and is the operational proof that the parser is faithful, not lossy.
 */
export function serializeParsedTx(tx: ParsedTx): Uint8Array {
  const w = new ByteWriter();
  w.u32(tx.version);
  writeMinimalVarInt(w, tx.inputs.length);
  for (const i of tx.inputs) {
    // display (big-endian) hex back to little-endian wire bytes
    const le = [...hexToBytes(i.prevTxid)].reverse();
    for (const b of le) w.u8(b);
    w.u32(i.vout);
    writeMinimalVarInt(w, i.scriptSig.length);
    for (const b of i.scriptSig) w.u8(b);
    w.u32(i.sequence);
  }
  writeMinimalVarInt(w, tx.outputs.length);
  for (const o of tx.outputs) {
    w.u64(o.satoshis);
    writeMinimalVarInt(w, o.script.length);
    for (const b of o.script) w.u8(b);
  }
  w.u32(tx.nLockTime);
  return w.toBytes();
}
