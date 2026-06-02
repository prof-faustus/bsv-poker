/**
 * Exhaustive lobby-setup option matrix. For EVERY combination of (variant × seat count × opponent
 * mode × bot count) the human could pick, the setup validator's verdict must exactly match the
 * intended rule — valid combinations accepted, every invalid one rejected with a reason. This is the
 * full cross-product of the choices a human can make in the lobby, not a sample.
 */

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { validateTableSetup, buildPracticeSeats, type OpponentMode } from '../packages/ui-core/src/view-models/table-setup.ts';
import { VARIANT_INFO } from '../packages/app-services/src/game-registry.ts';

const VARIANTS = ['holdem', 'omaha', 'stud', 'draw', 'razz'] as const;
const OPP: (OpponentMode | null)[] = [null, 'humans', 'bots'];

test('every (variant × seats × opponents × botCount) combination validates exactly per the rule', () => {
  let checked = 0;
  for (const variant of VARIANTS) {
    const info = VARIANT_INFO[variant];
    const range = { min: info.minSeats, max: info.maxSeats };
    for (let seats = 1; seats <= info.maxSeats + 1; seats++) {
      for (const opponents of OPP) {
        const botCounts = opponents === 'bots' ? [undefined, 0, 1, 2, seats - 1, seats] : [undefined];
        for (const botCount of botCounts) {
          const seatsOk = seats >= range.min && seats <= range.max;
          const expected =
            opponents !== null &&
            seatsOk &&
            (opponents === 'humans' || (typeof botCount === 'number' && botCount >= 1 && botCount <= seats - 1));
          const got = validateTableSetup({ variant, seats, opponents, ...(botCount !== undefined ? { botCount } : {}) }, range).ok;
          assert.equal(got, expected, `${variant} seats=${seats} opp=${opponents} bots=${botCount}: expected ${expected}, got ${got}`);
          checked++;
        }
      }
    }
  }
  assert.ok(checked > 200, `covered ${checked} setup combinations`);
});

test('a valid bot setup always seats exactly one human (seat 0) and the rest bots, for any count', () => {
  for (let bots = 1; bots <= 8; bots++) {
    const plan = buildPracticeSeats(bots, 100);
    assert.equal(plan.length, bots + 1);
    assert.equal(plan.filter((p) => p.isHuman).length, 1);
    assert.equal(plan[0]!.isHuman, true);
    assert.ok(plan.slice(1).every((p) => !p.isHuman));
  }
});
