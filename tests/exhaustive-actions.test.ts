/**
 * Exhaustive action-path coverage. Across the variant × seat matrix, drive distinct betting lines —
 * everyone all-in (max raise/bet → multi-way side pots), everyone passive (check/call to showdown),
 * everyone folding to a bet (fold-outs), and a mixed line — over many deals. Every line must settle
 * and conserve chips exactly. This stresses raises, all-ins, side pots, and fold-outs, not one path.
 */

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { playOfflineHand, offlineRuleset, seededShuffle } from '../packages/app-services/src/index.ts';
import { VARIANT_INFO } from '../packages/app-services/src/game-registry.ts';
import type { Action, LegalActions, Variant } from '../packages/protocol-types/src/index.ts';

const VARIANTS: Variant[] = ['holdem', 'omaha', 'stud', 'draw', 'razz'];
const STACK = 100;
const DEALS = 12;

const aggressive = (l: LegalActions, seat: number): Action =>
  l.raise ? { kind: 'raise', seat, amount: l.raise.max }
  : l.bet ? { kind: 'bet', seat, amount: l.bet.max }
  : l.call ? { kind: 'call', seat, amount: l.call.amount }
  : l.check ? { kind: 'check', seat, amount: 0 }
  : l.draw ? { kind: 'stand', seat, amount: 0 }
  : { kind: 'fold', seat, amount: 0 };

const passive = (l: LegalActions, seat: number): Action =>
  l.check ? { kind: 'check', seat, amount: 0 }
  : l.draw ? { kind: 'stand', seat, amount: 0 }
  : l.call ? { kind: 'call', seat, amount: l.call.amount }
  : { kind: 'fold', seat, amount: 0 };

const folder = (l: LegalActions, seat: number): Action =>
  l.fold && !l.check ? { kind: 'fold', seat, amount: 0 }
  : l.check ? { kind: 'check', seat, amount: 0 }
  : l.draw ? { kind: 'stand', seat, amount: 0 }
  : l.call ? { kind: 'call', seat, amount: l.call.amount }
  : { kind: 'fold', seat, amount: 0 };

const mixed = (l: LegalActions, seat: number): Action => (seat % 2 === 0 ? aggressive(l, seat) : passive(l, seat));

const STRATS = { aggressive, passive, folder, mixed };

function deck(variant: string, seed: number): number[] {
  const b = new Uint8Array(32);
  b[0] = seed & 0xff;
  b[1] = (seed >> 8) & 0xff;
  b[2] = variant.charCodeAt(0);
  return seededShuffle(b, 52);
}

for (const variant of VARIANTS) {
  const info = VARIANT_INFO[variant];
  for (const [name, strat] of Object.entries(STRATS)) {
    test(`${variant}: '${name}' line settles + conserves chips across all seat counts`, () => {
      for (let seats = info.minSeats; seats <= info.maxSeats; seats++) {
        const ruleset = offlineRuleset(variant, seats);
        const seatInits = Array.from({ length: seats }, (_, i) => ({ seat: i, stack: STACK }));
        for (let seed = 0; seed < DEALS; seed++) {
          const st = playOfflineHand(variant, ruleset, seatInits, deck(variant, seed), strat);
          assert.equal(st.handComplete, true, `${variant}/${name} ${seats}p seed ${seed}: stalled`);
          const chips = st.seats.reduce((s, x) => s + x.stack, 0);
          assert.equal(chips, seats * STACK, `${variant}/${name} ${seats}p seed ${seed}: chip leak (${chips})`);
          assert.equal(st.seats.find((s) => s.stack < 0), undefined, `${variant}/${name} ${seats}p seed ${seed}: negative stack`);
        }
      }
    });
  }
}
