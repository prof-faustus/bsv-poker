/**
 * Reusable live multi-party settlement coordinator (core §6.6, §8). Each peer holds ONLY its own
 * key, signs the deterministic N-of-N settlement transaction, and gossips its signature over the
 * relay socket; once all N signatures are collected, the designated submitter assembles + submits
 * the transaction. No party can move the pot alone. Used by the on-chain E2Es and the bot daemon.
 */

import { RelayClient } from '@bsv-poker/app-services';
import { signPreimage, fundingUnlocking, type Script, type KeyPair } from '@bsv-poker/script-templates-ts';
import { bytesToHex } from '@bsv-poker/protocol-types';
import { type Tx, serializeTxWire, txidWire, sighashMessage, SIGHASH_ALL_FORKID } from '@bsv-poker/tx-builder';

const sigT = (msg: Uint8Array, k: KeyPair): Uint8Array => Uint8Array.from([...signPreimage(msg, k.priv), SIGHASH_ALL_FORKID]);

/** Gossip a per-peer value over the relay (e.g. each player's on-chain pubkey) and collect all N,
 *  ordered by index. Re-announces until everyone has converged (covers subscribe races). */
export async function gatherByIndex(relayUrl: string, tableId: string, kind: string, idx: number, mine: string, n: number, timeoutMs = 30000): Promise<string[]> {
  const relay = new RelayClient(relayUrl);
  const vals = new Map<number, string>([[idx, mine]]);
  const deadline = Date.now() + timeoutMs;
  await new Promise<void>((resolve, reject) => {
    let graceUntil = 0; // keep broadcasting my value briefly after collecting, so late subscribers get it
    const unsub = relay.subscribe(tableId, (text) => {
      try {
        const e = JSON.parse(text) as { t?: string; idx?: number; v?: string };
        if (e.t === kind && typeof e.idx === 'number' && typeof e.v === 'string' && !vals.has(e.idx)) {
          vals.set(e.idx, e.v);
          if (vals.size === n && graceUntil === 0) graceUntil = Date.now() + 1800;
        }
      } catch { /* ignore */ }
    });
    const tick = (): void => {
      if (graceUntil > 0 && Date.now() > graceUntil) { unsub(); resolve(); return; }
      if (Date.now() > deadline) { unsub(); reject(new Error(`gatherByIndex(${kind}) timed out`)); return; }
      void relay.publish(tableId, new TextEncoder().encode(JSON.stringify({ t: kind, idx, v: mine })));
      setTimeout(tick, 300);
    };
    tick();
  });
  return Array.from({ length: n }, (_, i) => vals.get(i)!);
}

/** The funder broadcasts a single value (e.g. the escrow outpoint) repeatedly; peers await it. */
export function broadcastValue(relayUrl: string, tableId: string, kind: string, v: string): () => void {
  const relay = new RelayClient(relayUrl);
  const id = setInterval(() => void relay.publish(tableId, new TextEncoder().encode(JSON.stringify({ t: kind, v }))), 300);
  void relay.publish(tableId, new TextEncoder().encode(JSON.stringify({ t: kind, v })));
  return () => clearInterval(id);
}

export async function awaitValue(relayUrl: string, tableId: string, kind: string, timeoutMs = 30000): Promise<string> {
  const relay = new RelayClient(relayUrl);
  const deadline = Date.now() + timeoutMs;
  return new Promise<string>((resolve, reject) => {
    const unsub = relay.subscribe(tableId, (text) => {
      try {
        const e = JSON.parse(text) as { t?: string; v?: string };
        if (e.t === kind && typeof e.v === 'string') { unsub(); resolve(e.v); }
      } catch { /* ignore */ }
    });
    setTimeout(() => { unsub(); reject(new Error(`awaitValue(${kind}) timed out`)); }, timeoutMs - (Date.now() - (deadline - timeoutMs)));
  });
}

export interface CoSignOpts {
  readonly relayUrl: string;
  readonly tableId: string;
  /** This peer's seat index in the N-of-N (the signature order CHECKMULTISIG verifies). */
  readonly idx: number;
  readonly myKey: KeyPair;
  /** The settlement tx every peer builds identically from the agreed outcome. */
  readonly settleTx: Tx;
  readonly fundingScript: Script;
  readonly potValue: number;
  readonly n: number;
  /** Whether this peer assembles + submits once all sigs are in (any one peer may). */
  readonly submit: boolean;
  readonly submitTx: (rawHex: string) => Promise<{ ok: boolean; reason: string }>;
  readonly timeoutMs?: number;
}

/** Gossip this peer's settlement signature over the relay, collect all N, and (if submitter) submit. */
export async function coSignSettlement(opts: CoSignOpts): Promise<{ collected: number; txid: string | undefined }> {
  const relay = new RelayClient(opts.relayUrl);
  const msg = sighashMessage(opts.settleTx, 0, opts.fundingScript, opts.potValue);
  const sigs = new Map<number, Uint8Array>();
  sigs.set(opts.idx, sigT(msg, opts.myKey));
  const deadline = Date.now() + (opts.timeoutMs ?? 30000);

  await new Promise<void>((resolve, reject) => {
    let graceUntil = 0; // keep broadcasting my sig briefly after collecting, so late subscribers get it
    const unsub = relay.subscribe(opts.tableId, (text) => {
      try {
        const e = JSON.parse(text) as { t?: string; idx?: number; sig?: string };
        if (e.t === 'settle-sig' && typeof e.idx === 'number' && e.sig && !sigs.has(e.idx)) {
          sigs.set(e.idx, Uint8Array.from(Buffer.from(e.sig, 'hex')));
          if (sigs.size === opts.n && graceUntil === 0) graceUntil = Date.now() + 1800;
        }
      } catch { /* not our envelope */ }
    });
    const announce = (): void => {
      if (graceUntil > 0 && Date.now() > graceUntil) { unsub(); resolve(); return; }
      if (Date.now() > deadline) { unsub(); reject(new Error('settlement signature collection timed out')); return; }
      void relay.publish(opts.tableId, new TextEncoder().encode(JSON.stringify({ t: 'settle-sig', idx: opts.idx, sig: bytesToHex(sigs.get(opts.idx)!) })));
      setTimeout(announce, 300);
    };
    announce();
  });

  if (!opts.submit) return { collected: sigs.size, txid: undefined };
  const ordered = Array.from({ length: opts.n }, (_, i) => sigs.get(i)!);
  const scriptSig = fundingUnlocking(ordered);
  const res = await opts.submitTx(bytesToHex(serializeTxWire(opts.settleTx, [scriptSig])));
  if (!res.ok) throw new Error(`settlement submit rejected: ${res.reason}`);
  return { collected: sigs.size, txid: txidWire(opts.settleTx, [scriptSig]) };
}
