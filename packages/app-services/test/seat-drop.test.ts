/**
 * `onSeatDropped` drop event (audit #22 — the wiring point for on-chain forfeiture). When the live
 * client drops a seat that COMMITTED but never REVEALED, it must emit a `reveal`-phase drop event
 * carrying that seat's commitment (`c = SHA-256(entropy)`) and the anchored deadline height — the
 * exact inputs the Node-side ForfeitureCoordinator needs to forfeit the bond. Browser-safe: the
 * client only calls a function; the on-chain work is elsewhere.
 *
 * Same in-memory signed-relay + gated-height harness as timeout-claim.test.ts.
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { InteractiveNetworkedTableClient, type ClientUpdate, type TablePlayer, type SeatDrop } from '../src/interactive-client.ts';
import { offlineRuleset, universalBot } from '../src/offline.ts';
import { sessionAuthFromSeed } from '../src/session-auth.ts';
import { sha256, bytesToHex } from '@bsv-poker/protocol-types';
import type { RelayClient } from '../src/network.ts';

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
const relayDroppingReveal = (hub: MemHub, dropSeat: number): RelayClient =>
  ({
    subscribe: (t: string, cb: (x: string) => void) => hub.subscribe(t, cb),
    publish: async (t: string, b: Uint8Array) => {
      try {
        const e = JSON.parse(new TextDecoder().decode(b)) as { t?: string; seat?: number };
        if (e.t === 'reveal' && e.seat === dropSeat) return 0;
      } catch { /* forward */ }
      return hub.publish(t, b);
    },
  }) as unknown as RelayClient;
class GatedHeight {
  private openedAt = 0;
  private readonly tickMs: number;
  constructor(tickMs: number) { this.tickMs = tickMs; }
  open(): void { this.openedAt = Date.now(); }
  source = async (): Promise<number> => (this.openedAt === 0 ? 0 : Math.floor((Date.now() - this.openedAt) / this.tickMs));
}

const TABLE = 'tbl-drop';
const WINDOW = 3;

test('a committed-but-never-revealed seat triggers a reveal-phase drop event with its commitment (audit #22)', async () => {
  const hub = new MemHub();
  const height = new GatedHeight(40);
  const ruleset = offlineRuleset('holdem', 3);
  const seatDefs: TablePlayer[] = [0, 1, 2].map((seat) => ({ seat, stack: 100 }));
  const auths = await Promise.all([0, 1, 2].map((i) => sessionAuthFromSeed(new Uint8Array(32).fill(i + 1))));
  const pubs = auths.map((a) => a.pub);
  const entropies = [0, 1, 2].map(() => new Uint8Array(randomBytes(32)));
  const drops: SeatDrop[] = [];

  const clients = auths.map((auth, mySeat) =>
    new InteractiveNetworkedTableClient({
      relay: mySeat === 2 ? relayDroppingReveal(hub, 2) : relayOver(hub),
      tableId: TABLE, mySeat, seats: seatDefs, ruleset, entropy: entropies[mySeat]!, auth, seatPubs: pubs,
      heightSource: height.source, timeoutWindow: WINDOW,
      ...(mySeat !== 2 ? { onSeatDropped: (d: SeatDrop) => drops.push(d) } : {}),
    }),
  );
  for (let i = 0; i < 3; i++) {
    const c = clients[i]!;
    c.onUpdate((u: ClientUpdate) => { if (u.yourTurn && u.legal && i !== 2) c.submitAction(universalBot(u.legal, u.mySeat)); });
  }

  const h0 = clients[0]!.play();
  const h1 = clients[1]!.play();
  void clients[2]!.play();
  await new Promise((r) => setTimeout(r, 150)); // commits + honest reveals exchange at the floor height
  height.open(); // pass the reveal deadline → survivors drop the non-revealer

  await Promise.all([h0, h1]);
  clients[2]!.abort();

  const reveal2 = drops.find((d) => d.seat === 2 && d.phase === 'reveal');
  assert.ok(reveal2, 'expected a reveal-phase drop event for the non-revealing seat 2');
  assert.equal(reveal2!.commitment, bytesToHex(sha256(entropies[2]!)), 'the drop must carry seat 2 commit c = SHA-256(entropy)');
  assert.equal(typeof reveal2!.deadlineHeight, 'number');
  assert.ok(reveal2!.deadlineHeight >= WINDOW, 'the deadline height should be at least the window');
  // No spurious forfeitable drop for the honest survivors.
  assert.equal(drops.some((d) => d.phase === 'reveal' && d.seat !== 2), false, 'no honest seat should be reveal-dropped');
});
