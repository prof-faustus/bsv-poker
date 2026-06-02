/**
 * Side-pot computation — core §5.5 / §19.B. Deterministic (P2). Includes the conservation
 * assertion (REQ-ENG): Σ pot.amount = Σ contrib (a violation is a defect).
 *
 * Odd-chip / split rule — core §5.5.1, REQ-POKER-013: chips divide as evenly as granularity
 * allows; odd chip(s) go to the tied winner closest to the LEFT OF THE BUTTON. NO suit
 * precedence inside this award (suit tiebreak is a house-rule flag handled by the caller,
 * never here, never in hand evaluation).
 */

import type { Pot } from '@bsv-poker/protocol-types';

export interface SeatContribution {
  readonly seat: number;
  readonly contrib: number;
  readonly folded: boolean;
}

/** Build main + ordered side pots from per-seat contributions (§19.B). */
export function computePots(seats: readonly SeatContribution[]): Pot[] {
  const totalContrib = seats.reduce((s, p) => s + p.contrib, 0);

  // 1. sorted distinct positive contribution levels
  const levels = [...new Set(seats.map((p) => p.contrib).filter((c) => c > 0))].sort(
    (a, b) => a - b,
  );

  const pots: Pot[] = [];
  let prev = 0;
  for (const L of levels) {
    const increment = L - prev;
    const contributors = seats.filter((p) => p.contrib >= L);
    const amount = increment * contributors.length;
    const eligible = contributors.filter((p) => !p.folded).map((p) => p.seat);
    pots.push({ amount, eligible });
    prev = L;
  }

  // 5. conservation assertion
  const potSum = pots.reduce((s, p) => s + p.amount, 0);
  if (potSum !== totalContrib) {
    throw new Error(`pot conservation violated: Σpots=${potSum} Σcontrib=${totalContrib}`);
  }
  return pots;
}

/**
 * Award a single pot to its winner(s) given a comparator over eligible seats and the button.
 * `compareSeats(a,b)` returns +1 if a beats b, -1 if b beats a, 0 tie (best hand wins).
 * Ties split evenly; odd chips go left-of-button (REQ-POKER-013). `seatOrderFromButton`
 * lists seats in clockwise order starting immediately left of the button — used to assign
 * odd chips deterministically.
 */
export function awardPot(
  pot: Pot,
  compareSeats: (a: number, b: number) => -1 | 0 | 1,
  seatOrderFromButton: readonly number[],
): Map<number, number> {
  const result = new Map<number, number>();
  if (pot.eligible.length === 0) return result;

  // Find the best hand among eligible seats.
  let winners: number[] = [pot.eligible[0]!];
  for (let i = 1; i < pot.eligible.length; i++) {
    const s = pot.eligible[i]!;
    const c = compareSeats(s, winners[0]!);
    if (c > 0) winners = [s];
    else if (c === 0) winners.push(s);
  }

  if (winners.length === 1) {
    result.set(winners[0]!, pot.amount);
    return result;
  }

  // Split evenly; distribute odd chips starting left-of-button among tied winners.
  const base = Math.floor(pot.amount / winners.length);
  let remainder = pot.amount - base * winners.length;
  for (const w of winners) result.set(w, base);
  // Order tied winners by their position left-of-button.
  const ordered = seatOrderFromButton.filter((s) => winners.includes(s));
  for (const s of ordered) {
    if (remainder <= 0) break;
    result.set(s, (result.get(s) ?? 0) + 1);
    remainder--;
  }
  return result;
}
