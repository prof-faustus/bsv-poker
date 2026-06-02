/**
 * Accessibility labels (REQ-APP-054). Screen-reader / non-visual text for cards, seats, and actions.
 * Card identity is conveyed by RANK + SUIT WORDS — never by colour alone — so the game is fully
 * playable without colour perception (suits "clubs/diamonds/hearts/spades", not red/black).
 */

import { type Card, cardRank, cardSuit, isCard } from '@bsv-poker/protocol-types';

const RANK_WORDS = ['Two', 'Three', 'Four', 'Five', 'Six', 'Seven', 'Eight', 'Nine', 'Ten', 'Jack', 'Queen', 'King', 'Ace'];
const SUIT_WORDS = ['clubs', 'diamonds', 'hearts', 'spades'];

/** e.g. "Ace of spades" — colour-independent spoken text. */
export function accessibleCardLabel(card: Card): string {
  if (!isCard(card)) throw new RangeError(`card out of range: ${card}`);
  return `${RANK_WORDS[cardRank(card)]} of ${SUIT_WORDS[cardSuit(card)]}`;
}

export function accessibleSeatLabel(seat: number, seatCount: number): string {
  return `Seat ${seat + 1} of ${seatCount}`;
}

export function accessibleActionLabel(kind: string, amount?: number): string {
  const verb = kind.charAt(0).toUpperCase() + kind.slice(1);
  return amount !== undefined ? `${verb} ${amount}` : verb;
}
