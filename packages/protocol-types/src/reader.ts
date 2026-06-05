/**
 * ByteReader — a bounds-checked, non-throwing cursor over an immutable byte buffer.
 *
 * ============================================================================================
 * WHAT
 * ============================================================================================
 * The exact inverse of {@link ByteWriter}. It reads little-endian fixed-width integers (u8/u16/
 * u32/u64) and length-delimited byte runs from a buffer, advancing an internal cursor. It is the
 * single low-level primitive every wire/transaction/script parser in this codebase is built on.
 *
 * ============================================================================================
 * HOW
 * ============================================================================================
 * Each read first checks that enough bytes REMAIN before touching the buffer. If they do not, the
 * read returns `null` (for value reads) or `null` (for byte-run reads) and DOES NOT advance the
 * cursor — there is no way to make it read past the end. u64 is returned as a bigint so values
 * above 2^53 are never silently truncated (CWE-190). Multi-byte integers are assembled with
 * explicit shifts/`BigInt` arithmetic, not `DataView`, so behaviour is identical in every JS engine
 * and there is no implicit endianness or alignment surprise.
 *
 * ============================================================================================
 * WHY (and why THIS design rather than the alternatives)
 * ============================================================================================
 * A transaction/script parser consumes ATTACKER-CONTROLLED bytes. The single most common parser
 * defect class is an unchecked index/length read driven by an attacker-supplied length field
 * (CWE-125 out-of-bounds read, CWE-129 improper validation of array index). Centralising EVERY read
 * behind one primitive that cannot read out of bounds makes that whole defect class impossible by
 * construction, instead of relying on each call site to remember a bounds check.
 *
 * WHY null-returning reads instead of throwing: the NASA/JPL Power-of-10 rule is "check the return
 * value of every non-void function". A throwing reader hides control flow and tempts a caller into a
 * broad try/catch that also swallows unrelated bugs. A `null` return forces the caller to handle the
 * short-read at the exact call site, in plain sight of an auditor, with no hidden unwinding. The
 * cost is verbosity; for reference cryptographic infrastructure that is the correct trade.
 *
 * WHY a class with a private cursor rather than threading an offset through pure functions: the
 * cursor must only ever move forward and only ever by the number of bytes actually consumed. Making
 * the cursor private and advancing it in exactly one place per read removes the "forgot to advance"
 * and "advanced twice" bug classes that an explicit-offset style invites.
 *
 * ============================================================================================
 * SECURITY BOUNDARY
 * ============================================================================================
 *   trusted inputs:    none. The constructor's buffer is treated as hostile.
 *   untrusted inputs:  the entire buffer and every length implied by its contents.
 *   recoverable errors: a short read (not enough bytes) — surfaced as `null`, never an exception.
 *   fatal errors:      none. No input can make a ByteReader method throw or read OOB (proven by the
 *                      fuzz tests in protocol-types/test/reader.test.ts).
 *   side effects:      advances the private cursor on a SUCCESSFUL read only; a failed read leaves
 *                      the cursor unchanged so the caller can report the exact failure offset.
 *   state mutation:    cursor only; the underlying buffer is never mutated (reads return COPIES for
 *                      byte runs, so a caller cannot alias and mutate internal state).
 *
 * ============================================================================================
 * WHAT MUST NEVER BE ASSUMED
 * ============================================================================================
 *   - never assume a value read "succeeded": a `null` means insufficient bytes and MUST be handled;
 *   - never assume `remaining()` stays valid after a read — re-read it;
 *   - never assume a returned byte run aliases the buffer — it is a copy and is safe to keep;
 *   - never use this to read SECRETS for comparison — comparison must be constant-time (safe.ts).
 *
 * WHAT BREAKS IF THE RULE IS VIOLATED
 * ============================================================================================
 *   Ignoring a `null` return and using a default (e.g. treating a short u32 as 0) reintroduces the
 *   exact silent-misparse class this primitive exists to remove, and can desynchronise a parser
 *   from the true byte boundaries — the door to OOB reads and consensus divergence.
 */
export class ByteReader {
  private readonly buf: Uint8Array;
  private cursor = 0;

  constructor(buf: Uint8Array) {
    // Precondition assertions (NASA P10: >=2 assertions/function). A non-Uint8Array buffer is a
    // programming error at the call site, not hostile input, so it is surfaced loudly.
    if (!(buf instanceof Uint8Array)) throw new TypeError('ByteReader: buffer must be a Uint8Array');
    this.buf = buf;
  }

  /** Current cursor position (number of bytes already consumed). */
  get offset(): number {
    return this.cursor;
  }

  /** Bytes left to read from the cursor to the end of the buffer. */
  get remaining(): number {
    return this.buf.length - this.cursor;
  }

  /** True once every byte has been consumed (used to reject trailing data). */
  get atEnd(): boolean {
    return this.cursor >= this.buf.length;
  }

  /** Read exactly `n` bytes as a COPY, or null if fewer than `n` remain. `n` must be a non-negative integer. */
  tryReadBytes(n: number): Uint8Array | null {
    if (!Number.isInteger(n) || n < 0) return null; // defensive: never honour a bogus length
    if (n > this.remaining) return null; // short read — do NOT advance, do NOT touch the buffer
    const out = this.buf.slice(this.cursor, this.cursor + n); // slice() copies; no aliasing
    this.cursor += n;
    return out;
  }

  /** Read one unsigned byte, or null if none remain. */
  tryReadU8(): number | null {
    if (this.remaining < 1) return null;
    const v = this.buf[this.cursor]!;
    this.cursor += 1;
    return v;
  }

  /** Read a little-endian unsigned 16-bit integer, or null if fewer than 2 bytes remain. */
  tryReadU16LE(): number | null {
    if (this.remaining < 2) return null;
    const p = this.cursor;
    const v = this.buf[p]! | (this.buf[p + 1]! << 8);
    this.cursor += 2;
    return v >>> 0;
  }

  /** Read a little-endian unsigned 32-bit integer, or null if fewer than 4 bytes remain. */
  tryReadU32LE(): number | null {
    if (this.remaining < 4) return null;
    const p = this.cursor;
    // `>>> 0` forces an unsigned 32-bit result (the high bit must not be read as a sign).
    const v = (this.buf[p]! | (this.buf[p + 1]! << 8) | (this.buf[p + 2]! << 16) | (this.buf[p + 3]! << 24)) >>> 0;
    this.cursor += 4;
    return v;
  }

  /**
   * Read a little-endian unsigned 64-bit integer as a bigint, or null if fewer than 8 bytes remain.
   * Returned as bigint specifically so satoshi values above 2^53 are exact, never truncated
   * (CWE-190). The arithmetic is plain shifts on BigInt — no Number ever holds the full value.
   */
  tryReadU64LE(): bigint | null {
    if (this.remaining < 8) return null;
    const p = this.cursor;
    let v = 0n;
    for (let i = 7; i >= 0; i--) {
      v = (v << 8n) | BigInt(this.buf[p + i]!);
    }
    this.cursor += 8;
    return v;
  }
}
