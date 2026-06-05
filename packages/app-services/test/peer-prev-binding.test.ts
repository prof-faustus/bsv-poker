/**
 * Peer-action prior-state binding (audit finding #12, §8). A peer signs `prev` = the state hash
 * BEFORE its action; under convergent honest play (P2) that MUST equal our current state hash. The
 * client MUST verify it before applying — an authentic-but-wrong-`prev` action (a replay onto a
 * different branch, or an equivocating peer) must be REJECTED fail-closed, not applied.
 *
 *  - POSITIVE: two honest heads-up clients exchange real signed actions; every peer action's `prev`
 *    matches, the hand completes, and the two clients converge byte-for-byte.
 *  - NEGATIVE: a properly-SIGNED seat action carrying a wrong `prev` is injected; the receiving
 *    honest client aborts the hand with a prior-state-hash mismatch instead of applying it.
 *
 * Same in-memory signed-relay harness as timeout-claim.test.ts.
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { InteractiveNetworkedTableClient, type ClientUpdate, type TablePlayer } from '../src/interactive-client.ts';
import { offlineRuleset, universalBot } from '../src/offline.ts';
import { sessionAuthFromSeed, envelopeMessage, type SessionAuth } from '../src/session-auth.ts';
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
  inject(table: string, text: string): void {
    this.publish(table, new TextEncoder().encode(text));
  }
}

function relayOver(hub: MemHub): RelayClient {
  return {
    subscribe: (table: string, cb: (t: string) => void) => hub.subscribe(table, cb),
    publish: async (table: string, bytes: Uint8Array) => hub.publish(table, bytes),
  } as unknown as RelayClient;
}

const TABLE = 'tbl-prev';

async function buildHeadsUp(hub: MemHub, autoplay: boolean[]): Promise<{ auth: SessionAuth; client: InteractiveNetworkedTableClient }[]> {
  const ruleset = offlineRuleset('holdem', 2);
  const seatDefs: TablePlayer[] = [0, 1].map((seat) => ({ seat, stack: 100 }));
  const auths = await Promise.all([0, 1].map((i) => sessionAuthFromSeed(new Uint8Array(32).fill(i + 7))));
  const pubs = auths.map((a) => a.pub);
  return auths.map((auth, mySeat) => {
    // No heightSource: no timeouts — the client simply waits for each peer action (the path under test).
    const client = new InteractiveNetworkedTableClient({
      relay: relayOver(hub), tableId: TABLE, mySeat, seats: seatDefs, ruleset, entropy: randomBytes(32), auth, seatPubs: pubs,
    });
    if (autoplay[mySeat]) {
      client.onUpdate((u: ClientUpdate) => {
        if (u.yourTurn && u.legal) client.submitAction(universalBot(u.legal, u.mySeat));
      });
    }
    return { auth, client };
  });
}

test('POSITIVE: honest heads-up play exchanges valid prev-bound actions and converges (audit #12)', async () => {
  const hub = new MemHub();
  const seats = await buildHeadsUp(hub, [true, true]);
  const [s0, s1] = await Promise.all([seats[0]!.client.play(), seats[1]!.client.play()]);
  assert.equal(s0.handComplete, true);
  assert.equal(s1.handComplete, true);
  // Byte-identical convergence: every applied peer action's prev matched the receiver's state (P2).
  assert.equal(seats[0]!.client.stateHash(), seats[1]!.client.stateHash(), 'honest clients diverged');
});

test('NEGATIVE: a signed action with a WRONG prior-state hash is rejected fail-closed (audit #12)', async () => {
  const hub = new MemHub();
  // Seat 0 is honest and auto-plays; seat 1 runs the handshake but NEVER acts honestly — the only
  // seat-1 action on the wire is the forged-prev one we inject.
  const seats = await buildHeadsUp(hub, [true, false]);

  let injected = false;
  seats[0]!.client.onUpdate((u: ClientUpdate) => {
    // The first time it is NOT seat 0's turn in a live betting state, seat 1 is to act — inject a
    // properly-signed seat-1 action carrying a deliberately wrong prev (all-ff, never the real hash).
    if (!injected && u.state && !u.yourTurn && !u.complete) {
      injected = true;
      const env = { t: 'action', seat: 1, hand: 0, kind: 'fold', amount: 0, prev: 'ff'.repeat(32) };
      void seats[1]!.auth.sign(envelopeMessage(TABLE, env)).then((sig) => {
        hub.inject(TABLE, JSON.stringify({ ...env, sig }));
      });
    }
  });

  // Seat 1 joins the handshake (publishes commit+reveal) but never acts honestly; the ONLY seat-1
  // action on the wire is the forged-prev one injected above.
  void seats[1]!.client.play();
  // Seat 0 must abort the hand with a prior-state-hash mismatch rather than apply the wrong-state action.
  await assert.rejects(seats[0]!.client.play(), /prior-state hash mismatch/);
  seats[1]!.client.abort();
});
