/**
 * Real-value production-readiness gate (audit #34). A fully-configured mainnet deployment is ready;
 * EACH missing production invariant fails closed (network ack, signing mandatory, production sighash,
 * real custody, managed capability secret, loopback bind). A play/regtest config is ready without
 * real-value invariants (no real funds at risk).
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { evaluateRealValueReadiness, assertRealValueReady, type RealValueConfig } from '../src/production-readiness.ts';
import { MAINNET_ACK_TOKEN } from '../src/network-gate.ts';

/** A complete, production-ready mainnet configuration. */
const READY: RealValueConfig = {
  network: 'mainnet',
  mainnetAck: MAINNET_ACK_TOKEN,
  signingRequired: true,
  sighash: 'bip143-forkid',
  custody: 'software',
  relaySecretConfigured: true,
  bindHost: '127.0.0.1',
};

test('a fully-configured mainnet deployment is production-ready (audit #34)', () => {
  const r = evaluateRealValueReadiness(READY);
  assert.equal(r.ready, true, `should be ready: ${r.failures.join('; ')}`);
  assert.equal(r.realFunds, true, 'mainnet with ack puts real funds at risk');
  assert.doesNotThrow(() => assertRealValueReady(READY));
});

test('each missing production invariant fails closed for real funds (audit #34)', () => {
  const cases: Array<{ name: string; mut: Partial<RealValueConfig>; reason: RegExp }> = [
    { name: 'no mainnet ack', mut: { mainnetAck: 'wrong' }, reason: /network/ },
    { name: 'unsigned play allowed', mut: { signingRequired: false }, reason: /signing-mandatory/ },
    { name: 'simplified sighash', mut: { sighash: 'simplified' }, reason: /production-sighash/ },
    { name: 'fake custody', mut: { custody: 'fake' }, reason: /real-custody/ },
    { name: 'no custody', mut: { custody: 'none' }, reason: /real-custody/ },
    { name: 'no managed secret', mut: { relaySecretConfigured: false }, reason: /relay-secret/ },
    { name: 'non-loopback bind', mut: { bindHost: '0.0.0.0' }, reason: /bind-host/ },
  ];
  for (const c of cases) {
    const cfg = { ...READY, ...c.mut };
    const r = evaluateRealValueReadiness(cfg);
    assert.equal(r.ready, false, `"${c.name}" must NOT be ready`);
    assert.ok(r.failures.some((f) => c.reason.test(f)), `"${c.name}" failure should mention ${c.reason}: ${r.failures.join('; ')}`);
    assert.throws(() => assertRealValueReady(cfg), /NOT production-ready/, `assert must throw for "${c.name}"`);
  }
});

test('a non-loopback bind IS allowed when explicitly opted in (audit #34)', () => {
  const r = evaluateRealValueReadiness({ ...READY, bindHost: '0.0.0.0', allowNonLoopback: true });
  assert.equal(r.ready, true, `explicit non-loopback opt-in should be ready: ${r.failures.join('; ')}`);
});

test('a play/regtest config is ready without real-value invariants (no real funds)', () => {
  // Deliberately weak (unsigned, simplified, fake custody) — acceptable because NO real value is at risk.
  const play: RealValueConfig = { network: 'play-regtest', signingRequired: false, sighash: 'simplified', custody: 'fake', relaySecretConfigured: false };
  const r = evaluateRealValueReadiness(play);
  assert.equal(r.realFunds, false, 'play-money puts no real funds at risk');
  assert.equal(r.ready, true, 'a valid play/regtest selection is ready (no real-value invariants required)');
  assert.doesNotThrow(() => assertRealValueReady(play));
});

test('requesting mainnet without the ack is never real-funds and never ready', () => {
  // Built WITHOUT mainnetAck (omitted, not undefined — exactOptionalPropertyTypes).
  const noAck: RealValueConfig = { network: 'mainnet', signingRequired: true, sighash: 'bip143-forkid', custody: 'software', relaySecretConfigured: true, bindHost: '127.0.0.1' };
  const r = evaluateRealValueReadiness(noAck);
  assert.equal(r.realFunds, false, 'mainnet without ack must NOT be treated as real funds');
  assert.equal(r.ready, false, 'mainnet without ack must not be ready');
});
