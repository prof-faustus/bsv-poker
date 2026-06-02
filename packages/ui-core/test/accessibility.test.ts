import { test } from 'node:test';
import assert from 'node:assert/strict';
import { accessibleCardLabel, accessibleSeatLabel, accessibleActionLabel } from '../src/view-models/accessibility.ts';

test('card labels are colour-independent rank+suit words (REQ-APP-054)', () => {
  assert.equal(accessibleCardLabel(0), 'Two of clubs'); // rank 0, suit 0
  assert.equal(accessibleCardLabel(51), 'Ace of spades'); // rank 12, suit 3
  // No colour words anywhere across the whole deck.
  for (let c = 0; c < 52; c++) {
    const label = accessibleCardLabel(c).toLowerCase();
    assert.ok(!/\b(red|black)\b/.test(label), `card ${c} label leaks colour: ${label}`);
  }
});

test('diamonds and hearts are distinguishable by name, not colour (colour-blind safe)', () => {
  // 4 of diamonds vs 4 of hearts: same rank, would be same colour family on a screen.
  const diamonds = accessibleCardLabel(2 * 4 + 1); // rank 2, suit 1
  const hearts = accessibleCardLabel(2 * 4 + 2); // rank 2, suit 2
  assert.notEqual(diamonds, hearts);
  assert.match(diamonds, /diamonds/);
  assert.match(hearts, /hearts/);
});

test('every distinct card has a distinct label', () => {
  const labels = new Set<string>();
  for (let c = 0; c < 52; c++) labels.add(accessibleCardLabel(c));
  assert.equal(labels.size, 52);
});

test('seat and action labels are descriptive text', () => {
  assert.equal(accessibleSeatLabel(0, 6), 'Seat 1 of 6');
  assert.equal(accessibleActionLabel('raise', 200), 'Raise 200');
  assert.equal(accessibleActionLabel('check'), 'Check');
});
