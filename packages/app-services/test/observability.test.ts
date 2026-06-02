import { test } from 'node:test';
import assert from 'node:assert/strict';
import { Logger, MemorySink, redact, REDACTED } from '../src/observability.ts';

test('logs are structured + levelled, per component, and honour the minimum level (REQ-APP-120)', () => {
  const sink = new MemorySink();
  const log = new Logger('app-services', sink, 'info');
  log.debug('noisy'); // below min → dropped
  log.info('hand started', { gid: 'a1', seat: 0 });
  log.error('settlement failed', { reason: 'timeout' });
  assert.equal(sink.records.length, 2);
  assert.equal(sink.records[0]!.component, 'app-services');
  assert.equal(sink.records[0]!.level, 'info');
  assert.deepEqual(sink.records[0]!.fields, { gid: 'a1', seat: 0 });
  assert.equal(sink.records[1]!.level, 'error');
});

test('redaction strips key material recursively (REQ-APP-124)', () => {
  const out = redact({
    gid: 'a1',
    priv: 'deadbeef'.repeat(8),
    custody: { seed: '00'.repeat(32), scalar: 'ff'.repeat(32), pubKey: '02abc' },
    players: [{ name: 'p0', secret: 'hunter2' }],
  }) as Record<string, unknown>;
  assert.equal(out.gid, 'a1', 'non-sensitive fields pass through');
  assert.equal(out.priv, REDACTED);
  const custody = out.custody as Record<string, unknown>;
  assert.equal(custody.seed, REDACTED);
  assert.equal(custody.scalar, REDACTED);
  assert.equal(custody.pubKey, '02abc', 'public keys are not redacted');
  assert.equal((out.players as Record<string, unknown>[])[0]!.secret, REDACTED, 'redaction reaches into arrays');
});

test('a logger NEVER emits key material even when a caller passes it', () => {
  const sink = new MemorySink();
  new Logger('custody', sink, 'debug').debug('derived key', { privateKey: 'cafe'.repeat(16), gid: 'a1' });
  const serialized = JSON.stringify(sink.records);
  assert.ok(!serialized.includes('cafecafe'), 'no private material in the emitted record');
  assert.ok(serialized.includes(REDACTED));
});
