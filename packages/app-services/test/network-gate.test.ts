import { test } from 'node:test';
import assert from 'node:assert/strict';
import { selectNetwork, MAINNET_ACK_TOKEN, resolveBindHost, isLoopback } from '../src/network-gate.ts';

test('default is play-money regtest with a no-real-value banner (REQ-PROD-012)', () => {
  const s = selectNetwork();
  assert.equal(s.network, 'play-regtest');
  assert.equal(s.mainnetEnabled, false);
  assert.equal(s.realFunds, false);
  assert.match(s.banner, /no real value/i);
});

test('regtest surfaces a test-coins banner and never enables real funds', () => {
  const s = selectNetwork({ network: 'regtest' });
  assert.equal(s.realFunds, false);
  assert.match(s.banner, /regtest/i);
});

test('mainnet is REFUSED without the explicit acknowledgement token', () => {
  assert.throws(() => selectNetwork({ network: 'mainnet' }), /research\/regtest only/);
  assert.throws(() => selectNetwork({ network: 'mainnet', mainnetAck: 'yes please' }), /acknowledgement token/);
});

test('mainnet enables only with the exact token, and the banner warns of real funds', () => {
  const s = selectNetwork({ network: 'mainnet', mainnetAck: MAINNET_ACK_TOKEN });
  assert.equal(s.network, 'mainnet');
  assert.equal(s.mainnetEnabled, true);
  assert.equal(s.realFunds, true);
  assert.match(s.banner, /MAINNET.*REAL FUNDS/);
});

test('local services bind to loopback by default; non-loopback is refused without opt-in (REQ-APP-106)', () => {
  assert.equal(resolveBindHost(), '127.0.0.1');
  assert.equal(resolveBindHost({ host: '::1' }), '::1');
  assert.equal(isLoopback('127.0.0.1'), true);
  assert.equal(isLoopback('0.0.0.0'), false);
  assert.throws(() => resolveBindHost({ host: '0.0.0.0' }), /non-loopback/);
  assert.equal(resolveBindHost({ host: '192.168.1.5', allowNonLoopback: true }), '192.168.1.5');
});
