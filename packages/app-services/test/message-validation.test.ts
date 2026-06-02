import { test } from 'node:test';
import assert from 'node:assert/strict';
import { validateEnvelope, parseAndValidate } from '../src/message-validation.ts';

test('valid commit/reveal/action envelopes are accepted (REQ-APP-103)', () => {
  assert.ok(validateEnvelope({ t: 'commit', seat: 0, hand: 1, c: 'deadbeef' }));
  assert.ok(validateEnvelope({ t: 'reveal', seat: 2, hand: 1, r: 'cafe' }));
  assert.ok(validateEnvelope({ t: 'action', seat: 1, hand: 3, kind: 'raise', amount: 200 }));
  assert.ok(validateEnvelope({ t: 'action', seat: 1, hand: 3, kind: 'check' }));
});

test('unrecognized or malformed envelopes are REJECTED (never partially trusted)', () => {
  assert.equal(validateEnvelope({ t: 'evil', seat: 0, hand: 1 }), null, 'unknown kind');
  assert.equal(validateEnvelope({ t: 'commit', seat: -1, hand: 1, c: 'ab' }), null, 'bad seat');
  assert.equal(validateEnvelope({ t: 'commit', seat: 0, hand: 1.5, c: 'ab' }), null, 'non-integer hand');
  assert.equal(validateEnvelope({ t: 'commit', seat: 0, hand: 1 }), null, 'commit missing c');
  assert.equal(validateEnvelope({ t: 'reveal', seat: 0, hand: 1, r: 'nothex!' }), null, 'reveal non-hex');
  assert.equal(validateEnvelope({ t: 'action', seat: 0, hand: 1 }), null, 'action missing kind');
  assert.equal(validateEnvelope({ t: 'action', seat: 0, hand: 1, kind: 'raise', amount: Infinity }), null, 'non-finite amount');
  assert.equal(validateEnvelope(null), null);
  assert.equal(validateEnvelope('a string'), null);
});

test('parseAndValidate rejects bad JSON and bad envelopes from the wire', () => {
  assert.equal(parseAndValidate('{not json'), null);
  assert.equal(parseAndValidate(JSON.stringify({ t: 'action', seat: 0, hand: 0, kind: 'fold' }))?.kind, 'fold');
  assert.equal(parseAndValidate(JSON.stringify({ t: 'spoof' })), null);
});
