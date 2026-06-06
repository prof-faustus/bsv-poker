// Proves the peer is reachable cross-machine: a default peer binds ALL interfaces, so it is reachable
// over this host's real (non-loopback) IPv4 — a loopback-only peer is not. This is the difference
// between a real P2P node and a local-only one. It is a PEER listener (the node also dials), not a
// central server.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { connect } from 'node:net';
import { networkInterfaces } from 'node:os';
import { P2PTransport } from '../src/p2p-transport.ts';

function externalIPv4(): string | null {
  for (const ifaces of Object.values(networkInterfaces())) {
    for (const i of ifaces ?? []) {
      if (i.family === 'IPv4' && !i.internal) return i.address;
    }
  }
  return null;
}

function reachable(host: string, port: number, ms = 1500): Promise<boolean> {
  return new Promise((res) => {
    const s = connect(port, host);
    const t = setTimeout(() => {
      s.destroy();
      res(false);
    }, ms);
    s.once('connect', () => {
      clearTimeout(t);
      s.destroy();
      res(true);
    });
    s.once('error', () => {
      clearTimeout(t);
      res(false);
    });
  });
}

test('default peer is reachable over the real LAN IP (cross-machine); loopback-only peer is not', async (t) => {
  const ext = externalIPv4();
  if (!ext) {
    t.skip('no non-loopback IPv4 on this host — cross-machine reachability is not applicable here');
    return;
  }

  const cross = new P2PTransport(0); // default bind = all interfaces
  await cross.start();
  const local = new P2PTransport(0, '127.0.0.1'); // explicit loopback-only
  await local.start();
  try {
    // cross-machine peer: reachable both over the LAN IP and loopback
    assert.equal(await reachable(ext, cross.boundPort()), true, 'default peer must be reachable over the LAN IP');
    assert.equal(await reachable('127.0.0.1', cross.boundPort()), true);
    // loopback-only peer: reachable on loopback, NOT over the LAN IP (so the default fix is what enables cross-machine)
    assert.equal(await reachable('127.0.0.1', local.boundPort()), true);
    assert.equal(await reachable(ext, local.boundPort()), false, 'loopback-only peer must NOT be reachable over the LAN IP');
  } finally {
    cross.close();
    local.close();
  }
});
