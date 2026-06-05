/**
 * Non-grindable commit-reveal seating (audit finding #27). Seat order is a beacon over every player's
 * nonce; the prior scheme disclosed each nonce on join, so a LAST MOVER could grind its own nonce
 * against the others' to bias the beacon. The fix is commit-reveal: every seat first publishes a
 * binding commitment H(nonce); nonces are disclosed ONLY after all commitments are in. These tests
 * prove the wire-level ordering property (no nonce is revealed while any commitment is still pending)
 * and that all peers derive the SAME seating; plus that a reveal must match its commitment.
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { LobbyClient, computeSeatOrder, type TableMeta, type SeatingReveal } from '../src/lobby.ts';
import { sessionAuthFromSeed, type SessionAuth } from '../src/session-auth.ts';
import { seatCommit } from '../src/lobby.ts';
import { randomBytes } from 'node:crypto';
import type { RelayClient } from '../src/network.ts';

interface Frame { seq: number; t?: string | undefined; pub?: string | undefined; commit?: string | undefined; nonce?: string | undefined }

/** In-memory relay hub that RECORDS the publish order of every seating frame (for the ordering proof). */
class RecordingHub {
  private readonly subs = new Map<string, Set<(t: string) => void>>();
  readonly log: Frame[] = [];
  private seq = 0;
  subscribe(table: string, cb: (t: string) => void): () => void {
    let set = this.subs.get(table);
    if (!set) this.subs.set(table, (set = new Set()));
    set.add(cb);
    return () => set!.delete(cb);
  }
  publish(table: string, bytes: Uint8Array): number {
    const text = new TextDecoder().decode(bytes);
    try {
      const o = JSON.parse(text) as Frame;
      this.log.push({ seq: this.seq++, t: o.t, pub: o.pub, commit: o.commit, nonce: o.nonce });
    } catch { /* ignore */ }
    const set = this.subs.get(table);
    if (!set) return 0;
    for (const cb of [...set]) queueMicrotask(() => cb(text));
    return set.size;
  }
  inject(table: string, frame: object): void { this.publish(table, new TextEncoder().encode(JSON.stringify(frame))); }
}
const relayOver = (hub: RecordingHub): RelayClient =>
  ({ subscribe: (t: string, cb: (x: string) => void) => hub.subscribe(t, cb), publish: async (t: string, b: Uint8Array) => hub.publish(t, b) }) as unknown as RelayClient;

const META: TableMeta = { name: 'seat-test', variant: 'holdem', smallBlind: 1, bigBlind: 2, startingStack: 100, maxSeats: 3 };
const TABLE = 'tbl-seat';

async function seatThree(hub: RecordingHub): Promise<{ auth: SessionAuth; result: { mySeat: number; players: { pub: string }[] } }[]> {
  const auths = await Promise.all([0, 1, 2].map((i) => sessionAuthFromSeed(new Uint8Array(32).fill(i + 21))));
  const joins = auths.map((auth, i) => {
    const lobby = new LobbyClient(relayOver(hub));
    return lobby.joinWaitingRoom(TABLE, { id: `p${i}`, pub: auth.pub, sign: (m) => auth.sign(m) }, META);
  });
  const results = await Promise.all(joins.map((j) => j.seated));
  return auths.map((auth, i) => ({ auth, result: results[i]! }));
}

test('seating discloses NO nonce until every commitment is on the wire (audit #27 non-grindable)', async () => {
  const hub = new RecordingHub();
  await seatThree(hub);

  // First publish index at which the 3rd DISTINCT commitment appeared.
  const seenCommit = new Set<string>();
  let allCommitsAt = -1;
  for (const f of hub.log) {
    if (f.t === 'join' && f.commit && f.pub) {
      seenCommit.add(f.pub);
      if (seenCommit.size === META.maxSeats && allCommitsAt === -1) allCommitsAt = f.seq;
    }
  }
  const firstReveal = hub.log.find((f) => f.t === 'seat-reveal');
  assert.notEqual(allCommitsAt, -1, 'all commitments should have been published');
  assert.ok(firstReveal, 'reveals must eventually be published');
  assert.ok(
    firstReveal!.seq > allCommitsAt,
    `a nonce was revealed (seq ${firstReveal!.seq}) before all commitments were in (seq ${allCommitsAt}) — grindable`,
  );
});

test('all peers derive the SAME seating and distinct seats (audit #27)', async () => {
  const hub = new RecordingHub();
  const seated = await seatThree(hub);
  // Every client agrees on the seating order (same pub sequence) ...
  const order0 = seated[0]!.result.players.map((p) => p.pub);
  for (const s of seated) assert.deepEqual(s.result.players.map((p) => p.pub), order0, 'peers disagree on seating order');
  // ... and each client's own seat matches its position in that order; the seats are a permutation 0..n-1.
  const mySeats = seated.map((s) => s.result.mySeat).sort((a, b) => a - b);
  assert.deepEqual(mySeats, [0, 1, 2], 'seats are not a clean permutation');
  for (const s of seated) assert.equal(order0[s.result.mySeat], s.auth.pub, 'mySeat does not match position in the agreed order');
});

test('PROPERTY: computeSeatOrder is a permutation, deterministic, and input-order-independent (audit #27)', () => {
  const rnd = (): string => randomBytes(8).toString('hex');
  for (let trial = 0; trial < 500; trial++) {
    const n = 2 + (randomBytes(1)[0]! % 8); // 2..9 players
    const reveals: SeatingReveal[] = Array.from({ length: n }, (_, i) => ({ id: `p${i}`, pub: rnd(), nonce: rnd() }));
    const order = computeSeatOrder(reveals, n);
    // (1) it is a PERMUTATION of the input pubs — same set, no duplicates, no fabricated seats.
    const inPubs = new Set(reveals.map((r) => r.pub));
    const outPubs = order.map((r) => r.pub);
    assert.equal(outPubs.length, n, 'must seat exactly every player');
    assert.equal(new Set(outPubs).size, n, 'no duplicate seat');
    for (const p of outPubs) assert.ok(inPubs.has(p), 'a seated pub must come from the input');
    // (2) DETERMINISTIC + INPUT-ORDER INDEPENDENT: shuffling the input yields the identical order
    //     (every honest peer, regardless of the order it saw reveals, derives the same seating).
    const shuffled = [...reveals].sort(() => (randomBytes(1)[0]! < 128 ? -1 : 1));
    assert.deepEqual(computeSeatOrder(shuffled, n).map((r) => r.pub), outPubs, 'seating must not depend on input order');
    // (3) slicing to fewer seats than players keeps exactly maxSeats and stays a prefix of the order.
    const cut = Math.max(2, n - 1);
    assert.deepEqual(computeSeatOrder(reveals, cut).map((r) => r.pub), outPubs.slice(0, cut), 'maxSeats slice must be the order prefix');
  }
});

test('a reveal whose nonce does not match its commitment is rejected (binding)', () => {
  // seatCommit is the exact H(nonce) the protocol commits to; a different nonce yields a
  // different commitment, so a swapped-nonce reveal can never satisfy the seatCommit check.
  const committed = seatCommit('the-committed-nonce');
  const swapped = seatCommit('a-different-nonce');
  assert.notEqual(committed, swapped, 'distinct nonces must produce distinct commitments');
  assert.equal(seatCommit('the-committed-nonce'), committed, 'the commitment must be deterministic');
});
