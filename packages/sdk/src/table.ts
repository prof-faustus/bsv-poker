/**
 * Table session — the Phase-1 integration that wires entropy commit/reveal + distributed
 * shuffle + encrypted-card deal + betting FSM + showdown + settlement into ONE hand, with a
 * transcript and deterministic replay (core §17 Phase 1; REQ-DATA-002/003).
 *
 * This runs in-process (one client simulating N parties) — the deterministic-core integration.
 * Multi-client over the relay and the in-tree node (adapters/regtest-node) are the app/adapter layers.
 */

import {
  type Action,
  type Card,
  type GameState,
  type Ruleset,
  bytesToHex,
  sha256,
  ByteWriter,
} from '@bsv-poker/protocol-types';
import {
  makeRealCT,
  canonicalPartyOrder,
  shuffledDeck,
} from '@bsv-poker/crypto-mentalpoker';
import { type Custody } from '@bsv-poker/wallet-custody';
import { type CTContract, type BSContract, makeFakeBS } from '@bsv-poker/adapters';
import {
  type Tx,
  buildFunding,
  buildSettlement,
  sighashPreimage,
} from '@bsv-poker/tx-builder';
import {
  evaluate,
  fundingUnlocking,
  type Script,
} from '@bsv-poker/script-templates-ts';
import { getGame } from './registry.ts';
import { hashRuleset } from './ruleset.ts';

export interface Player {
  readonly seat: number;
  readonly stack: number;
  readonly custody: Custody;
  /** This party's recorded entropy r_p for the shuffle (core §4.1). */
  readonly entropy: Uint8Array;
}

export interface EntropyRecord {
  readonly pub: string; // identity pubkey (hex, compressed)
  readonly seat: number;
  readonly commitment: string; // c_p = H(r_p)
  readonly reveal: string; // r_p hex (reveal material; REQ-DATA-002)
}

export interface HandTranscript {
  readonly ruleset: Ruleset;
  readonly gid: string;
  readonly rulesetHash: string;
  readonly buttonSeat: number;
  readonly seats: ReadonlyArray<{ seat: number; stack: number }>;
  readonly partyOrder: readonly string[]; // canonical identity pubkeys
  readonly entropy: readonly EntropyRecord[]; // in canonical party order
  readonly actions: readonly Action[];
}

export interface HandResult {
  readonly state: GameState;
  readonly transcript: HandTranscript;
  /** The settlement spend verified through the real interpreter (close-out, core §6.6). */
  readonly settlementVerified: boolean;
}

const IDENTITY_ROLE = 'identity';

function gidFor(pubs: readonly string[]): string {
  const w = new ByteWriter();
  for (const p of pubs) w.hex(p);
  return bytesToHex(sha256(w.toBytes())).slice(0, 16);
}

export interface Sdk {
  readonly ct: CTContract;
  readonly bs: BSContract;
  runHand(players: readonly Player[], ruleset: Ruleset, actions: readonly Action[]): HandResult;
  deriveState(transcript: HandTranscript): GameState;
}

export function createSdk(deps?: { ct?: CTContract; bs?: BSContract }): Sdk {
  const ct = deps?.ct ?? makeRealCT();
  const bs = deps?.bs ?? makeFakeBS();

  function setupShuffle(
    players: readonly Player[],
    gid: string,
  ): { order: string[]; entropy: EntropyRecord[]; deck: Card[] } {
    // Each party's identity pubkey + entropy commit/reveal (core §4.1).
    const records = players.map((p) => {
      const pub = p.custody.derive(gid, 0, IDENTITY_ROLE);
      // commit then reveal (synchronous in-process; the real flow closes all commits first)
      const commitment = bytesToHex(sha256(p.entropy));
      return { pub, seat: p.seat, commitment, reveal: bytesToHex(p.entropy), entropy: p.entropy };
    });
    // Canonical party order = lexicographic identity pubkeys (REQ-CRYPTO-003).
    const order = canonicalPartyOrder(records.map((r) => r.pub));
    const ordered = order.map((pub) => records.find((r) => r.pub === pub)!);
    const deck = shuffledDeck(ordered.map((r) => r.entropy), 52);
    const entropy: EntropyRecord[] = ordered.map((r) => ({
      pub: r.pub,
      seat: r.seat,
      commitment: r.commitment,
      reveal: r.reveal,
    }));
    return { order, entropy, deck };
  }

  function runHand(
    players: readonly Player[],
    ruleset: Ruleset,
    actions: readonly Action[],
  ): HandResult {
    const ordered = [...players].sort((a, b) => a.seat - b.seat);
    const identityPubs = ordered.map((p) => p.custody.derive('pre', 0, IDENTITY_ROLE));
    const gid = gidFor(identityPubs);
    const rh = hashRuleset(ruleset);
    const { order, entropy, deck } = setupShuffle(ordered, gid);

    // Funding: N-of-N multisig over buy-ins, bound to gid + rulesetHash (core §6.6).
    const potSats = ordered.reduce((s, p) => s + Math.min(p.stack, ruleset.maxBuyIn), 0);
    const fundingPubs = order.map((pub) => Uint8Array.from(Buffer.from(pub, 'hex')));
    const fundingBind = {
      gid,
      rulesetHash: rh,
      round: 0,
      stateHash: '00'.repeat(32),
      actingSeat: -1,
      successorCommitment: '00'.repeat(32),
    };
    const fundingOut = buildFunding(fundingBind, fundingPubs, potSats);
    void bs.nodeBroadcast('funding:' + gid); // canonical path (in-process fake)

    // Play the hand through the engine.
    const module = getGame(ruleset.variant)({ deck });
    let state = module.init(ruleset, ordered.map((p) => ({ seat: p.seat, stack: p.stack })));
    for (const a of actions) state = module.apply(state, a);

    const transcript: HandTranscript = {
      ruleset,
      gid,
      rulesetHash: rh,
      buttonSeat: state.buttonSeat,
      seats: ordered.map((p) => ({ seat: p.seat, stack: p.stack })),
      partyOrder: order,
      entropy,
      actions: [...actions],
    };

    // Settlement: spend the funding multisig (cooperative close-out, core §6.6). Build a
    // settlement tx, sighash it, and verify the N-of-N spend through the REAL interpreter.
    const settlementVerified = verifySettlement(ordered, fundingOut.locking, potSats, gid, rh);

    return { state, transcript, settlementVerified };
  }

  function verifySettlement(
    ordered: readonly Player[],
    fundingLocking: Script,
    potSats: number,
    gid: string,
    rh: string,
  ): boolean {
    const winnerPub = ordered[0]!.custody.derive(gid, 0, IDENTITY_ROLE);
    const settleBind = {
      gid,
      rulesetHash: rh,
      round: 99,
      stateHash: 'aa'.repeat(32),
      actingSeat: ordered[0]!.seat,
      successorCommitment: 'bb'.repeat(32),
    };
    const settleOut = buildSettlement(
      settleBind,
      Uint8Array.from(Buffer.from(winnerPub, 'hex')),
      potSats,
    );
    const tx: Tx = {
      version: 1,
      inputs: [{ prevTxid: gid.padEnd(64, '0'), vout: 0, sequence: 0xffffffff }],
      outputs: [settleOut],
      nLockTime: 0,
    };
    const preimage = sighashPreimage(tx, 0);
    // Every party signs the cooperative close-out (N-of-N funding multisig).
    const sigs = ordered.map((p) =>
      p.custody.sign(gid, 0, IDENTITY_ROLE, {
        sighashPreimage: preimage,
        describe: { action: 'settle', amounts: `${potSats}`, potOrState: 'pot close-out' },
      }),
    );
    return evaluate(fundingUnlocking(sigs), fundingLocking, { sighashPreimage: preimage }).ok;
  }

  /** Reconstruct the final state from a transcript (deterministic replay; REQ-DATA-003). */
  function deriveState(transcript: HandTranscript): GameState {
    const entropies = transcript.entropy.map((e) => Uint8Array.from(Buffer.from(e.reveal, 'hex')));
    const deck = shuffledDeck(entropies, 52);
    const module = getGame(transcript.ruleset.variant)({ deck });
    let state = module.init(transcript.ruleset, [...transcript.seats]);
    for (const a of transcript.actions) state = module.apply(state, a);
    return state;
  }

  return { ct, bs, runHand, deriveState };
}
