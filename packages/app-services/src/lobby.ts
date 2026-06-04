/**
 * Relay-backed lobby + waiting room (app §A6.3/§A7, core §8.2) — this is how REAL players find
 * and join a game (not a bot). A host creates a table with a config; others see it via the relay
 * table list and join the waiting room by announcing themselves on the table channel; when the
 * seats fill, everyone derives the SAME seat assignment (sorted by identity pubkey) and starts.
 * The relay is transport/index only (P3); seating is agreed by the players, not the relay.
 */

import { type Ruleset, type Variant, sha256, bytesToHex } from '@bsv-poker/protocol-types';
import type { RelayClient } from './network.ts';
import { type TablePlayer } from './interactive-client.ts';
import { verifySig } from './session-auth.ts';

/** Canonical signed message proving possession of `pub` for a join to this table (audit 3). */
const joinMessage = (tableId: string, pub: string, nonce: string): string => JSON.stringify(['join', tableId, pub, nonce]);

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

interface JoinEnvelope {
  t: 'join';
  id: string;
  pub: string;
  nonce?: string;
  sig?: string; // Ed25519 over joinMessage(tableId, pub, nonce) — proves possession of `pub` (audit 3)
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
    const id = `tbl-${Date.now().toString(36)}-${Math.floor(Math.random() * 1e6).toString(36)}`;
    await this.relay.createTable(id, JSON.stringify(meta));
    return id;
  }

  /** List open tables with their parsed config. */
  async listTables(): Promise<OpenTable[]> {
    const out: OpenTable[] = [];
    for (const t of await this.relay.listTables()) {
      try {
        out.push({ id: t.id, meta: JSON.parse(t.name) as TableMeta, members: t.members });
      } catch {
        /* a table whose name isn't our JSON meta — skip */
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
    /** TEST FIXTURES ONLY (audit 2): permit unsigned joins. Production must supply `me.sign`. */
    allowUnsigned = false,
  ): { seated: Promise<SeatedResult>; abort: () => void } {
    // Signed joins are mandatory (audit 2): a join must prove possession of its pub. Unsigned joins
    // are permitted only behind the explicit test flag.
    if (!me.sign && !allowUnsigned) {
      throw new Error('joinWaitingRoom requires a signing key (audit 2); set allowUnsigned only in test fixtures');
    }
    const myNonce = Math.random().toString(36).slice(2) + Date.now().toString(36);
    const joined = new Map<string, { id: string; pub: string; nonce: string }>();
    joined.set(me.pub, { id: me.id, pub: me.pub, nonce: myNonce });
    let unsub: (() => void) | null = null;
    let aborted = false;

    const seated = new Promise<SeatedResult>((resolve, reject) => {
      unsub = this.relay.subscribe(tableId, (text) => {
        void (async () => {
          try {
            const env = JSON.parse(text) as JoinEnvelope;
            if (env.t !== 'join' || !env.pub || joined.has(env.pub)) return;
            // A join must prove possession of its pub: verify the signature (audit 3). When `me`
            // signs, peers must sign too; reject unsigned/forged joins so a pub can't be spoofed.
            if (me.sign) {
              if (!env.nonce || !env.sig || !(await verifySig(env.pub, joinMessage(tableId, env.pub, env.nonce), env.sig))) return;
            }
            joined.set(env.pub, { id: env.id, pub: env.pub, nonce: env.nonce ?? '' });
            onPlayers?.([...joined.values()]);
          } catch {
            /* not a join envelope */
          }
        })();
      });

      const announce = (): void => {
        void (async () => {
          const base = { t: 'join' as const, id: me.id, pub: me.pub, nonce: myNonce };
          const env = me.sign ? { ...base, sig: await me.sign(joinMessage(tableId, me.pub, myNonce)) } : base;
          await this.relay.publish(tableId, new TextEncoder().encode(JSON.stringify(env)));
        })();
      };

      const deadline = Date.now() + 120000;
      const tick = (): void => {
        if (aborted) return;
        announce();
        if (joined.size >= meta.maxSeats) {
          // Non-grindable seating (audit 9): derive seat order from a beacon = H(all join nonces),
          // then order seats by H(beacon‖pub). A player cannot pick a favourable seat by choosing
          // their pubkey, because the order key passes through a hash bound to EVERY player's
          // committed (signed-join) nonce. Deterministic: all peers compute the same order.
          const all = [...joined.values()].sort((a, b) => (a.pub < b.pub ? -1 : 1));
          const beacon = bytesToHex(sha256(new TextEncoder().encode(all.map((p) => `${p.pub}:${p.nonce}`).join('|'))));
          const orderKey = (pub: string): string => bytesToHex(sha256(new TextEncoder().encode(`${beacon}:${pub}`)));
          const players = [...all].sort((a, b) => (orderKey(a.pub) < orderKey(b.pub) ? -1 : 1)).slice(0, meta.maxSeats);
          const seats: TablePlayer[] = players.map((_, i) => ({ seat: i, stack: meta.startingStack }));
          const mySeat = players.findIndex((p) => p.pub === me.pub);
          if (mySeat < 0) {
            // didn't make the cut for this table
            reject(new Error('table filled before you were seated'));
            return;
          }
          resolve({ mySeat, seats, ruleset: rulesetFromMeta(meta), players });
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
