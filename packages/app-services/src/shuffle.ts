/**
 * Browser-safe deck shuffle for Phase-1 play-money/regtest hot-seat play.
 *
 * HONEST SCOPE (§A2.3, build brief): this is a single-party Fisher–Yates seeded by
 * crypto.getRandomValues. It is NOT the multi-party mental-poker shuffle (core §4) — that
 * commit-reveal protocol with no single party knowing the order is the Node SDK / crypto-layer
 * path and is out of scope for the browser bundle (crypto-mentalpoker uses node:crypto). Here a
 * single client deals to itself + a bot, so a trustless shuffle is neither possible nor claimed.
 */

import { NUM_CARDS, type Card } from '@bsv-poker/protocol-types';

/** Deterministic Fisher–Yates using an injected 0..1 RNG (so tests can seed it). */
export function shuffleWith(rng: () => number): Card[] {
  const deck: Card[] = [];
  for (let c = 0; c < NUM_CARDS; c++) deck.push(c);
  for (let i = deck.length - 1; i > 0; i--) {
    const j = Math.floor(rng() * (i + 1));
    const tmp = deck[i]!;
    deck[i] = deck[j]!;
    deck[j] = tmp;
  }
  return deck;
}

/**
 * A uniform 0..1 RNG backed by the platform CSPRNG. Fail-CLOSED (CWE-338): if no CSPRNG is present
 * there is NO Math.random fallback — a deck must never be shuffled with a predictable source, even
 * for play-money. Node 24 and every modern browser provide crypto.getRandomValues.
 */
export function cryptoRng(): () => number {
  const g = globalThis as { crypto?: { getRandomValues?: (a: Uint32Array) => Uint32Array } };
  const c = g.crypto;
  if (!c || typeof c.getRandomValues !== 'function') {
    throw new Error('no CSPRNG available (crypto.getRandomValues missing) — refusing to shuffle');
  }
  return () => {
    const buf = new Uint32Array(1);
    c.getRandomValues!(buf);
    return buf[0]! / 0x100000000;
  };
}

/** Unbiased Fisher–Yates over a uint32 source, rejection-sampling each step (exact sampler). */
export function shuffleU32(draw: () => number): Card[] {
  const deck: Card[] = [];
  for (let c = 0; c < NUM_CARDS; c++) deck.push(c);
  for (let i = deck.length - 1; i > 0; i--) {
    const bound = i + 1;
    const limit = Math.floor(0x100000000 / bound) * bound;
    let r = draw();
    while (r >= limit) r = draw();
    const j = r % bound;
    [deck[i], deck[j]] = [deck[j]!, deck[i]!];
  }
  return deck;
}

/** A uint32 source backed by the platform CSPRNG. Fail-CLOSED (CWE-338): no Math.random fallback. */
export function cryptoU32(): () => number {
  const g = globalThis as { crypto?: { getRandomValues?: (a: Uint32Array) => Uint32Array } };
  const c = g.crypto;
  if (!c || typeof c.getRandomValues !== 'function') {
    throw new Error('no CSPRNG available (crypto.getRandomValues missing) — refusing to shuffle');
  }
  return () => { const buf = new Uint32Array(1); c.getRandomValues!(buf); return buf[0]!; };
}

/** Shuffle a fresh 52-card deck using the platform CSPRNG (regtest/play-money), unbiased sampler. */
export function shuffleDeck(): Card[] {
  return shuffleU32(cryptoU32());
}
