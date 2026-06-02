import { test } from 'node:test';
import assert from 'node:assert/strict';
import { PLANNED_GAMES, createPlannedGame, SUPPORTED_VARIANTS, createGameModule } from '../src/game-registry.ts';

test('Blackjack is RESERVED as a planned game with its variant profile (REQ-APP-219)', () => {
  const bj = PLANNED_GAMES.find((g) => g.id === 'blackjack');
  assert.ok(bj, 'blackjack registry entry exists');
  assert.equal(bj!.status, 'planned');
  assert.equal(bj!.dealerArea, true);
  assert.equal(bj!.interPlayerPot, false, 'player-vs-dealer, no inter-player pot');
  for (const c of ['hit', 'stand', 'double', 'split', 'insurance']) assert.ok(bj!.controls.includes(c));
});

test('Blackjack is NOT a playable variant and cannot be instantiated (fail-closed, P7/P8)', () => {
  assert.ok(!(SUPPORTED_VARIANTS as readonly string[]).includes('blackjack'), 'not in the playable set');
  assert.throws(() => createPlannedGame('blackjack'), /not yet playable/);
  // The poker factory also refuses it — no silent fallback to a poker model (core D7).
  assert.throws(() => createGameModule('blackjack' as never, []), /no module for variant/);
});
