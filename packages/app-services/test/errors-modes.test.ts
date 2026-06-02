import { test } from 'node:test';
import assert from 'node:assert/strict';
import { makeError, ERROR_CODES } from '../src/errors.ts';
import { resolveConnectionMode, reconcile } from '../src/network-modes.ts';

test('error taxonomy: each code maps to a category + recoverable flag (REQ-APP-110)', () => {
  assert.ok(ERROR_CODES.length >= 8);
  const e = makeError('PROTO_OUT_OF_TURN', 'seat 2 acted out of turn');
  assert.equal(e.category, 'protocol');
  assert.equal(e.recoverable, false);
  assert.equal(makeError('NET_DISCONNECTED').recoverable, true);
});

test('an unknown error code degrades to a non-recoverable internal error (fail-closed)', () => {
  const e = makeError('NOPE_NOT_A_CODE');
  assert.equal(e.code, 'INTERNAL');
  assert.equal(e.category, 'internal');
  assert.equal(e.recoverable, false);
});

test('connection modes: bundled-local loopback by default; remote relay when given (REQ-APP-040/041)', () => {
  assert.deepEqual(resolveConnectionMode(), { mode: 'bundled-local', base: 'http://127.0.0.1:8091', loopback: true });
  assert.equal(resolveConnectionMode({ relayUrl: 'https://relay.example.com' }).mode, 'remote-relay');
  assert.equal(resolveConnectionMode({ relayUrl: 'http://127.0.0.1:9000' }).loopback, true);
});

test('dual-path: canonical wins and a divergent speed reading is SURFACED, never overrides (REQ-APP-074)', () => {
  assert.deepEqual(reconcile({ speed: 'X', canonical: 'X' }), { value: 'X', conflict: false, source: 'canonical' });
  assert.deepEqual(reconcile({ speed: 'Y', canonical: 'X' }), { value: 'X', conflict: true, source: 'canonical' });
  assert.deepEqual(reconcile({ speed: 'P' }), { value: 'P', conflict: false, source: 'speed' });
  assert.deepEqual(reconcile<string>({}), { value: undefined, conflict: false, source: 'none' });
});

import { canCreateOrJoin } from '../src/network-modes.ts';

test('the lobby permits create/join ONLY when services are READY (REQ-APP-023)', () => {
  assert.equal(canCreateOrJoin('ready'), true);
  for (const s of ['init', 'start_indexer', 'start_relay', 'degraded', 'shutdown', 'fatal']) {
    assert.equal(canCreateOrJoin(s), false, `${s} must not permit table actions`);
  }
});
