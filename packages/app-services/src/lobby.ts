/**
 * Relay-backed lobby + waiting room (app §A6.3/§A7, core §8.2) — this is how REAL players find
 * and join a game (not a bot). A host creates a table with a config; others see it via the relay
 * table list and join the waiting room by announcing themselves on the table channel; when the
 * seats fill, everyone derives the SAME seat assignment (sorted by identity pubkey) and starts.
 * The relay is transport/index only (P3); seating is agreed by the players, not the relay.
 */

import { type Ruleset, type Variant, sha256, bytesToHex, safeJsonParse, randomId } from '@bsv-poker/protocol-types';
import type { RelayClient } from './network.ts';
import { type TablePlayer } from './interactive-client.ts';
import { verifySig } from './session-auth.ts';

/** Canonical signed message proving possession of `pub` for a join COMMIT to this table (audit 3).
 *  The commit binds `H(nonce)`, not the nonce — the nonce is disclosed only in the reveal phase. */
const joinMessage = (tableId: string, pub: string, commit: string): string => JSON.stringify(['join', tableId, pub, commit]);
/** Canonical signed message for the seating-nonce REVEAL (audit #27 — non-grindable seating). */
const revealMessage = (tableId: string, pub: string, nonce: string): string => JSON.stringify(['seat-reveal', tableId, pub, nonce]);
/** The seating commitment for a nonce: H(nonce). A player is bound to this before any nonce is seen.
 *  Exported so the non-grindability test can assert the binding (a swapped nonce cannot match). */
export const seatCommit = (nonce: string): string => bytesToHex(sha256(new TextEncoder().encode(nonce)));

/** A seat's committed-then-revealed contribution to the seating beacon. */
export interface SeatingReveal {
  readonly id: string;
  readonly pub: string;
  readonly nonce: string;
}

/**
 * Pure, deterministic seat ordering (audit #27). `beacon = H(all pub:nonce, sorted by pub)`; seat
 * order = sort by `H(beacon‖pub)`, sliced to `maxSeats`. Independent of input iteration order (it
 * sorts internally), so every peer that saw the same committed-then-revealed set derives the IDENTICAL
 * order. Non-grindable: the order key passes through the beacon, which is bound to every player's
 * nonce — all FIXED by commitment before any nonce is disclosed (see `joinWaitingRoom`).
 */
export function computeSeatOrder(reveals: readonly SeatingReveal[], maxSeats: number): SeatingReveal[] {
  const all = [...reveals].sort((a, b) => (a.pub < b.pub ? -1 : 1));
  const beacon = bytesToHex(sha256(new TextEncoder().encode(all.map((p) => `${p.pub}:${p.nonce}`).join('|'))));
  const orderKey = (pub: string): string => bytesToHex(sha256(new TextEncoder().encode(`${beacon}:${pub}`)));
  return [...all].sort((a, b) => (orderKey(a.pub) < orderKey(b.pub) ? -1 : 1)).slice(0, maxSeats);
}

export interface TableMeta {
  readonly name: string;
  readonly variant: Variant;
  readonly smallBlind: number;
  readonly bigBlind: number;
  readonly startingStack: number;
  readonly maxSeats: number;
  /** Omaha Hi-Lo split (Omaha-8, REQ-FSM-007) — only meaningful for the omaha variant. */
  readonly hiLo?: boolean;
}

export interface OpenTable {
  readonly id: string;
  readonly meta: TableMeta;
  readonly members: number;
}

export interface SeatedResult {
  readonly mySeat: number;
  readonly seats: TablePlayer[];
  readonly ruleset: Ruleset;
  readonly players: Array<{ id: string; pub: string }>;
}

/** Phase 1 — COMMIT: announce presence + a binding commitment H(nonce) to the seating nonce. */
interface JoinEnvelope {
  t: 'join';
  id: string;
  pub: string;
  commit?: string; // H(nonce) hex — fixes the nonce before any nonce is disclosed (audit #27)
  sig?: string; // Ed25519 over joinMessage(tableId, pub, commit) — proves possession of `pub` (audit 3)
}

/** Phase 2 — REVEAL: disclose the nonce, which must hash to the previously-committed value. */
interface RevealEnvelope {
  t: 'seat-reveal';
  pub: string;
  nonce: string;
  sig?: string; // Ed25519 over revealMessage(tableId, pub, nonce)
}

export function rulesetFromMeta(meta: TableMeta): Ruleset {
  // Stud and Razz use ante + bring-in; the blind variants use small/big blinds (core §7.3).
  const bringIn = meta.variant === 'stud' || meta.variant === 'razz';
  return {
    variant: meta.variant,
    bettingStructure: 'NL',
    forcedBetModel: bringIn ? 'ante-bringin' : 'blinds',
    seats: meta.maxSeats,
    blinds: bringIn
      ? { smallBlind: 0, bigBlind: 0, ante: Math.max(1, Math.floor(meta.smallBlind)), bringIn: Math.max(1, Math.floor(meta.smallBlind)) }
      : { smallBlind: meta.smallBlind, bigBlind: meta.bigBlind, ante: 0, bringIn: 0 },
    minBuyIn: meta.startingStack,
    maxBuyIn: meta.startingStack,
    timeouts: { decisionMs: 30000, recoveryMs: 120000 },
    signingMode: 'A',
    currency: 'play-regtest',
    suitTiebreakHouseRule: false,
    hiLo: meta.variant === 'omaha' ? (meta.hiLo ?? false) : false,
  };
}

export class LobbyClient {
  private readonly relay: RelayClient;
  constructor(relay: RelayClient) {
    this.relay = relay;
  }

  /** Host a new table; returns the table id (the meta is carried in the relay table name). */
  async createTable(meta: TableMeta): Promise<string> {
    // CSPRNG table id (CWE-338): 128 bits of unguessable entropy, not Math.random.
    const id = `tbl-${Date.now().toString(36)}-${randomId(16)}`;
    await this.relay.createTable(id, JSON.stringify(meta));
    return id;
  }

  /** List open tables with their parsed config. */
  async listTables(): Promise<OpenTable[]> {
    const out: OpenTable[] = [];
    for (const t of await this.relay.listTables()) {
      // Bounded parse of untrusted relay-provided table metadata (CWE-400); skip anything that
      // isn't a small, well-formed meta object.
      const parsed = safeJsonParse(t.name, { maxBytes: 4096, maxDepth: 4 });
      if (parsed.ok && parsed.value !== null && typeof parsed.value === 'object') {
        out.push({ id: t.id, meta: parsed.value as TableMeta, members: t.members });
      }
    }
    return out;
  }

  /**
   * Join a table's waiting room and resolve once it is full. `onPlayers` fires as players arrive.
   * Returns the agreed seat assignment (sorted by identity pubkey) + ruleset, and an `abort()`.
   */
  joinWaitingRoom(
    tableId: string,
    me: { id: string; pub: string; sign?: (msg: string) => Promise<string> },
    meta: TableMeta,
    onPlayers?: (players: Array<{ id: string; pub: string }>) => void,
    /** TEST FIXTURES ONLY (audit 2): permit unsigned joins. Production must supply `me.sign`. The flag
     *  is an OBJECT (not a bare boolean) so the security lint can ban `allowUnsigned: true` in any
     *  shipped src by construction — see tools/lint-security.ts. */
    seatingOpts: { allowUnsigned?: boolean } = {},
  ): { seated: Promise<SeatedResult>; abort: () => void } {
    const allowUnsigned = seatingOpts.allowUnsigned ?? false;
    // Signed joins are mandatory (audit 2): a join must prove possession of its pub. Unsigned joins
    // are permitted only behind the explicit test flag.
    if (!me.sign && !allowUnsigned) {
      throw new Error('joinWaitingRoom requires a signing key (audit 2); set allowUnsigned only in test fixtures');
    }
    // CSPRNG join nonce (CWE-338): the nonce feeds the beacon that fixes seat order, so it must be
    // unpredictable — a guessable nonce would let an adversary bias the seating beacon.
    const myNonce = randomId(16);
    const myCommit = seatCommit(myNonce);
    // COMMIT-REVEAL seating (audit #27 — non-grindable, no last-mover advantage). Phase 1 collects a
    // binding commitment H(nonce) from every seat; phase 2 collects the nonces, each checked against
    // its commitment. Because every nonce is FIXED (committed) before ANY nonce is disclosed, a late
    // joiner cannot grind their nonce against the others' to bias the beacon (the prior scheme leaked
    // nonces on join, which a last mover could grind against). See seat-ordering.test.ts.
    const commits = new Map<string, { id: string; pub: string; commit: string }>();
    const reveals = new Map<string, { id: string; pub: string; nonce: string }>();
    commits.set(me.pub, { id: me.id, pub: me.pub, commit: myCommit });
    let phase: 'commit' | 'reveal' = 'commit';
    let unsub: (() => void) | null = null;
    let aborted = false;

    const seated = new Promise<SeatedResult>((resolve, reject) => {
      unsub = this.relay.subscribe(tableId, (text) => {
        void (async () => {
          try {
            // Bounded parse of an untrusted relay frame (CWE-400); seating envelopes are tiny.
            const parsed = safeJsonParse(text, { maxBytes: 8192, maxDepth: 4 });
            if (!parsed.ok) return;
            const raw = parsed.value as JoinEnvelope | RevealEnvelope;
            if (!raw || typeof raw !== 'object' || typeof raw.pub !== 'string' || !raw.pub) return;

            if (raw.t === 'join') {
              const env = raw as JoinEnvelope;
              if (typeof env.commit !== 'string' || !env.commit || commits.has(env.pub)) return;
              // A join must prove possession of its pub (audit 3): when `me` signs, peers must too —
              // the signature binds the COMMITMENT, so a pub cannot be spoofed and the commitment is
              // authenticated before the reveal phase.
              if (me.sign && !(env.sig && (await verifySig(env.pub, joinMessage(tableId, env.pub, env.commit), env.sig)))) return;
              commits.set(env.pub, { id: env.id, pub: env.pub, commit: env.commit });
              onPlayers?.([...commits.values()].map((c) => ({ id: c.id, pub: c.pub })));
              return;
            }

            if (raw.t === 'seat-reveal') {
              const env = raw as RevealEnvelope;
              const committed = commits.get(env.pub);
              // Only accept a reveal from a seat that committed, whose nonce hashes to its commitment
              // (binds it to the value fixed in phase 1), and — when signing — is signed by that pub.
              if (!committed || typeof env.nonce !== 'string' || seatCommit(env.nonce) !== committed.commit) return;
              if (me.sign && !(env.sig && (await verifySig(env.pub, revealMessage(tableId, env.pub, env.nonce), env.sig)))) return;
              if (!reveals.has(env.pub)) reveals.set(env.pub, { id: committed.id, pub: env.pub, nonce: env.nonce });
              return;
            }
          } catch {
            /* not a seating envelope */
          }
        })();
      });

      const announceCommit = (): void => {
        void (async () => {
          const base = { t: 'join' as const, id: me.id, pub: me.pub, commit: myCommit };
          const env = me.sign ? { ...base, sig: await me.sign(joinMessage(tableId, me.pub, myCommit)) } : base;
          await this.relay.publish(tableId, new TextEncoder().encode(JSON.stringify(env)));
        })();
      };
      const announceReveal = (): void => {
        void (async () => {
          const base = { t: 'seat-reveal' as const, pub: me.pub, nonce: myNonce };
          const env = me.sign ? { ...base, sig: await me.sign(revealMessage(tableId, me.pub, myNonce)) } : base;
          await this.relay.publish(tableId, new TextEncoder().encode(JSON.stringify(env)));
        })();
      };

      const deadline = Date.now() + 120000;
      const tick = (): void => {
        if (aborted) return;
        // Re-broadcast discipline: keep publishing our commit (for late subscribers); once we have seen
        // every seat's COMMIT we enter the reveal phase and ALSO publish our nonce — never before, so
        // no peer can observe our nonce while it could still grind theirs.
        announceCommit();
        if (phase === 'commit' && commits.size >= meta.maxSeats) phase = 'reveal';
        if (phase === 'reveal') announceReveal();

        if (phase === 'reveal' && reveals.size >= meta.maxSeats) {
          // Non-grindable seating (pure, deterministic — see computeSeatOrder): bound to commitments
          // fixed before any reveal, identical on every peer.
          const players = computeSeatOrder([...reveals.values()], meta.maxSeats);
          const seats: TablePlayer[] = players.map((_, i) => ({ seat: i, stack: meta.startingStack }));
          const mySeat = players.findIndex((p) => p.pub === me.pub);
          if (mySeat < 0) {
            reject(new Error('table filled before you were seated'));
            return;
          }
          resolve({ mySeat, seats, ruleset: rulesetFromMeta(meta), players: players.map((p) => ({ id: p.id, pub: p.pub })) });
          return;
        }
        if (Date.now() > deadline) {
          reject(new Error('waiting-room timeout'));
          return;
        }
        setTimeout(tick, 400);
      };
      tick();
    }).finally(() => unsub?.());

    return {
      seated,
      abort: () => {
        aborted = true;
        unsub?.();
      },
    };
  }
}
