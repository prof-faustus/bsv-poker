/**
 * Lobby-facing presentational components (REQ-APP-052) — framework-free (vanilla DOM): the
 * walletPanel (balance / add / withdraw / history + buy-in affordability) and the variantPicker
 * (choose one of the five variants, seat count within the variant's range, and the Omaha hi-lo
 * toggle). Pure render + EXPLICIT handlers (no <form> submit, REQ-UI-003): every money action is a
 * button onClick, never an implicit form submission. No business logic — amounts/affordability come
 * from the pure wallet-panel view-model; legality/funds movement happen in app-services WalletService.
 */
import { el, type Child } from '../dom.ts';
import { chip } from './primitives.ts';
import { walletPanelVM, validateAmount, validateWithdraw, type WalletSnapshot } from '../view-models/wallet-panel.ts';
import type { VariantId } from '../view-models/network-lobby.ts';

const KIND_LABEL: Record<string, string> = {
  deposit: 'Add funds',
  withdraw: 'Withdraw',
  'buy-in': 'Buy-in',
  'cash-out': 'Cash-out',
};

export interface WalletPanelProps {
  readonly snapshot: WalletSnapshot;
  readonly addAmount: number;
  readonly onAddAmountChange: (n: number) => void;
  readonly onAddFunds: (amount: number) => void;
  readonly withdrawAmount: number;
  readonly onWithdrawAmountChange: (n: number) => void;
  readonly withdrawDest: string;
  readonly onWithdrawDestChange: (s: string) => void;
  readonly onWithdraw: (amount: number, dest: string) => void;
  readonly compact?: boolean;
}

const numVal = (e: Event): number => Number((e.target as HTMLInputElement).value);
const strVal = (e: Event): string => (e.target as HTMLInputElement).value;

export function walletPanel(p: WalletPanelProps): HTMLElement {
  const vm = walletPanelVM(p.snapshot);
  const addV = validateAmount(p.addAmount);
  const wdV = validateWithdraw(p.withdrawAmount, vm.balance, p.withdrawDest);

  return el('section', {
    'aria-label': 'wallet',
    style: { border: '1px solid rgba(255,255,255,0.15)', borderRadius: '12px', padding: p.compact ? '10px' : '14px', background: 'linear-gradient(180deg,#1b1d23,#141519)', display: 'grid', gap: '10px' },
  },
    el('div', { style: { display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '8px' } },
      el('div', { style: { display: 'flex', alignItems: 'center', gap: '8px' } },
        chip(undefined, '#2e7d32', 28),
        el('div', {},
          el('div', { style: { fontSize: '12px', color: '#9aa' } }, 'Wallet balance'),
          el('div', { 'aria-label': 'wallet balance', style: { fontSize: '22px', fontWeight: '800', color: '#ffd24d' } }, `${vm.balance} chips`),
        ),
      ),
      el('span', { style: { fontSize: '11px', color: vm.playMoney ? '#9c8' : '#f88', border: '1px solid currentColor', borderRadius: '999px', padding: '2px 8px' } },
        vm.playMoney ? 'PLAY-MONEY (REGTEST)' : vm.network.toUpperCase()),
    ),

    el('div', { style: { display: 'flex', gap: '8px', alignItems: 'flex-end', flexWrap: 'wrap' } },
      el('label', { style: { fontSize: '12px', color: '#bbb', display: 'grid', gap: '2px' } }, 'Add funds',
        el('input', { type: 'number', min: '1', 'aria-label': 'add funds amount', value: p.addAmount, onInput: (e: Event) => p.onAddAmountChange(numVal(e)), style: { width: '110px' } })),
      el('button', { type: 'button', disabled: !addV.ok, onClick: () => p.onAddFunds(p.addAmount), style: { padding: '6px 12px' } }, 'Add'),
    ),

    el('div', { style: { display: 'flex', gap: '8px', alignItems: 'flex-end', flexWrap: 'wrap' } },
      el('label', { style: { fontSize: '12px', color: '#bbb', display: 'grid', gap: '2px' } }, 'Withdraw',
        el('input', { type: 'number', min: '1', 'aria-label': 'withdraw amount', value: p.withdrawAmount, onInput: (e: Event) => p.onWithdrawAmountChange(numVal(e)), style: { width: '110px' } })),
      el('label', { style: { fontSize: '12px', color: '#bbb', display: 'grid', gap: '2px' } }, 'To address',
        el('input', { type: 'text', 'aria-label': 'withdraw address', value: p.withdrawDest, onInput: (e: Event) => p.onWithdrawDestChange(strVal(e)), placeholder: 'regtest address', style: { width: '180px' } })),
      el('button', { type: 'button', disabled: !wdV.ok, onClick: () => p.onWithdraw(p.withdrawAmount, p.withdrawDest), style: { padding: '6px 12px' } }, 'Withdraw'),
    ),
    (!wdV.ok && wdV.error && p.withdrawAmount > 0) ? el('div', { style: { color: '#f88', fontSize: '12px' } }, wdV.error) : false,

    vm.rows.length > 0
      ? el('div', {},
          el('div', { style: { fontSize: '12px', color: '#9aa', marginBottom: '4px' } }, 'Recent transactions'),
          el('ul', { style: { listStyle: 'none', padding: '0', margin: '0', display: 'grid', gap: '2px', fontSize: '12px' } },
            ...vm.rows.map((r) =>
              el('li', { style: { display: 'flex', justifyContent: 'space-between', gap: '8px' } },
                el('span', { style: { color: '#ccc' } }, KIND_LABEL[r.kind] ?? r.kind, r.memo ? el('span', { style: { color: '#888' } }, ` · ${r.memo}`) : false as Child),
                el('span', { style: { color: r.inflow ? '#8f8' : '#f88', fontWeight: '700' } }, String(r.signedAmount)),
              )),
          ))
      : false,

    el('p', { style: { fontSize: '11px', color: '#888', margin: '0' } },
      'Play-money balance only — persisted locally. The live on-chain deposit/withdraw backend is wired separately on the Node side (regtest faucet / custody, §A2.3); here Add/Withdraw move the local balance.'),
  );
}

export interface VariantOption {
  readonly id: VariantId;
  readonly label: string;
  readonly minSeats: number;
  readonly maxSeats: number;
  readonly note: string;
}

export interface VariantPickerProps {
  readonly options: readonly VariantOption[];
  readonly value: VariantId;
  readonly onChange: (v: VariantId) => void;
  readonly hiLo: boolean;
  readonly onHiLoChange: (b: boolean) => void;
}

export function variantPicker(p: VariantPickerProps): HTMLElement {
  const selected = p.options.find((o) => o.id === p.value);
  return el('div', { role: 'group', 'aria-label': 'variant', style: { display: 'grid', gap: '6px' } },
    el('span', { style: { fontSize: '12px', color: '#bbb' } }, 'Game variant'),
    el('div', { style: { display: 'flex', gap: '6px', flexWrap: 'wrap' } },
      ...p.options.map((o) => {
        const active = o.id === p.value;
        return el('button', {
          type: 'button', 'aria-pressed': String(active), onClick: () => p.onChange(o.id),
          style: { padding: '8px 12px', borderRadius: '8px', cursor: 'pointer', border: active ? '2px solid #ffd24d' : '1px solid #555', background: active ? '#1c4a2e' : '#1a1a1a', color: '#fff', fontWeight: active ? '700' : '500' },
        }, o.label);
      }),
    ),
    selected ? el('span', { style: { fontSize: '12px', color: '#9aa' } }, `${selected.note} · ${selected.minSeats}–${selected.maxSeats} seats`) : false,
    p.value === 'omaha'
      ? el('label', { style: { fontSize: '13px', color: '#ddd', display: 'inline-flex', gap: '6px', alignItems: 'center' } },
          el('input', { type: 'checkbox', checked: p.hiLo, onChange: (e: Event) => p.onHiLoChange((e.target as HTMLInputElement).checked) }),
          'Hi-Lo split (8-or-better)')
      : false,
  );
}
