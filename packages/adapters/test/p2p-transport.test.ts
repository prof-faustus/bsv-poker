/**
 * P2P gossip transport (NO SERVER): a frame published at one end of a chain reaches the far end via
 * flooding; the publisher's own subscribers get an echo; each frame is delivered ONCE (dedup, no loop).
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { P2PTransport } from '../src/p2p-transport.ts';

const txt = (s: string): Uint8Array => new TextEncoder().encode(s);
async function until(cond: () => boolean, ms = 3000): Promise<void> {
  const dl = Date.now() + ms;
  while (!cond()) {
    if (Date.now() > dl) throw new Error('timeout');
    await new Promise((r) => setTimeout(r, 20));
  }
}

test('a frame gossips across a chain A—B—C to the far peer, exactly once (no server)', async () => {
  const a = new P2PTransport(9521);
  const b = new P2PTransport(9522);
  const c = new P2PTransport(9523);
  await a.start([]);
  await b.start([{ host: '127.0.0.1', port: 9521 }]);
  await c.start([{ host: '127.0.0.1', port: 9522 }]); // chain: A—B—C (A and C not directly connected)
  try {
    await until(() => a.peerCount() >= 1 && b.peerCount() >= 2 && c.peerCount() >= 1);
    const atC: string[] = [];
    const atOwn: string[] = [];
    c.subscribe('tbl', (t) => atC.push(t));
    a.subscribe('tbl', (t) => atOwn.push(t)); // publisher's own echo

    await a.publish('tbl', txt('hello-peers'));
    await until(() => atC.length > 0);
    await new Promise((r) => setTimeout(r, 100)); // allow any (wrong) duplicate to arrive

    assert.deepEqual(atC, ['hello-peers'], 'far peer C receives the frame EXACTLY once via gossip');
    assert.deepEqual(atOwn, ['hello-peers'], 'the publisher receives its own frame (echo)');
  } finally {
    a.close();
    b.close();
    c.close();
  }
});

test('subscribers only get their table; unsubscribe stops delivery', async () => {
  const a = new P2PTransport(9531);
  const b = new P2PTransport(9532);
  await a.start([]);
  await b.start([{ host: '127.0.0.1', port: 9531 }]);
  try {
    await until(() => a.peerCount() >= 1 && b.peerCount() >= 1);
    const t1: string[] = [];
    const t2: string[] = [];
    const off = b.subscribe('t1', (t) => t1.push(t));
    b.subscribe('t2', (t) => t2.push(t));

    await a.publish('t1', txt('one'));
    await until(() => t1.length > 0);
    assert.deepEqual(t1, ['one']);
    assert.deepEqual(t2, [], 'a different table gets nothing');

    off(); // unsubscribe t1
    await a.publish('t1', txt('two'));
    await new Promise((r) => setTimeout(r, 150));
    assert.deepEqual(t1, ['one'], 'after unsubscribe, no further delivery');
  } finally {
    a.close();
    b.close();
  }
});
