import { test } from 'node:test';
import assert from 'node:assert/strict';
import { validateOnRead, isPersistedRecord, readBatch } from '../src/persistence.ts';

test('validateOnRead accepts a well-formed record (REQ-APP-132)', () => {
  const r = validateOnRead({ kind: 'transaction', id: 'tx1', payload: { txid: 'ab' } }, isPersistedRecord);
  assert.equal(r.ok, true);
});

test('a corrupt record is QUARANTINED, not silently dropped or trusted', () => {
  const r = validateOnRead({ kind: 'bogus', id: '', payload: null }, isPersistedRecord);
  assert.equal(r.ok, false);
  if (!r.ok) {
    assert.deepEqual(r.quarantined, { kind: 'bogus', id: '', payload: null });
    assert.match(r.reason, /schema/);
  }
});

test('readBatch partitions valid records from quarantined ones (no silent loss)', () => {
  const { records, quarantined } = readBatch([
    { kind: 'table', id: 't1', payload: {} },
    { kind: 'player', id: 'p0', payload: { seat: 0 } },
    'not even an object',
    { kind: 'card-lineage', id: '', payload: {} }, // empty id → quarantine
  ]);
  assert.equal(records.length, 2);
  assert.equal(quarantined.length, 2, 'every bad record is accounted for, none lost');
});

import { applyRetention, DEFAULT_RETAINED_HANDS } from '../src/persistence.ts';

test('transcript retention keeps the most recent N hands (REQ-APP-133)', () => {
  const hands = Array.from({ length: 250 }, (_, i) => i);
  const kept = applyRetention(hands, 100);
  assert.equal(kept.length, 100);
  assert.equal(kept[0], 150, 'oldest kept is hand 150');
  assert.equal(kept[99], 249, 'newest hand retained');
  assert.equal(applyRetention([1, 2, 3], 10).length, 3, 'keeps all when fewer than N');
  assert.equal(DEFAULT_RETAINED_HANDS, 100);
});
