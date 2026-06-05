/**
 * signingModal, showdownPanel, settlementSummary (REQ-APP-052; §A6.7/§A6.8) — framework-free
 * (vanilla DOM). Presentational only. The signing modal states EXACTLY what is being authorised — no
 * silent signing (REQ-UI-006 / core §11.6): every line of the prompt is rendered for the human to
 * read before they confirm.
 */
import { el } from '../dom.ts';
import { cardChip, cardBack, banner } from './primitives.ts';
import type { SigningPromptVM } from '../view-models/signing.ts';
import type { ShowdownViewModel, SettlementViewModel } from '../view-models/showdown.ts';

/** The signing modal: returns the dialog element, or null when there is no prompt (nothing to sign). */
export function signingModal(prompt: SigningPromptVM | null, onConfirm: () => void, onCancel: () => void): HTMLElement | null {
  if (!prompt) return null;
  return el('div', {
    role: 'dialog', 'aria-modal': 'true', 'aria-label': prompt.title,
    style: { position: 'fixed', inset: '0', background: 'rgba(0,0,0,0.6)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: '100' },
  },
    el('div', { style: { background: '#1d1d1d', padding: '20px', borderRadius: '8px', maxWidth: '460px' } },
      el('h2', { style: { marginTop: '0' } }, prompt.title),
      el('ul', {}, ...prompt.lines.map((l) => el('li', {}, l))),
      el('p', { style: { fontSize: '12px', color: '#bbb' } }, prompt.disclosure),
      el('div', { style: { display: 'flex', gap: '8px', justifyContent: 'flex-end' } },
        el('button', { type: 'button', onClick: onCancel }, 'Cancel'),
        el('button', { type: 'button', onClick: onConfirm }, 'Confirm & apply'),
      ),
    ),
  );
}

export function showdownPanel(vm: ShowdownViewModel): HTMLElement {
  return el('div', { 'aria-label': 'showdown', style: { border: '1px solid #555', borderRadius: '8px', padding: '12px' } },
    el('h3', { style: { marginTop: '0' } }, 'Showdown'),
    vm.uncontested ? banner('Hand won uncontested — cards not revealed.', 'info') : false,
    el('div', { style: { margin: '8px 0' } },
      'Board: ',
      ...(vm.board.length === 0 ? ['(none)'] : vm.board.map((c) => cardChip(c))),
    ),
    ...vm.seats.map((s) =>
      el('div', { style: { display: 'flex', gap: '8px', alignItems: 'center' } },
        el('strong', {}, `Seat ${s.seat}`),
        s.folded
          ? el('span', { style: { color: '#999' } }, 'folded ', cardBack(), cardBack())
          : el('span', { style: { display: 'inline-flex' } }, ...s.holeCards.map((c) => cardChip(c))),
        s.won > 0 ? el('span', { style: { color: '#8f8' } }, `won ${s.won}`) : false,
      ),
    ),
  );
}

export function settlementSummary(vm: SettlementViewModel): HTMLElement {
  return el('div', { 'aria-label': 'settlement', style: { border: '1px solid #555', borderRadius: '8px', padding: '12px', marginTop: '8px' } },
    el('h3', { style: { marginTop: '0' } }, `Settlement (total pot ${vm.totalPot})`),
    el('table', {},
      el('thead', {},
        el('tr', {},
          el('th', { style: { textAlign: 'left', paddingRight: '12px' } }, 'Seat'),
          el('th', { style: { textAlign: 'left', paddingRight: '12px' } }, 'Net'),
          el('th', { style: { textAlign: 'left' } }, 'Stack'),
        ),
      ),
      el('tbody', {}, ...vm.rows.map((r) =>
        el('tr', {},
          el('td', {}, String(r.seat)),
          el('td', { style: { color: r.delta >= 0 ? '#8f8' : '#f88' } }, `${r.delta >= 0 ? '+' : ''}${r.delta}`),
          el('td', {}, String(r.endingStack)),
        ),
      )),
    ),
  );
}
