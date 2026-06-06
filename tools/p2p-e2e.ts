/**
 * Fully PEER-TO-PEER hand — NO SERVER. Three players exchange every commit/reveal/action DIRECTLY
 * over a P2P gossip mesh and converge byte-for-byte on the final state. There is NO relay server, NO
 * indexer server, NO central anything — only `P2PTransport` peers talking to each other.
 *
 * The mesh is a CHAIN A—B—C (A and C are NOT directly connected), so the hand can only complete if
 * frames GOSSIP across the mesh (A's commit reaches C via B and vice-versa). Convergence proves the
 * serverless transport carries the whole mental-poker protocol.
 */
import assert from 'node:assert/strict';
import { P2PTransport } from '@bsv-poker/adapters/p2p-transport';
import {
  InteractiveNetworkedTableClient,
  offlineRuleset,
  universalBot,
  sessionAuthFromSeed,
  type ClientUpdate,
  type TablePlayer,
} from '@bsv-poker/app-services';
import type { RelayClient } from '@bsv-poker/app-services';

const TABLE = 'p2p-table';

async function main(): Promise<void> {
  // Three peers, each on its own loopback port, connected in a CHAIN: B dials A, C dials B.
  const portA = 9301, portB = 9302, portC = 9303;
  const tA = new P2PTransport(portA);
  const tB = new P2PTransport(portB);
  const tC = new P2PTransport(portC);
  await tA.start([]); // A only listens
  await tB.start([{ host: '127.0.0.1', port: portA }]); // B—A
  await tC.start([{ host: '127.0.0.1', port: portB }]); // C—B   (A and C never directly connect)

  // Wait for the chain to come up.
  const dl = Date.now() + 10_000;
  while (!(tA.peerCount() >= 1 && tB.peerCount() >= 2 && tC.peerCount() >= 1)) {
    if (Date.now() > dl) throw new Error(`mesh did not form: A=${tA.peerCount()} B=${tB.peerCount()} C=${tC.peerCount()}`);
    await new Promise((r) => setTimeout(r, 50));
  }
  console.log(`[p2p] mesh up (chain A—B—C): A=${tA.peerCount()} peer, B=${tB.peerCount()} peers, C=${tC.peerCount()} peer — NO server.`);

  const ruleset = offlineRuleset('holdem', 3);
  const seatDefs: TablePlayer[] = [0, 1, 2].map((seat) => ({ seat, stack: 100 }));
  const auths = await Promise.all([0, 1, 2].map((i) => sessionAuthFromSeed(new Uint8Array(32).fill(i + 31))));
  const pubs = auths.map((a) => a.pub);
  const transports = [tA, tB, tC];

  const clients = auths.map((auth, mySeat) =>
    new InteractiveNetworkedTableClient({
      relay: transports[mySeat] as unknown as RelayClient, // the P2P mesh IS the transport — no relay server
      tableId: TABLE,
      mySeat,
      seats: seatDefs,
      ruleset,
      entropy: new Uint8Array(32).map(() => Math.floor(Math.random() * 256)),
      auth,
      seatPubs: pubs,
    }),
  );
  for (const c of clients) {
    c.onUpdate((u: ClientUpdate) => { if (u.yourTurn && u.legal) c.submitAction(universalBot(u.legal, u.mySeat)); });
  }

  console.log('[p2p] three peers playing a hand entirely peer-to-peer…');
  const results = await Promise.all(clients.map((c) => c.play()));

  // Every honest peer converged on the SAME final state — over the serverless gossip mesh.
  assert.equal(results.every((s) => s.handComplete), true, 'the hand must complete');
  assert.equal(clients[0]!.stateHash(), clients[1]!.stateHash(), 'peers A and B diverged');
  assert.equal(clients[1]!.stateHash(), clients[2]!.stateHash(), 'peers B and C diverged');
  const chips = results[0]!.seats.reduce((a, p) => a + p.stack, 0);
  assert.equal(chips, 300, 'chips conserved');
  console.log(`[p2p] all three peers converged: stateHash ${clients[0]!.stateHash()!.slice(0, 16)}…, chips ${chips}.`);

  for (const t of transports) t.close();
  console.log('\n[p2p] PASS — a full mental-poker hand played FULLY PEER-TO-PEER across a gossip mesh (chain A—B—C), no relay/indexer/server. The transport carried every commit/reveal/action directly between players.');
}

main().then(
  () => process.exit(0),
  (e) => {
    console.error('[p2p] FAIL:', (e as Error).stack ?? (e as Error).message);
    process.exit(1);
  },
);
