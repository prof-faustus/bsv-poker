/**
 * Shared presentational primitives (REQ-APP-052) — framework-free (vanilla DOM via `../dom`). Pure
 * render of props: NO business logic, no legality computation. Suits carry a letter/word glyph so no
 * information is colour-only (a11y, §A3.5 / core §5.5.1 no suit precedence).
 *
 * `playingCard` renders a realistic card face (rounded white rect, rank+suit in the corners, a centre
 * pip); `cardBack` a patterned back for concealed cards; `cardChip` a small face alias used by the
 * showdown/settlement panels. Each returns a real DOM node. Text is set via the DOM helper's
 * textContent path (never innerHTML) so card/label strings cannot inject markup.
 */
import { el, type Child } from '../dom.ts';
import type { CardVM } from '../view-models/table.ts';

const SUIT_GLYPH: Record<string, string> = { c: '♣', d: '♦', h: '♥', s: '♠' };
const SUIT_NAME: Record<string, string> = { c: 'clubs', d: 'diamonds', h: 'hearts', s: 'spades' };
const SUIT_RED = new Set(['d', 'h']);

export type CardSize = 'sm' | 'md' | 'lg';

const SIZES: Record<CardSize, { w: number; h: number; rank: number; pip: number }> = {
  sm: { w: 34, h: 48, rank: 13, pip: 16 },
  md: { w: 46, h: 64, rank: 16, pip: 24 },
  lg: { w: 58, h: 82, rank: 20, pip: 32 },
};

export function playingCard(card: CardVM, size: CardSize = 'md'): HTMLElement {
  const s = SIZES[size];
  const red = SUIT_RED.has(card.suit);
  const glyph = SUIT_GLYPH[card.suit] ?? '?';
  const color = red ? '#c4122f' : '#16181d';
  const corner = (): HTMLElement =>
    el('span', { style: { display: 'flex', flexDirection: 'column', alignItems: 'center', lineHeight: '0.95' } },
      el('span', { style: { fontSize: `${s.rank}px`, fontWeight: '800' } }, card.rank),
      el('span', { style: { fontSize: `${s.rank - 2}px` } }, glyph),
    );
  return el('span', {
    role: 'img',
    'aria-label': `${card.rank} of ${SUIT_NAME[card.suit] ?? card.suit}`,
    style: {
      position: 'relative', display: 'inline-flex', width: `${s.w}px`, height: `${s.h}px`, borderRadius: '7px',
      background: 'linear-gradient(180deg,#ffffff,#f1f1ee)', border: '1px solid #c9c9c0',
      boxShadow: '0 2px 4px rgba(0,0,0,0.45), inset 0 0 0 1px rgba(255,255,255,0.6)', color,
      fontFamily: 'Georgia, "Times New Roman", serif', margin: '2px', userSelect: 'none', flex: '0 0 auto',
    },
  },
    el('span', { style: { position: 'absolute', top: '3px', left: '4px' } }, corner()),
    el('span', { 'aria-hidden': 'true', style: { position: 'absolute', inset: '0', display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: `${s.pip}px` } }, glyph),
    el('span', { style: { position: 'absolute', bottom: '3px', right: '4px', transform: 'rotate(180deg)' } }, corner()),
  );
}

/** Back-compat: a small face card used by showdown/settlement panels. */
export function cardChip(card: CardVM): HTMLElement {
  return playingCard(card, 'sm');
}

export function cardBack(size: CardSize = 'md'): HTMLElement {
  const s = SIZES[size];
  return el('span', {
    'aria-label': 'concealed card', role: 'img',
    style: {
      display: 'inline-block', width: `${s.w}px`, height: `${s.h}px`, borderRadius: '7px',
      background: 'repeating-linear-gradient(45deg,#39508f,#39508f 5px,#2a3c6e 5px,#2a3c6e 10px)',
      border: '1px solid #1c2747', boxShadow: '0 2px 4px rgba(0,0,0,0.45), inset 0 0 0 2px rgba(255,255,255,0.18)',
      margin: '2px', flex: '0 0 auto',
    },
  });
}

/** A single casino chip (decorative). `value` is shown for readability. */
export function chip(value?: number, color = '#c4122f', size = 26): HTMLElement {
  return el('span', {
    'aria-hidden': 'true',
    style: {
      display: 'inline-flex', alignItems: 'center', justifyContent: 'center', width: `${size}px`, height: `${size}px`,
      borderRadius: '50%', background: `radial-gradient(circle at 50% 40%, ${color}, ${shade(color)})`,
      border: '2px dashed rgba(255,255,255,0.7)', color: '#fff', fontSize: `${Math.max(8, size * 0.32)}px`,
      fontWeight: '700', boxShadow: '0 1px 2px rgba(0,0,0,0.5)',
    },
  }, value ?? '');
}

/** A labelled chip stack — a chip plus an amount, for pot/bet displays. */
export function chipStack(amount: number, label?: string, color?: string): HTMLElement {
  return el('span', {
    style: {
      display: 'inline-flex', alignItems: 'center', gap: '6px', background: 'rgba(0,0,0,0.45)', borderRadius: '14px',
      padding: '2px 10px 2px 4px', color: '#fff', fontWeight: '700', fontSize: '13px',
    },
  }, chip(undefined, color, 22), el('span', {}, `${label ? `${label} ` : ''}${amount}`));
}

function shade(hex: string): string {
  // Darken a #rrggbb by ~35% for the chip's lower gradient stop; fall back to a known safe colour if
  // the input is not a strict 6-digit hex (so parseInt cannot mis-read a malformed value — CWE-1025).
  if (!/^#[0-9a-fA-F]{6}$/.test(hex)) return '#7a0c1d';
  const n = parseInt(hex.slice(1), 16);
  const r = Math.round(((n >> 16) & 255) * 0.65);
  const g = Math.round(((n >> 8) & 255) * 0.65);
  const b = Math.round((n & 255) * 0.65);
  return `rgb(${r},${g},${b})`;
}

export function banner(children: Child | Child[], tone: 'warn' | 'info' | 'error' = 'warn'): HTMLElement {
  const bg = tone === 'error' ? '#7a1f1f' : tone === 'info' ? '#1f4d7a' : '#7a5a1f';
  const kids = Array.isArray(children) ? children : [children];
  return el('div', { role: 'status', style: { background: bg, color: '#fff', padding: '6px 12px', borderRadius: '4px', fontSize: '13px' } }, ...kids);
}
