/**
 * Exhaustive play coverage: EVERY variant × EVERY legal seat count, played to a settled hand across
 * many distinct deals. Asserts each hand (a) reaches showdown/settlement without stalling and (b)
 * conserves chips exactly (no chip created or destroyed). This is the robustness sweep across the
 * whole user-reachable game matrix — not a single happy path.
 */

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { playOfflineHand, offlineRuleset, seededShuffle } from '../packages/app-services/src/index.ts';
import { VARIANT_INFO } from '../packages/app-services/src/game-registry.ts';
import type { Variant } from '../packages/protocol-types/src/index.ts';

const VARIANTS: Variant[] = ['holdem', 'omaha', 'stud', 'draw', 'razz'];
const STACK = 100;
const DEALS = 30; // distinct shuffles per (variant, seat-count)

function deckForSeed(variant: string, seed: number): number[] {
  const b = new Uint8Array(32);
  b[0] = seed & 0xff;
  b[1] = (seed >> 8) & 0xff;
  b[2] = variant.charCodeAt(0);
  return seededShuffle(b, 52);
}

for (const variant of VARIANTS) {
  const info = VARIANT_INFO[variant];
  for (let seats = info.minSeats; seats <= info.maxSeats; seats++) {
    test(`${variant} ${seats}-handed: ${DEALS} deals all settle + conserve every chip`, () => {
      const ruleset = offlineRuleset(variant, seats);
      const seatInits = Array.from({ length: seats }, (_, i) => ({ seat: i, stack: STACK }));
      const bankroll = seats * STACK;
      for (let seed = 0; seed < DEALS; seed++) {
        const state = playOfflineHand(variant, ruleset, seatInits, deckForSeed(variant, seed));
        assert.equal(state.handComplete, true, `${variant} ${seats}p seed ${seed}: hand stalled (not complete)`);
        // At settlement every pot is distributed into stacks, so chips are conserved in the stacks.
        const chips = state.seats.reduce((s, x) => s + x.stack, 0);
        assert.equal(chips, bankroll, `${variant} ${seats}p seed ${seed}: chip conservation broken (${chips} ≠ ${bankroll})`);
        const negative = state.seats.find((s) => s.stack < 0);
        assert.equal(negative, undefined, `${variant} ${seats}p seed ${seed}: a seat went negative`);
      }
    });
  }
}
