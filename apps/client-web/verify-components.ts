/**
 * Browser-executed render tests for the table-screen vanilla components (ui-core/src/components):
 * pokerTable, actionBar, signingModal, showdownPanel, settlementSummary, timerBanner. These render
 * only inside a live game, so the lobby render check (verify-render.ts) does not exercise them — this
 * closes that gap. Each component is rendered from a fixture view-model (matching its typed props) in
 * a REAL headless browser and its key DOM is asserted, plus a negative XSS case (a malicious seat
 * label must NOT become markup — the textContent path holds for component-supplied strings too).
 *
 * Skips (exit 0) when no headless browser is found; fails (exit 1) on any failed assertion.
 * Run: `node verify-components.ts` (from apps/client-web; requires the web build).
 */
import { writeFile, rm } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, dumpDom } from './headless.ts';

const DIST = join(dirname(fileURLToPath(import.meta.url)), 'dist', 'esm');
const TEST_HTML = '__component_test__.html';

const PAGE = `<!doctype html><html><head><meta charset="utf-8"></head>
<body><div id="app"></div><pre id="result">COMP-TESTS: PENDING</pre>
<script type="module">
import { el } from './packages/ui-core/src/dom.js';
import { pokerTable, actionBar, signingModal, showdownPanel, settlementSummary, timerBanner }
  from './packages/ui-core/src/components/index.js';
const results = [];
const ok = (name, cond) => results.push({ name, ok: !!cond });
const app = document.getElementById('app');
const mountOne = (node) => { const w = el('div', {}); w.appendChild(node); app.appendChild(w); return w; };

const C = (code) => ({ code, rank: code[0], suit: code[1] });
const heroSeat = { seat: 0, stack: 100, committedThisRound: 10, folded: false, allIn: false,
  isButton: true, isToAct: true, isHero: true, holeCards: [C('As'), C('Ah')] };
const villain = { seat: 1, stack: 90, committedThisRound: 20, folded: false, allIn: false,
  isButton: false, isToAct: false, isHero: false, holeCards: [] };
const tableVM = {
  phase: 'flop', handComplete: false, board: [C('Td'), C('2c'), C('Kh')],
  seats: [heroSeat, villain], pots: [{ amount: 30, eligible: [0, 1] }], totalPot: 30, toAct: 0,
  heroSeat: 0, actionBar: { isHeroTurn: true, legal: { check: false, call: { amount: 10 }, raise: { min: 20, max: 100 }, fold: true } },
  timer: { seat: 0, decisionMs: 30000, consequenceText: 'You are on the clock — default fold', defaultKind: 'fold' },
};

// 1. pokerTable renders the felt, both seats (stacks), the board cards, and the pot.
const pt = mountOne(pokerTable(tableVM, (s) => (s.isHero ? 'You' : 'Villain')));
ok('table-group', pt.querySelector('[aria-label="poker table"]') !== null);
ok('table-seat-hero', pt.textContent.indexOf('You') >= 0 && pt.textContent.indexOf('100') >= 0);
ok('table-seat-villain', pt.textContent.indexOf('Villain') >= 0 && pt.textContent.indexOf('90') >= 0);
ok('table-board-cards', pt.querySelectorAll('[role="img"]').length >= 3);
ok('table-pot', pt.textContent.indexOf('30') >= 0);

// 2. NEGATIVE XSS: a malicious seat label must render as text, not an element.
const ptx = mountOne(pokerTable(tableVM, () => '<img src=x onerror="window.__pwned=1">'));
ok('label-no-xss', ptx.querySelector('img') === null && window.__pwned === undefined);

// 3. actionBar shows the legal controls (Fold, Call 10, Raise) and a bet sizer.
let lastAction = null;
const ab = mountOne(actionBar({ vm: tableVM.actionBar, heroSeat: 0, betAmount: 20,
  onBetAmountChange: () => {}, onAction: (k, a) => { lastAction = [k, a]; }, pot: 30 }));
const btnText = [...ab.querySelectorAll('button')].map((b) => b.textContent).join('|');
ok('action-fold', btnText.indexOf('Fold') >= 0);
ok('action-call', btnText.indexOf('Call 10') >= 0);
ok('action-raise', btnText.indexOf('Raise') >= 0);
ok('action-no-check', btnText.indexOf('Check') < 0); // check:false -> no Check button
const fold = [...ab.querySelectorAll('button')].find((b) => b.textContent === 'Fold');
fold.dispatchEvent(new MouseEvent('click'));
ok('action-fires', lastAction && lastAction[0] === 'fold');

// 4. signingModal renders the title + every disclosure line (no silent signing).
let confirmed = false;
const sm = mountOne(signingModal({ title: 'Authorise: bet 20', lines: ['You bet 20', 'Pot becomes 50'], disclosure: 'This signs an action envelope.' },
  () => { confirmed = true; }, () => {}));
ok('modal-dialog', sm.querySelector('[role="dialog"]') !== null);
ok('modal-title', sm.textContent.indexOf('Authorise: bet 20') >= 0);
ok('modal-lines', sm.textContent.indexOf('You bet 20') >= 0 && sm.textContent.indexOf('Pot becomes 50') >= 0);
const confirmBtn = [...sm.querySelectorAll('button')].find((b) => /Confirm/.test(b.textContent));
confirmBtn.dispatchEvent(new MouseEvent('click'));
ok('modal-confirm-fires', confirmed === true);

// 5. showdownPanel + settlementSummary.
const sd = mountOne(showdownPanel({ uncontested: false, board: [C('Td'), C('2c'), C('Kh')],
  seats: [{ seat: 0, folded: false, holeCards: [C('As'), C('Ah')], won: 50 }, { seat: 1, folded: true, holeCards: [], won: 0 }] }));
ok('showdown-heading', sd.textContent.indexOf('Showdown') >= 0);
ok('showdown-won', sd.textContent.indexOf('won 50') >= 0);
const ss = mountOne(settlementSummary({ totalPot: 50, rows: [{ seat: 0, delta: 30, endingStack: 130 }, { seat: 1, delta: -30, endingStack: 70 }] }));
ok('settlement-total', ss.textContent.indexOf('50') >= 0);
ok('settlement-delta', ss.textContent.indexOf('+30') >= 0 && ss.textContent.indexOf('-30') >= 0);

// 6. timerBanner surfaces the consequence text.
const tb = mountOne(timerBanner(tableVM.timer));
ok('timer-text', tb.textContent.indexOf('default fold') >= 0);

const failed = results.filter((r) => !r.ok);
document.getElementById('result').textContent = failed.length === 0
  ? ('COMP-TESTS: PASS (' + results.length + ' assertions)')
  : ('COMP-TESTS: FAIL: ' + failed.map((r) => r.name).join(', '));
</script></body></html>`;

async function main(): Promise<void> {
  if (!existsSync(join(DIST, 'packages', 'ui-core', 'src', 'components', 'index.js'))) {
    console.error('verify-components: built components missing — run build.ts first.');
    process.exit(1);
  }
  const browser = findBrowser();
  if (!browser) {
    console.log('verify-components: SKIP — no headless Chrome/Edge found (set BSV_CHROME to force).');
    process.exit(0);
  }
  const htmlPath = join(DIST, TEST_HTML);
  await writeFile(htmlPath, PAGE, 'utf8');
  try {
    console.log(`verify-components: ${browser}`);
    const dom = await dumpDom(browser, DIST, TEST_HTML);
    const m = dom.match(/COMP-TESTS: (PASS[^<]*|FAIL[^<]*)/);
    const verdict = m?.[1];
    if (!verdict) {
      console.error('verify-components: FAILED — no result marker (page threw before reporting).');
      console.error(`--- first 1500 chars ---\n${dom.slice(0, 1500)}`);
      process.exit(1);
    }
    if (verdict.startsWith('FAIL')) {
      console.error(`verify-components: ${verdict}`);
      process.exit(1);
    }
    console.log(`verify-components: OK — ${verdict}`);
    process.exit(0);
  } finally {
    await rm(htmlPath, { force: true }).catch(() => {});
  }
}

void main();
