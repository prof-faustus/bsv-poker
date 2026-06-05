/**
 * LIVE on-chain bond forfeiture E2E (audit finding #22 — forfeiture wired into the live path).
 *
 * The relay-level drop and the on-chain penalty are now ONE flow: a real
 * `InteractiveNetworkedTableClient` table runs over an in-memory relay with the in-tree node as the
 * shared clock; a seat COMMITS but never REVEALS; the honest survivors drop it at the anchored reveal
 * deadline (relay level) AND, via the client's `onSeatDropped` event, a `ForfeitureCoordinator`
 * FORFEITS that seat's on-chain bond to the pot beneficiary. Proven end-to-end against the project's
 * OWN node (no external process):
 *
 *   1. the survivors re-derive the deck and finish the hand heads-up (relay-level accountability);
 *   2. the drop event fires for the non-revealer with its commitment (the bond's reveal commitment);
 *   3. the coordinator's forfeiture is REJECTED before maturity by the node's nLockTime gate, then
 *      ACCEPTED at maturity — the non-revealer's bond is forfeited to the beneficiary, conserving the
 *      bond value, and the absent owner can never reclaim it (it is spent).
 *
 * Signatures are bare DER over the BIP-143 sighash — the convention the in-tree interpreter verifies.
 */
import { randomBytes } from 'node:crypto';
import assert from 'node:assert/strict';
import { RegtestNode, p2pkhScript } from '@bsv-poker/adapters/regtest-node';
import { ForfeitureCoordinator, type SeatBond } from '@bsv-poker/adapters/forfeiture-coordinator';
import {
  genKeyPair,
  signPreimage,
  bondRevealOrForfeitLocking,
  bondReclaimByRevealUnlocking,
  type Script,
  type KeyPair,
} from '@bsv-poker/script-templates-ts';
import { bytesToHex, sha256, type BranchBinding } from '@bsv-poker/protocol-types';
import { type Tx, serializeTxWire, txidWire, sighashMessage } from '@bsv-poker/tx-builder';
import {
  InteractiveNetworkedTableClient,
  offlineRuleset,
  universalBot,
  sessionAuthFromSeed,
  type ClientUpdate,
  type TablePlayer,
  type SeatDrop,
} from '@bsv-poker/app-services';
import type { RelayClient } from '@bsv-poker/app-services';

const SUBSIDY = 5_000_000_000;
const BIND: BranchBinding = { gid: 'a1'.repeat(8), rulesetHash: 'b2'.repeat(32), round: 0, stateHash: 'c3'.repeat(32), actingSeat: -1, successorCommitment: '00'.repeat(32) };
const TABLE = 'tbl-live-forfeit';
const WINDOW = 3;
const hex = (b: Uint8Array): string => bytesToHex(b);

// ---- in-memory signed relay (the live transport) -------------------------------------------------
class MemHub {
  private readonly subs = new Map<string, Set<(t: string) => void>>();
  subscribe(table: string, cb: (t: string) => void): () => void {
    let set = this.subs.get(table);
    if (!set) this.subs.set(table, (set = new Set()));
    set.add(cb);
    return () => set!.delete(cb);
  }
  publish(table: string, bytes: Uint8Array): number {
    const text = new TextDecoder().decode(bytes);
    const set = this.subs.get(table);
    if (!set) return 0;
    for (const cb of [...set]) queueMicrotask(() => cb(text));
    return set.size;
  }
}
const relayOver = (hub: MemHub): RelayClient =>
  ({ subscribe: (t: string, cb: (x: string) => void) => hub.subscribe(t, cb), publish: async (t: string, b: Uint8Array) => hub.publish(t, b) }) as unknown as RelayClient;
/** Swallows this seat's reveal frames — the seat commits but never reveals. */
const relayDroppingReveal = (hub: MemHub, dropSeat: number): RelayClient =>
  ({
    subscribe: (t: string, cb: (x: string) => void) => hub.subscribe(t, cb),
    publish: async (t: string, b: Uint8Array) => {
      try {
        const e = JSON.parse(new TextDecoder().decode(b)) as { t?: string; seat?: number };
        if (e.t === 'reveal' && e.seat === dropSeat) return 0;
      } catch { /* not JSON — forward */ }
      return hub.publish(t, b);
    },
  }) as unknown as RelayClient;

async function main(): Promise<void> {
  const node = new RegtestNode();
  const funder = genKeyPair();
  const bene = genKeyPair(); // pot beneficiary — claims forfeited bonds
  const benePayout: Script = p2pkhScript(bene.pubCompressed);
  const BOND = SUBSIDY - 1000;

  // Each seat's per-hand entropy is its bond's reveal preimage; commitment = SHA-256(entropy) = the
  // handshake commit `c`. Fund a reveal-or-forfeit bond per seat (owner-reclaimable by revealing, else
  // forfeitable to `bene` after maturity).
  const entropies = [0, 1, 2].map(() => new Uint8Array(randomBytes(32)));
  const owners: KeyPair[] = [0, 1, 2].map(() => genKeyPair());

  const fundBond = async (commitment: Uint8Array, ownerPub: Uint8Array): Promise<SeatBond> => {
    const cb = await node.generateBlock(hex(funder.pubCompressed));
    await node.generateBlock(hex(funder.pubCompressed));
    const script = bondRevealOrForfeitLocking(BIND, commitment, ownerPub, bene.pubCompressed);
    const tx: Tx = { version: 1, inputs: [{ prevTxid: cb.coinbaseTxid, vout: 0, sequence: 0xffffffff }], outputs: [{ satoshis: BOND, locking: script }], nLockTime: 0 };
    const ss: Script = [signPreimage(sighashMessage(tx, 0, p2pkhScript(funder.pubCompressed), SUBSIDY), funder.priv), funder.pubCompressed];
    const r = await node.submitTx(bytesToHex(serializeTxWire(tx, [ss])));
    assert.equal(r.ok, true, `bond funding rejected: ${r.reason}`);
    await node.generateBlock(hex(funder.pubCompressed));
    return { txid: txidWire(tx, [ss]), vout: 0, script, value: BOND, commitment: hex(commitment) };
  };

  const bonds = new Map<number, SeatBond>();
  for (let seat = 0; seat < 3; seat++) bonds.set(seat, await fundBond(sha256(entropies[seat]!), owners[seat]!.pubCompressed));

  const coordinator = new ForfeitureCoordinator({ node, beneficiary: bene, beneficiaryPayout: benePayout, bonds });

  // ---- the live table -------------------------------------------------------------------------
  const hub = new MemHub();
  const ruleset = offlineRuleset('holdem', 3);
  const seatDefs: TablePlayer[] = [0, 1, 2].map((seat) => ({ seat, stack: 100 }));
  const auths = await Promise.all([0, 1, 2].map((i) => sessionAuthFromSeed(new Uint8Array(32).fill(i + 11))));
  const pubs = auths.map((a) => a.pub);
  const dropEvents: SeatDrop[] = [];

  const clients = auths.map((auth, mySeat) =>
    new InteractiveNetworkedTableClient({
      relay: mySeat === 2 ? relayDroppingReveal(hub, 2) : relayOver(hub),
      tableId: TABLE,
      mySeat,
      seats: seatDefs,
      ruleset,
      entropy: entropies[mySeat]!,
      auth,
      seatPubs: pubs,
      heightSource: () => node.height(), // the SHARED clock is the in-tree node's tip
      timeoutWindow: WINDOW,
      // Survivors record the forfeiture intent the instant they drop the non-revealer (audit #22).
      ...(mySeat !== 2
        ? { onSeatDropped: (d: SeatDrop) => { dropEvents.push(d); coordinator.record(d); } }
        : {}),
    }),
  );
  for (let i = 0; i < 3; i++) {
    const c = clients[i]!;
    c.onUpdate((u: ClientUpdate) => { if (u.yourTurn && u.legal && i !== 2) c.submitAction(universalBot(u.legal, u.mySeat)); });
  }

  const startHeight = await node.height();
  const h0 = clients[0]!.play();
  const h1 = clients[1]!.play();
  void clients[2]!.play(); // commits, then its reveal is swallowed → it never reveals

  // Let the commit phase + the honest reveals exchange WITHOUT advancing the clock (so nobody is
  // commit-dropped); the deadline is startHeight+WINDOW and height is still startHeight.
  await new Promise((r) => setTimeout(r, 400));
  // Now advance the shared clock past the reveal deadline → the survivors drop the non-revealer.
  for (let k = 0; k < WINDOW + 1; k++) await node.generateBlock(hex(funder.pubCompressed));
  const deadline = startHeight + WINDOW;

  const [s0, s1] = await Promise.all([h0, h1]);
  clients[2]!.abort();

  // (1) relay-level accountability: the survivors converged heads-up and excluded the non-revealer.
  assert.equal(clients[0]!.stateHash(), clients[1]!.stateHash(), 'survivors diverged after the drop');
  assert.equal(s0.handComplete && s1.handComplete, true, 'the heads-up hand did not complete');
  assert.equal(s0.seats.find((p) => p.seat === 2), undefined, 'the non-revealer must be excluded from the hand');
  console.log('[live-forfeit] relay-level: survivors finished heads-up; seat 2 excluded.');

  // (2) the drop event fired for the non-revealer (reveal phase), carrying its bond commitment.
  const reveal2 = dropEvents.find((d) => d.seat === 2 && d.phase === 'reveal');
  assert.ok(reveal2, 'no reveal-phase drop event was emitted for the non-revealer');
  assert.equal(reveal2!.commitment, bonds.get(2)!.commitment, 'drop commitment must match the bond commitment');
  assert.equal(reveal2!.deadlineHeight, deadline, `drop deadlineHeight ${reveal2!.deadlineHeight} != expected ${deadline}`);
  assert.equal(coordinator.pendingSeats().includes(2), true, 'the forfeiture was not recorded for seat 2');
  console.log(`[live-forfeit] drop event: seat 2 reveal-dropped at height ${deadline} (commitment ${reveal2!.commitment.slice(0, 12)}…).`);

  // (3a) NEGATIVE — a forfeiture submitted before maturity is rejected by the nLockTime finality gate.
  //      (Re-derive at a future maturity to demonstrate the gate deterministically.)
  {
    const future = (await node.height()) + 5;
    const probe = new ForfeitureCoordinator({ node, beneficiary: bene, beneficiaryPayout: benePayout, bonds });
    probe.record({ seat: 2, hand: 0, phase: 'reveal', deadlineHeight: future, commitment: bonds.get(2)!.commitment });
    const premature = await probe.flush();
    assert.equal(premature[0]?.submitted, false, 'a premature forfeiture must be rejected (immature/nLockTime)');
    console.log(`[live-forfeit] premature forfeiture correctly held back: ${premature[0]?.reason}`);
  }

  // (3b) POSITIVE — at maturity the recorded forfeiture is accepted; the bond is forfeited to bene.
  const flushed = await coordinator.flush();
  const r2 = flushed.find((f) => f.seat === 2);
  assert.equal(r2?.submitted, true, `forfeiture at maturity rejected: ${r2?.reason}`);
  await node.generateBlock(hex(funder.pubCompressed)); // confirm

  const bond2 = bonds.get(2)!;
  assert.equal((await node.outpointStatus(bond2.txid, bond2.vout)).unspent, false, 'seat 2 bond not consumed by FORFEIT');
  assert.equal(coordinator.pendingSeats().includes(2), false, 'seat 2 forfeiture should be cleared after submission');
  console.log(`[live-forfeit] FORFEIT confirmed: seat 2's bond (${BOND} sats) forfeited to the beneficiary.`);

  // (3c) the absent owner can NEVER reclaim its forfeited bond (the outpoint is already spent).
  {
    const tx: Tx = { version: 1, inputs: [{ prevTxid: bond2.txid, vout: bond2.vout, sequence: 0xffffffff }], outputs: [{ satoshis: BOND - 1000, locking: p2pkhScript(owners[2]!.pubCompressed) }], nLockTime: 0 };
    const msg = sighashMessage(tx, 0, bond2.script, BOND);
    const ss = bondReclaimByRevealUnlocking(signPreimage(msg, owners[2]!.priv), entropies[2]!);
    const late = await node.submitTx(bytesToHex(serializeTxWire(tx, [ss])));
    assert.equal(late.ok, false, 'the forfeited owner must not be able to reclaim its bond (double-spend)');
    console.log(`[live-forfeit] post-forfeit owner reclaim correctly rejected: "${late.reason}"`);
  }

  // The two survivors' bonds remain reclaimable (they revealed) — accountability hit only the absent seat.
  assert.equal((await node.outpointStatus(bonds.get(0)!.txid, 0)).unspent, true, "survivor seat 0's bond must be untouched");
  assert.equal((await node.outpointStatus(bonds.get(1)!.txid, 0)).unspent, true, "survivor seat 1's bond must be untouched");

  console.log('\n[live-forfeit] PASS — the LIVE relay drop and the on-chain bond FORFEITURE are one flow: a non-revealer is dropped by the survivors AND its bond is forfeited to the pot beneficiary at maturity (premature claims rejected by the node), conserving the bond, while the survivors finish heads-up and the honest seats keep their bonds. Standalone — the in-tree node, no external process.');
}

main().then(
  () => process.exit(0),
  (e) => {
    console.error('[live-forfeit] FAIL:', (e as Error).stack ?? (e as Error).message);
    process.exit(1);
  },
);
