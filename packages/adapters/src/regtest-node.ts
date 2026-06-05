/**
 * RegtestNode — the project's OWN in-tree BSV regtest node. STANDALONE: no external process, no
 * separate project, no network dependency. It reuses only this repository's hardened components —
 * the transaction parser (`parseTxWire`), the script deserializer (`deserializeScript`), the real
 * Script interpreter (`evaluate`), the BIP-143 sighash (`sighashMessage`), and the canonical
 * SHA-256 — to validate spends exactly as a node would.
 *
 * ============================================================================================
 * WHY THIS EXISTS
 * ============================================================================================
 * The project must be standalone: every part builds and runs on in-tree code and never relies on an
 * external system. The on-chain end-to-end tests therefore run against THIS node, in-process, instead
 * of an external daemon. Because we own the node, the consensus rules an absent-player bond
 * forfeiture depends on — chiefly **nLockTime finality** — are actually ENFORCED here and proven by
 * the e2e (no "the production node would do it" disclaimers).
 *
 * ============================================================================================
 * WHAT IT ENFORCES (consensus-faithful)
 * ============================================================================================
 *  - every input resolves to an unspent output, runs through the REAL interpreter over the BIP-143
 *    sighash, and a failing script is rejected (no spend without a valid unlock);
 *  - sum(inputs) >= sum(outputs) (a tx cannot create value);
 *  - **nLockTime finality** (`IsFinalTx`): a transaction with a future nLockTime and any non-final
 *    input (`nSequence != 0xffffffff`) is REJECTED from the mempool until its locktime is reached —
 *    this is the maturity gate the bond FORFEIT branch relies on;
 *  - **sequence replacement** (original-protocol rule): a tx whose conflicting inputs all carry a
 *    strictly higher nSequence than an existing mempool tx replaces it — what the cooperative
 *    settlement uses to supersede a pre-broadcast timeout-default;
 *  - double-spend rejection (a confirmed-spent outpoint is simply absent from the UTXO set).
 *
 * ============================================================================================
 * SECURITY BOUNDARY
 * ============================================================================================
 *   trusted inputs:  none — `submitTx` bytes are hostile and flow through the hardened parsers
 *                    (`parseTxWire`, `deserializeScript`) which never throw / never OOB-read.
 *   recoverable errors: any malformed/invalid/non-final/under-funded/conflicting tx → `{ ok:false,
 *                    reason }`; the node keeps running and its UTXO set is unchanged on rejection.
 *   fatal errors:    none (no input panics the node).
 *   side effects:    only a SUCCESSFUL submit mutates the mempool; only `generateBlock` mutates the
 *                    confirmed UTXO set. A rejected tx mutates nothing.
 *
 * Regtest simplifications (documented, not hidden): coinbase maturity is not enforced (regtest
 * convenience, matching the existing on-chain e2es), and nLockTime time-based locktimes use the
 * wall clock. Neither affects the height-based maturity gate the forfeiture relies on.
 */

import { ByteWriter, bytesToHex, hash256, tryHexToBytes } from '@bsv-poker/protocol-types';
import {
  OP,
  evaluate,
  deserializeScript,
  serializeScript,
  fairPlayCommitment,
  type Script,
} from '@bsv-poker/script-templates-ts';
import { parseTxWire, sighashMessage, type Tx, type TxOutput, type ParsedTx } from '@bsv-poker/tx-builder';

const SUBSIDY = 5_000_000_000;
const SEQUENCE_FINAL = 0xffffffff;
const LOCKTIME_THRESHOLD = 500_000_000; // < this → block-height locktime; >= → unix-time locktime

/** A confirmed unspent output: its value and its locking script (raw wire bytes). */
interface Utxo {
  readonly value: number;
  readonly script: Uint8Array;
}

interface MempoolEntry {
  readonly txid: string;
  readonly spends: readonly string[]; // outpoint keys this tx consumes
  readonly outputs: readonly Utxo[]; // outputs it creates (added to the set on confirmation)
  readonly seqByInput: ReadonlyMap<string, number>;
}

/** Submit result, shaped identically to the previous external node client. */
export interface SubmitResult {
  readonly ok: boolean;
  readonly reason: string;
  readonly txid: string;
}

const outpointKey = (txid: string, vout: number): string => `${txid}:${vout}`;

/**
 * P2PKH locking script to a compressed pubkey (HASH160 = RIPEMD160(SHA256(pub))). Exported so tests
 * and on-chain e2es derive the IDENTICAL coinbase/output script and matching sighash scriptCode.
 */
export function p2pkhScript(pub: Uint8Array): Script {
  return [OP.OP_DUP, OP.OP_HASH160, fairPlayCommitment(pub), OP.OP_EQUALVERIFY, OP.OP_CHECKSIG];
}

/** Display (big-endian) txid hex of raw transaction bytes (double-SHA256, reversed). */
function rawTxid(raw: Uint8Array): string {
  return bytesToHex(Uint8Array.from([...hash256(raw)].reverse()));
}

export class RegtestNode {
  private readonly utxos = new Map<string, Utxo>();
  private mempool: MempoolEntry[] = [];
  private readonly spentByMempool = new Map<string, string>();
  private h = 0;

  // ----- the interface the e2es use (async, drop-in for the prior node client) -----

  async ping(): Promise<boolean> {
    return true;
  }

  async height(): Promise<number> {
    return this.h;
  }

  /** Mine a block: confirm the mempool, then add a fresh coinbase paying the subsidy to `payoutPubHex`. */
  async generateBlock(payoutPubHex: string): Promise<{ blockHash: string; txs: number; coinbaseTxid: string }> {
    const minedFromMempool = this.mempool.length;
    // Confirm every mempool tx: remove the outpoints it spends, add the outputs it creates.
    for (const e of this.mempool) {
      for (const s of e.spends) this.utxos.delete(s);
      e.outputs.forEach((o, vout) => this.utxos.set(outpointKey(e.txid, vout), o));
    }
    this.mempool = [];
    this.spentByMempool.clear();

    this.h += 1;
    const pub = tryHexToBytes(payoutPubHex);
    if (pub === null) throw new Error('generateBlock: payout pubkey is not valid hex');
    const { txid, script, value } = this.buildCoinbase(pub, this.h);
    this.utxos.set(outpointKey(txid, 0), { value, script });
    return { blockHash: bytesToHex(hash256(new TextEncoder().encode(`block:${this.h}`))), txs: minedFromMempool + 1, coinbaseTxid: txid };
  }

  /** Validate a raw transaction and admit it to the mempool (consensus rules above). */
  async submitTx(rawTxHex: string): Promise<SubmitResult> {
    const raw = tryHexToBytes(rawTxHex);
    if (raw === null) return { ok: false, reason: 'transaction is not valid hex', txid: '' };
    const parsed = parseTxWire(raw);
    if (!parsed.ok) return { ok: false, reason: `malformed tx: ${parsed.reason}`, txid: '' };
    const txid = rawTxid(raw);
    // A non-coinbase transaction must spend at least one input and create at least one output;
    // an input-less tx would create value from nothing (only a coinbase has no inputs, and a
    // coinbase is never admitted to the mempool).
    if (parsed.tx.inputs.length === 0) return { ok: false, reason: 'transaction has no inputs', txid };
    if (parsed.tx.outputs.length === 0) return { ok: false, reason: 'transaction has no outputs', txid };
    const tx = this.toTx(parsed.tx);
    if (tx === null) return { ok: false, reason: 'tx output script malformed', txid };

    if (txid in Object.fromEntries(this.mempool.map((e) => [e.txid, true]))) {
      return { ok: true, reason: 'already in mempool', txid };
    }

    // nLockTime finality (the maturity gate). A future-locktime tx with any non-final input is not
    // admissible until its locktime is reached (height-based: becomes final when it could be mined in
    // the NEXT block, i.e. threshold = current height + 1).
    if (!this.isFinalTx(parsed.tx, this.h + 1)) {
      return { ok: false, reason: 'non-final: nLockTime not reached (premature)', txid };
    }

    // Resolve every input against the confirmed UTXO set.
    const resolved: { key: string; utxo: Utxo }[] = [];
    for (let i = 0; i < parsed.tx.inputs.length; i++) {
      const tin = parsed.tx.inputs[i]!;
      const key = outpointKey(tin.prevTxid, tin.vout);
      const utxo = this.utxos.get(key);
      if (utxo === undefined) return { ok: false, reason: `input ${i}: UTXO not found`, txid };
      resolved.push({ key, utxo });
    }

    // Conflict / replacement: a mempool tx already spending one of our outpoints.
    const conflicts = new Set<string>();
    for (const { key } of resolved) {
      const owner = this.spentByMempool.get(key);
      if (owner !== undefined && owner !== txid) conflicts.add(owner);
    }
    if (conflicts.size > 1) return { ok: false, reason: 'multi-conflict replacement not accepted', txid };
    let replaces: string | null = null;
    if (conflicts.size === 1) {
      replaces = [...conflicts][0]!;
      const old = this.mempool.find((e) => e.txid === replaces)!;
      for (const tin of parsed.tx.inputs) {
        const key = outpointKey(tin.prevTxid, tin.vout);
        const oldSeq = old.seqByInput.get(key);
        if (oldSeq !== undefined && tin.sequence <= oldSeq) {
          return { ok: false, reason: `replacement rejected: seq ${tin.sequence} <= existing ${oldSeq}`, txid };
        }
      }
    }

    // Script verification through the REAL interpreter + value conservation.
    let totalIn = 0;
    for (let i = 0; i < parsed.tx.inputs.length; i++) {
      const { utxo } = resolved[i]!;
      const tin = parsed.tx.inputs[i]!;
      const code = deserializeScript(utxo.script);
      const sig = deserializeScript(tin.scriptSig);
      if (!code.ok || !sig.ok) return { ok: false, reason: `input ${i}: script bytes malformed`, txid };
      const preimage = sighashMessage(tx, i, code.script, utxo.value);
      const r = evaluate(sig.script, code.script, { sighashPreimage: preimage });
      if (!r.ok) return { ok: false, reason: `input ${i}: script rejected: ${r.reason}`, txid };
      totalIn += utxo.value;
    }
    const totalOut = tx.outputs.reduce((s, o) => s + o.satoshis, 0);
    if (totalIn < totalOut) return { ok: false, reason: `inputs ${totalIn} < outputs ${totalOut}`, txid };

    // Admit (evicting the replaced tx, if any).
    if (replaces !== null) this.evict(replaces);
    const seqByInput = new Map<string, number>();
    for (const tin of parsed.tx.inputs) seqByInput.set(outpointKey(tin.prevTxid, tin.vout), tin.sequence);
    const outputs: Utxo[] = parsed.tx.outputs.map((o) => ({ value: Number(o.satoshis), script: o.script }));
    this.mempool.push({ txid, spends: resolved.map((r) => r.key), outputs, seqByInput });
    for (const { key } of resolved) this.spentByMempool.set(key, txid);
    return { ok: true, reason: '', txid };
  }

  /** Unspent status of an outpoint (confirmed UTXO set, minus anything a mempool tx is spending). */
  async outpointStatus(txidHex: string, vout: number): Promise<{ unspent: boolean; value: number }> {
    const key = outpointKey(txidHex, vout);
    const u = this.utxos.get(key);
    if (u !== undefined && !this.spentByMempool.has(key)) return { unspent: true, value: u.value };
    return { unspent: false, value: u?.value ?? 0 };
  }

  async utxoCount(): Promise<number> {
    return this.utxos.size;
  }

  async status(): Promise<{ ok: boolean; height: number; mempool: number }> {
    return { ok: true, height: this.h, mempool: this.mempool.length };
  }

  async shutdown(): Promise<void> {
    /* in-process — nothing to tear down */
  }

  // ----- internals -----

  private evict(txid: string): void {
    const e = this.mempool.find((x) => x.txid === txid);
    if (!e) return;
    this.mempool = this.mempool.filter((x) => x.txid !== txid);
    for (const key of e.spends) if (this.spentByMempool.get(key) === txid) this.spentByMempool.delete(key);
  }

  /** IsFinalTx (Bitcoin consensus): final iff locktime is satisfied OR every input is final. */
  private isFinalTx(tx: ParsedTx, blockHeight: number): boolean {
    if (tx.nLockTime === 0) return true;
    const threshold = tx.nLockTime < LOCKTIME_THRESHOLD ? blockHeight : Math.floor(Date.now() / 1000);
    if (tx.nLockTime < threshold) return true;
    for (const tin of tx.inputs) if (tin.sequence !== SEQUENCE_FINAL) return false;
    return true;
  }

  /** Convert a parsed tx into the tx-builder `Tx` shape the sighash needs. */
  private toTx(p: ParsedTx): Tx | null {
    const outputs: TxOutput[] = [];
    for (const o of p.outputs) {
      if (o.satoshis > BigInt(Number.MAX_SAFE_INTEGER)) return null;
      const s = deserializeScript(o.script);
      if (!s.ok) return null;
      outputs.push({ satoshis: Number(o.satoshis), locking: s.script });
    }
    return {
      version: p.version,
      inputs: p.inputs.map((i) => ({ prevTxid: i.prevTxid, vout: i.vout, sequence: i.sequence })),
      outputs,
      nLockTime: p.nLockTime,
    };
  }

  /** Build a unique coinbase paying the subsidy as P2PKH to `pub`; height makes the txid unique. */
  private buildCoinbase(pub: Uint8Array, height: number): { txid: string; script: Uint8Array; value: number } {
    const lockingBytes = serializeScript(p2pkhScript(pub));
    // Minimal coinbase wire bytes: version, 1 input (null prevout, height in scriptSig for uniqueness),
    // 1 output (subsidy + p2pkh), nLockTime 0.
    const w = new ByteWriter();
    w.u32(1); // version
    w.u8(1); // vin count
    for (let i = 0; i < 32; i++) w.u8(0); // null prev txid
    w.u32(0xffffffff); // null prev vout
    const heightBytes = new ByteWriter().u32(height).toBytes(); // BIP-34-style unique tag
    w.u8(heightBytes.length);
    for (const b of heightBytes) w.u8(b);
    w.u32(0xffffffff); // sequence
    w.u8(1); // vout count
    w.u64(SUBSIDY);
    w.u8(lockingBytes.length);
    for (const b of lockingBytes) w.u8(b);
    w.u32(0); // nLockTime
    const raw = w.toBytes();
    return { txid: rawTxid(raw), script: lockingBytes, value: SUBSIDY };
  }
}
