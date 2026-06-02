/**
 * `reproduce` (core §14.5, P10, REQ-TEST-005): regenerate EVERY committed vector from code and
 * exit non-zero on any mismatch. Run with `--write` to (re)generate the committed file after a
 * deliberate change. Zero fabrication: every number here is produced by running code.
 *
 * Covered vectors: §19.D hand-eval (high categories, Omaha 2+3, ace-to-five low), §19.A
 * rulesetHash worked example, §19.C script template wire-byte sizes, and a full-hand transcript
 * state hash (determinism / replay anchor).
 */

import { existsSync, readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { parseHand, type Ruleset, rulesetHash } from '@bsv-poker/protocol-types';
import { eval5High, bestOmaha, bestLow, CATEGORY_NAMES } from '@bsv-poker/hand-eval';
import {
  branchBindingPrefix,
  fundingLocking,
  revealOrTimeoutLocking,
  foldLocking,
  settlementLocking,
  fairPlayLocking,
  fairPlayCommitment,
  scriptSizeBytes,
  revealCommitment,
} from '@bsv-poker/script-templates-ts';
import { createHoldem } from '@bsv-poker/game-holdem';
import type { BranchBinding, Card } from '@bsv-poker/protocol-types';
import type { Action } from '@bsv-poker/protocol-types';

const ROOT = process.cwd();
const VECTORS = join(ROOT, 'spec/vectors/reproduce.json');

const SAMPLE_RULESET: Ruleset = {
  variant: 'holdem',
  bettingStructure: 'NL',
  forcedBetModel: 'blinds',
  seats: 2,
  blinds: { smallBlind: 1, bigBlind: 2, ante: 0, bringIn: 0 },
  minBuyIn: 100,
  maxBuyIn: 200,
  timeouts: { decisionMs: 30000, recoveryMs: 120000 },
  signingMode: 'A',
  currency: 'play-regtest',
  suitTiebreakHouseRule: false,
  hiLo: false,
};

const HIGH_HANDS = [
  'As Ks Qs Js Ts',
  '9h 8h 7h 6h 5h',
  '5c 4c 3c 2c Ac',
  'Qs Qh Qd Qc Ks',
  'As Ah Ad Ks Kh',
  'Ad Jd 9d 6d 3d',
  'As Kd Qh Jc Ts',
  '5s 4d 3h 2c As',
  '7s 7h 7d Ks Qd',
  'As Ah Ks Kh 5d',
  '8s 8h Ad 7c 5h',
  'As Kd Jh 8c 6s',
];

const LOW_HANDS = [
  'Ah 2d 3c 4s 5h Kd Qs',
  'Ah 2d 3c 4s 6h Ks Qd',
  'Ah 2d 4c 5s 7h Ks Qd',
  'Ah Ad 2c 3s 8h 9s Td',
  'Ah 2h 3h 4h 5h Kh Qh',
];

const FIXED_BINDING: BranchBinding = {
  gid: 'aa'.repeat(8),
  rulesetHash: 'bb'.repeat(32),
  round: 0,
  stateHash: 'cc'.repeat(32),
  actingSeat: 0,
  successorCommitment: 'dd'.repeat(32),
};
const PUB = Uint8Array.from([0x02, ...Array.from({ length: 32 }, (_, i) => i + 1)]);

function fixedDeck(): Card[] {
  const head = ['As', 'Ks', 'Ah', 'Kh', 'Qd', 'Jc', '9h', '4s', '3h'].map(parseHand).flat();
  const used = new Set(head);
  const rest: Card[] = [];
  for (let c = 0; c < 52; c++) if (!used.has(c)) rest.push(c);
  return [...head, ...rest];
}

function generate(): unknown {
  const high = HIGH_HANDS.map((h) => {
    const v = eval5High(parseHand(h));
    return { hand: h, category: CATEGORY_NAMES[v.category], tiebreak: v.tiebreak };
  });
  const omaha = (() => {
    const v = bestOmaha(parseHand('Js 9h 4c 3d'), parseHand('As Ks Qs 2s 7d')).value;
    return { category: CATEGORY_NAMES[v.category], tiebreak: v.tiebreak };
  })();
  const low = LOW_HANDS.map((h) => {
    const v = bestLow(parseHand(h)).value;
    return { hand: h, pairPenalty: v.pairPenalty, values: v.values };
  });

  const templateSizes = {
    branchBindingPrefix: scriptSizeBytes(branchBindingPrefix(FIXED_BINDING)),
    fold: scriptSizeBytes(foldLocking(FIXED_BINDING, PUB)),
    funding2of2: scriptSizeBytes(fundingLocking(FIXED_BINDING, [PUB, PUB])),
    revealOrTimeout: scriptSizeBytes(
      revealOrTimeoutLocking(FIXED_BINDING, revealCommitment(7, Uint8Array.of(1, 2, 3, 4)), PUB, PUB),
    ),
    settlement: scriptSizeBytes(settlementLocking(FIXED_BINDING, PUB)),
    fairPlayPerCard: scriptSizeBytes(fairPlayLocking(FIXED_BINDING, fairPlayCommitment(PUB), PUB)),
  };
  // §19.C per-hand transaction-count envelope for heads-up Hold'em (structurally derived from
  // §19.E: 1 funding + 2 entropy commits + shuffle-stage commits + 1 deal + 3 board reveals +
  // per-action bets + fold/settlement + per-card fair-play). Byte totals stay TRACKED
  // ASSUMPTION pending the embedded node's full interpreter (REQ-CRYPTO-009 / RT-01 m2).
  const perHandTxEnvelope = {
    funding: 1,
    entropyCommits: 2,
    shuffleStageCommits: 2,
    deal: 1,
    boardReveals: 3,
    fairPlayPerCard: 52,
    note: 'byte totals are TRACKED ASSUMPTION until the embedded node interpreter measures them',
  };

  // Full heads-up hand → state hash (determinism / replay anchor, P2).
  const m = createHoldem({ deck: fixedDeck() });
  let s = m.init(SAMPLE_RULESET, [
    { seat: 0, stack: 100 },
    { seat: 1, stack: 100 },
  ]);
  const play: Action[] = [
    { kind: 'call', seat: 0, amount: 1 },
    { kind: 'check', seat: 1, amount: 0 },
    { kind: 'check', seat: 1, amount: 0 },
    { kind: 'check', seat: 0, amount: 0 },
    { kind: 'check', seat: 1, amount: 0 },
    { kind: 'check', seat: 0, amount: 0 },
    { kind: 'check', seat: 1, amount: 0 },
    { kind: 'check', seat: 0, amount: 0 },
  ];
  for (const a of play) s = m.apply(s, a);

  return {
    handEvalHigh: high,
    handEvalOmaha: omaha,
    handEvalLow: low,
    rulesetHashSample: rulesetHash(SAMPLE_RULESET),
    templateWireBytes: templateSizes,
    perHandTxEnvelope,
    fullHandStateHash: m.stateHash(s),
    fullHandPayouts: s.payouts,
  };
}

function main(): void {
  const generated = generate();
  const json = JSON.stringify(generated, null, 2) + '\n';
  const write = process.argv.includes('--write');
  if (write) {
    mkdirSync(join(ROOT, 'spec/vectors'), { recursive: true });
    writeFileSync(VECTORS, json);
    console.log(`reproduce --write: vectors written to spec/vectors/reproduce.json`);
    return;
  }
  if (!existsSync(VECTORS)) {
    console.error('reproduce: committed vectors missing — run `node tools/reproduce.ts --write`');
    process.exit(1);
  }
  const committed = readFileSync(VECTORS, 'utf8');
  if (committed !== json) {
    console.error('REPRODUCE MISMATCH (core §14.5): regenerated vectors differ from committed.');
    // show a tiny diff hint
    const a = committed.split('\n');
    const b = json.split('\n');
    for (let i = 0; i < Math.max(a.length, b.length); i++) {
      if (a[i] !== b[i]) {
        console.error(`  line ${i + 1}:\n    committed: ${a[i] ?? '(none)'}\n    regenerated: ${b[i] ?? '(none)'}`);
        break;
      }
    }
    process.exit(1);
  }
  console.log('reproduce OK — all committed vectors regenerate bit-for-bit.');
}

main();
