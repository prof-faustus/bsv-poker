/**
 * On-chain bond FORFEITURE coordinator (audit finding #22 — the accountability penalty, wired into
 * the LIVE path). The interactive client emits a drop event the moment it drops a seat at an anchored
 * deadline (`onSeatDropped`); for a `reveal` drop — a seat that COMMITTED but never REVEALED — this
 * coordinator turns that event into a real on-chain forfeiture: it spends the non-revealer's per-hand
 * bond (locked with `bondRevealOrForfeitLocking` to that seat's commitment) to the pot beneficiary,
 * with `nLockTime = deadlineHeight` so the node's finality gate enforces the maturity.
 *
 * This is Node-side (it builds + signs real BSV transactions). It is intentionally decoupled from the
 * browser-safe client: it consumes a STRUCTURAL drop event, so app-services need not be imported here.
 *
 * Lifecycle: `record(drop)` is synchronous and idempotent per seat (safe to call from the client's
 * drop callback); `flush()` submits every recorded forfeiture whose maturity height has been reached.
 * A premature claim is rejected by the node's nLockTime finality gate and stays pending for the next
 * flush — exactly the gate proven in onchain-forfeit-e2e, now driven by a live drop.
 */
import { bondForfeitClaimUnlocking, signPreimage, type KeyPair, type Script } from '@bsv-poker/script-templates-ts';
import { type Tx, serializeTxWire, txidWire, sighashMessage } from '@bsv-poker/tx-builder';
import { bytesToHex } from '@bsv-poker/protocol-types';

/** A funded per-seat bond outpoint locked with `bondRevealOrForfeitLocking(b, commitment, owner, bene)`. */
export interface SeatBond {
  readonly txid: string;
  readonly vout: number;
  /** The bond locking script — the scriptCode the BIP-143 sighash commits to. */
  readonly script: Script;
  readonly value: number;
  /** hex `SHA-256(entropy)` — the reveal commitment the bond is locked to (equals the handshake `c`). */
  readonly commitment: string;
}

/** Minimal node surface the coordinator needs (RegtestNode / RealBsvNode both satisfy it). */
export interface ForfeitNode {
  submitTx(rawHex: string): Promise<{ ok: boolean; reason?: string }>;
  height(): Promise<number>;
}

/** Structural drop event (mirrors app-services `SeatDrop`; kept structural to avoid a layer dependency). */
export interface DropEvent {
  readonly seat: number;
  readonly hand: number;
  readonly phase: 'commit' | 'reveal' | 'action';
  readonly deadlineHeight: number;
  readonly commitment?: string;
}

/** A built, signed forfeiture transaction awaiting maturity. */
export interface ForfeiturePlan {
  readonly seat: number;
  readonly bondTxid: string;
  readonly forfeitTxid: string;
  readonly raw: string;
  /** nLockTime — the node accepts the claim only once its tip height reaches this. */
  readonly maturity: number;
}

export interface FlushResult {
  readonly seat: number;
  readonly submitted: boolean;
  readonly reason?: string;
}

export interface ForfeitureCoordinatorOpts {
  readonly node: ForfeitNode;
  /** The pot beneficiary that claims forfeited bonds. */
  readonly beneficiary: KeyPair;
  /** Locking script the forfeited value pays to (e.g. p2pkh of the beneficiary). */
  readonly beneficiaryPayout: Script;
  /** seat → its funded bond outpoint. */
  readonly bonds: ReadonlyMap<number, SeatBond>;
  readonly feeSats?: number;
}

export class ForfeitureCoordinator {
  private readonly pending = new Map<number, ForfeiturePlan>();
  private readonly opts: ForfeitureCoordinatorOpts;

  // A normal constructor (not parameter properties) — the repo bans non-erasable TS syntax.
  constructor(opts: ForfeitureCoordinatorOpts) {
    this.opts = opts;
  }

  /**
   * Record a forfeiture intent for a REVEAL drop (the only on-chain-forfeitable case — a non-revealer).
   * Idempotent per seat. Returns the built plan, or null when the drop is not a forfeitable reveal-drop,
   * the seat has no registered bond, or the drop's commitment does not match the bond's commitment.
   */
  record(d: DropEvent): ForfeiturePlan | null {
    if (d.phase !== 'reveal') return null;
    const existing = this.pending.get(d.seat);
    if (existing) return existing;
    const bond = this.opts.bonds.get(d.seat);
    if (!bond) return null;
    // Integrity: only forfeit a bond whose locked commitment matches the dropped seat's committed `c`.
    if (d.commitment !== undefined && d.commitment !== bond.commitment) return null;

    const fee = this.opts.feeSats ?? 1000;
    const tx: Tx = {
      version: 1,
      // Non-final input (sequence < 0xffffffff) so the node ENFORCES nLockTime (maturity, audit 3).
      inputs: [{ prevTxid: bond.txid, vout: bond.vout, sequence: 0xfffffffe }],
      outputs: [{ satoshis: bond.value - fee, locking: this.opts.beneficiaryPayout }],
      nLockTime: d.deadlineHeight,
    };
    const msg = sighashMessage(tx, 0, bond.script, bond.value);
    const ss = bondForfeitClaimUnlocking(signPreimage(msg, this.opts.beneficiary.priv));
    const plan: ForfeiturePlan = {
      seat: d.seat,
      bondTxid: bond.txid,
      forfeitTxid: txidWire(tx, [ss]),
      raw: bytesToHex(serializeTxWire(tx, [ss])),
      maturity: d.deadlineHeight,
    };
    this.pending.set(d.seat, plan);
    return plan;
  }

  /**
   * Submit every recorded forfeiture whose maturity height has been reached. Immature claims stay
   * pending (the node would reject them via the nLockTime finality gate) and are retried next flush.
   * A successfully-submitted forfeiture is removed from the pending set.
   */
  async flush(): Promise<FlushResult[]> {
    const h = await this.opts.node.height();
    const out: FlushResult[] = [];
    for (const [seat, plan] of [...this.pending]) {
      if (h < plan.maturity) {
        out.push({ seat, submitted: false, reason: `immature (height ${h} < maturity ${plan.maturity})` });
        continue;
      }
      const r = await this.opts.node.submitTx(plan.raw);
      out.push({ seat, submitted: r.ok, ...(r.reason ? { reason: r.reason } : {}) });
      if (r.ok) this.pending.delete(seat);
    }
    return out;
  }

  /** Seats with a recorded-but-not-yet-confirmed forfeiture. */
  pendingSeats(): number[] {
    return [...this.pending.keys()];
  }
}
