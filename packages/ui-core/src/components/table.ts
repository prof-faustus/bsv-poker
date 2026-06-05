/**
 * Table-screen presentational components (REQ-APP-052) — framework-free (vanilla DOM). All pure
 * render of view-model props. Explicit onClick/onInput handlers, NO <form> submit (REQ-UI-003). No
 * business logic: the action bar reads the legal-action descriptor from the engine and never computes
 * legality — the bet/raise slider bounds and quick-button amounts come from the pure bet-sizing
 * view-model which itself only clamps to the engine's legal range.
 *
 * `pokerTable` is the centrepiece: a green felt oval with the pot + community board in the middle and
 * seats positioned around the ellipse (see seatPositions in the table-layout view-model). It is
 * responsive (percentage-positioned) and AT-accessible (the to-act seat is announced via
 * aria-current; cards carry suit names + letters so nothing is colour-only).
 */
import { el } from '../dom.ts';
import { playingCard, cardBack, cardChip, banner, chipStack } from './primitives.ts';
import { seatPositions } from '../view-models/table-layout.ts';
import { sizerRange, clampToRange, quickButtons } from '../view-models/bet-sizing.ts';
import type { SeatVM, PotVM, ActionBarVM, TimerVM, TableViewModel } from '../view-models/table.ts';

export function mainnetBanner(regtest: boolean): HTMLElement {
  // REQ-VM-007 / §A3.5 — unmissable. Phase-1 is always regtest play-money.
  return banner(
    regtest ? 'REGTEST — play money. No real funds are at risk.' : 'MAINNET RESEARCH MODE — real value at risk.',
    regtest ? 'warn' : 'error',
  );
}

export function board(cards: TableViewModel['board']): HTMLElement {
  return el('div', { 'aria-label': 'community cards', style: { display: 'flex', minHeight: '64px', justifyContent: 'center', alignItems: 'center', gap: '2px' } },
    ...(cards.length === 0
      ? [el('span', { style: { color: 'rgba(255,255,255,0.55)', fontStyle: 'italic', fontSize: '13px' } }, '(no community cards yet)')]
      : cards.map((c) => playingCard(c, 'md'))),
  );
}

export function potDisplay(pots: readonly PotVM[], total: number): HTMLElement {
  const sidePots = pots.length > 1 ? pots : [];
  return el('div', { 'aria-label': 'pots', style: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '4px' } },
    chipStack(total, 'Pot', '#2e7d32'),
    sidePots.length > 0
      ? el('div', { style: { display: 'flex', gap: '6px', flexWrap: 'wrap', justifyContent: 'center' } },
          ...sidePots.map((p, i) => el('span', { title: `eligible: ${p.eligible.join(', ')}`, style: { fontSize: '11px', color: 'rgba(255,255,255,0.8)' } }, `${i === 0 ? 'Main' : `Side ${i}`}: ${p.amount}`)))
      : false,
  );
}

/** Cards a seat shows: hero face-up, everyone else face-down backs (custody boundary). */
function seatCards(seat: SeatVM): HTMLElement {
  const backs = Math.max(2, seat.holeCards.length || 2);
  return el('div', { 'aria-label': `seat ${seat.seat} cards`, style: { display: 'flex', justifyContent: 'center' } },
    ...(seat.isHero && seat.holeCards.length > 0
      ? seat.holeCards.map((c) => playingCard(c, 'sm'))
      : Array.from({ length: backs }, () => cardBack('sm'))),
  );
}

/** One seat pod placed on the rail. Shows name, stack, button, to-act ring, state + cards. */
function seatPod(seat: SeatVM, label: string, xPct: number, yPct: number): HTMLElement {
  return el('div', {
    'aria-current': seat.isToAct ? 'true' : undefined,
    style: { position: 'absolute', left: `${xPct}%`, top: `${yPct}%`, transform: 'translate(-50%, -50%)', width: '132px', display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '4px' },
  },
    seatCards(seat),
    el('div', {
      style: {
        width: '100%', textAlign: 'center', borderRadius: '10px', padding: '5px 6px',
        background: seat.isHero ? 'linear-gradient(180deg,#1c4a2e,#13301f)' : 'linear-gradient(180deg,#2a2a2e,#191919)',
        border: seat.isToAct ? '2px solid #ffd24d' : '1px solid rgba(255,255,255,0.18)',
        boxShadow: seat.isToAct ? '0 0 0 3px rgba(255,210,77,0.35), 0 0 14px rgba(255,210,77,0.5)' : '0 2px 6px rgba(0,0,0,0.5)',
        opacity: seat.folded ? '0.45' : '1', color: '#fff', fontSize: '12px',
      },
    },
      el('div', { style: { fontWeight: '700', display: 'flex', alignItems: 'center', justifyContent: 'center', gap: '4px' } },
        el('span', { style: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', maxWidth: '96px' } }, label),
        seat.isButton
          ? el('span', { 'aria-label': 'dealer button', style: { display: 'inline-flex', width: '16px', height: '16px', borderRadius: '50%', background: '#fff', color: '#111', fontSize: '9px', fontWeight: '800', alignItems: 'center', justifyContent: 'center', border: '1px solid #aaa' } }, 'D')
          : false,
      ),
      el('div', { style: { display: 'flex', alignItems: 'center', justifyContent: 'center', gap: '4px', marginTop: '2px' } },
        el('span', { 'aria-label': 'chip stack', style: { color: '#ffd24d', fontWeight: '700' } }, String(seat.stack)),
        seat.folded ? el('span', { style: { color: '#e88' } }, '· folded') : false,
        seat.allIn ? el('span', { style: { color: '#8ce' } }, '· all-in') : false,
      ),
    ),
    seat.committedThisRound > 0 ? el('div', { style: { marginTop: '2px' } }, chipStack(seat.committedThisRound, undefined, '#2e74c4')) : false,
  );
}

/** The realistic card table: a green felt oval, seats fanned around the rail, pot + board centre. */
export function pokerTable(vm: TableViewModel, seatLabel?: (seat: SeatVM) => string): HTMLElement {
  const label = seatLabel ?? ((s: SeatVM) => (s.isHero ? 'You' : 'Bot'));
  const order = vm.seats.map((s) => s.seat);
  const positions = seatPositions({ count: vm.seats.length, heroSeat: vm.heroSeat, seatOrder: order });
  const posBySeat = new Map(positions.map((p) => [p.seat, p]));

  return el('div', {
    role: 'group', 'aria-label': 'poker table',
    style: { position: 'relative', width: '100%', maxWidth: '820px', margin: '0 auto', aspectRatio: '16 / 10', background: 'radial-gradient(ellipse at center, #0d1117 0%, #05070b 100%)', borderRadius: '24px', padding: '8px' },
  },
    el('div', {
      style: { position: 'absolute', inset: '9%', borderRadius: '50%', background: 'radial-gradient(ellipse at 50% 38%, #2f9e57 0%, #1f7d42 55%, #145c30 100%)', border: '14px solid #5b3a1f', boxShadow: 'inset 0 0 40px rgba(0,0,0,0.55), 0 0 0 3px #3a2412, 0 10px 30px rgba(0,0,0,0.6)' },
    },
      el('div', { style: { position: 'absolute', top: '50%', left: '50%', transform: 'translate(-50%, -50%)', display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '8px', width: '70%' } },
        potDisplay(vm.pots, vm.totalPot),
        board(vm.board),
        el('div', { style: { fontSize: '11px', color: 'rgba(255,255,255,0.55)', textTransform: 'uppercase', letterSpacing: '1px' } }, vm.phase),
      ),
    ),
    ...vm.seats.map((s) => {
      const p = posBySeat.get(s.seat);
      return p ? seatPod(s, label(s), p.xPct, p.yPct) : false;
    }).filter((x): x is HTMLElement => x !== false),
  );
}

/** Legacy flat seat list — kept for back-compat (and as a responsive fallback). */
export function seatRing(seats: readonly SeatVM[], seatLabel?: (seat: SeatVM) => string): HTMLElement {
  const label = seatLabel ?? ((s: SeatVM) => (s.isHero ? '(you)' : '(bot)'));
  return el('div', { style: { display: 'flex', gap: '16px', flexWrap: 'wrap' } },
    ...seats.map((s) =>
      el('div', { 'aria-current': s.isToAct ? 'true' : undefined, style: { border: s.isToAct ? '2px solid #ffd24d' : '1px solid #555', borderRadius: '8px', padding: '8px', minWidth: '150px', opacity: s.folded ? '0.5' : '1', background: s.isHero ? '#13301f' : '#1a1a1a' } },
        el('div', { style: { fontWeight: '700' } }, `Seat ${s.seat} ${label(s)} ${s.isButton ? '(D)' : ''}`),
        el('div', {}, `Stack: ${s.stack}`),
        el('div', {}, `In front: ${s.committedThisRound}`),
        s.folded ? el('div', { style: { color: '#e88' } }, 'folded') : false,
        s.allIn ? el('div', { style: { color: '#8ce' } }, 'all-in') : false,
        el('div', { style: { display: 'flex' } }, ...(s.isHero && s.holeCards.length > 0 ? s.holeCards.map((c) => cardChip(c)) : [cardBack('sm'), cardBack('sm')])),
      )),
  );
}

export function timerBanner(timer: TimerVM): HTMLElement {
  // Surfaces the consequence/default text (core §11.4) — never hidden.
  return banner(el('span', { 'aria-live': 'polite' }, timer.consequenceText), 'info');
}

export interface ActionBarProps {
  readonly vm: ActionBarVM;
  readonly heroSeat: number;
  readonly betAmount: number;
  readonly onBetAmountChange: (n: number) => void;
  readonly onAction: (choice: 'fold' | 'check' | 'call' | 'bet' | 'raise', amount: number) => void;
  readonly pot?: number;
}

export function actionBar(p: ActionBarProps): HTMLElement {
  const { vm, betAmount, onBetAmountChange, onAction } = p;
  if (!vm.isHeroTurn) {
    return el('div', { role: 'group', 'aria-label': 'actions', style: { color: '#999', padding: '8px' } }, 'Not your turn — controls disabled.');
  }
  const legal = vm.legal;
  const range = sizerRange(legal);
  const toCall = legal.call ? legal.call.amount : 0;
  const quicks = quickButtons({ range, pot: p.pot ?? 0, toCall });
  const btn: Record<string, string> = { padding: '8px 14px', fontSize: '14px', fontWeight: '700', borderRadius: '8px', border: '1px solid rgba(255,255,255,0.2)', cursor: 'pointer', color: '#fff' };
  const num = (e: Event): number => Number((e.target as HTMLInputElement).value);

  return el('div', {
    role: 'group', 'aria-label': 'actions',
    style: { display: 'flex', gap: '10px', alignItems: 'center', flexWrap: 'wrap', background: 'linear-gradient(180deg,#23262e,#15171c)', border: '1px solid rgba(255,255,255,0.12)', borderRadius: '12px', padding: '12px' },
  },
    legal.fold ? el('button', { type: 'button', onClick: () => onAction('fold', 0), style: { ...btn, background: '#7a2222' } }, 'Fold') : false,
    legal.check ? el('button', { type: 'button', onClick: () => onAction('check', 0), style: { ...btn, background: '#2e6b3e' } }, 'Check') : false,
    legal.call ? el('button', { type: 'button', onClick: () => onAction('call', legal.call!.amount), style: { ...btn, background: '#2e6b3e' } }, `Call ${legal.call.amount}`) : false,

    range.available
      ? el('div', { style: { display: 'flex', flexDirection: 'column', gap: '6px', background: 'rgba(0,0,0,0.25)', borderRadius: '10px', padding: '8px' } },
          el('div', { style: { display: 'flex', gap: '6px', alignItems: 'center', flexWrap: 'wrap' } },
            ...quicks.map((q) => el('button', { type: 'button', onClick: () => onBetAmountChange(q.amount), style: { ...btn, background: '#34495e', padding: '4px 8px', fontSize: '12px' } }, q.label))),
          el('div', { style: { display: 'flex', gap: '8px', alignItems: 'center' } },
            el('label', { for: 'bet-slider', style: { color: '#bbb', fontSize: '12px' } }, 'Size'),
            el('input', { id: 'bet-slider', type: 'range', min: String(range.min), max: String(range.max), value: clampToRange(betAmount, range), onInput: (e: Event) => onBetAmountChange(clampToRange(num(e), range)), 'aria-label': 'bet size slider', style: { flex: '1', minWidth: '120px' } }),
            el('input', { id: 'bet-sizer', type: 'number', min: String(range.min), max: String(range.max), value: betAmount, onInput: (e: Event) => onBetAmountChange(clampToRange(num(e), range)), 'aria-label': 'bet size', style: { width: '84px' } }),
            el('small', { style: { color: '#aaa' } }, `(${range.min}–${range.max})`),
          ),
          el('div', {},
            legal.bet ? el('button', { type: 'button', onClick: () => onAction('bet', clampToRange(betAmount, range)), style: { ...btn, background: '#b5701b', width: '100%' } }, `Bet ${clampToRange(betAmount, range)}`) : false,
            legal.raise ? el('button', { type: 'button', onClick: () => onAction('raise', clampToRange(betAmount, range)), style: { ...btn, background: '#b5701b', width: '100%' } }, `Raise to ${clampToRange(betAmount, range)}`) : false,
          ),
        )
      : false,
  );
}

/** Keep handViewer (renders a single seat's cards) for back-compat. */
export function handViewer(seat: SeatVM): HTMLElement {
  return seatCards(seat);
}
