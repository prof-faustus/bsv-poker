/**
 * Stated boundary/limit assertions (REQ-ENG-004): every P7/P8 boundary the system claims is enforced
 * in source and asserted here, so a change can't silently paper over it. This test aggregates the
 * load-bearing guards: the mainnet gate, loopback binding, the seat range (2–9), trust-boundary
 * envelope validation, and log redaction.
 */

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { selectNetwork, resolveBindHost, validateEnvelope, redact, REDACTED } from '../packages/app-services/src/index.ts';
import { seatPositions } from '../packages/ui-core/src/view-models/index.ts';

test('boundary: mainnet is off by default and refused without the explicit token', () => {
  assert.equal(selectNetwork().mainnetEnabled, false);
  assert.throws(() => selectNetwork({ network: 'mainnet' }));
});

test('boundary: local services bind loopback-only unless explicitly allowed', () => {
  assert.equal(resolveBindHost(), '127.0.0.1');
  assert.throws(() => resolveBindHost({ host: '0.0.0.0' }));
});

test('boundary: seat count is clamped to 2..9 (core D2)', () => {
  assert.equal(seatPositions({ count: 20, heroSeat: 0 }).length, 9);
  assert.equal(seatPositions({ count: 1, heroSeat: 0 }).length, 2);
});

test('boundary: unrecognized inbound envelopes are rejected', () => {
  assert.equal(validateEnvelope({ t: 'spoof', seat: 0, hand: 0 }), null);
});

test('boundary: key material is never emitted (redaction)', () => {
  assert.equal((redact({ priv: 'ff'.repeat(32) }) as Record<string, unknown>).priv, REDACTED);
});
