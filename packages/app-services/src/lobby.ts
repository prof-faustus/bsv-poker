/**
 * Relay-backed lobby + waiting room (app §A6.3/§A7, core §8.2) — this is how REAL players find
 * and join a game (not a bot). A host creates a table with a config; others see it via the relay
 * table list and join the waiting room by announcing themselves on the table channel; when the
 * seats fill, everyone derives the SAME seat assignment (sorted by identity pubkey) and starts.
 * The relay is transport/index only (P3); seating is agreed by the players, not the relay.
 */

import type { Ruleset, Variant } from '@bsv-poker/protocol-types';
import type { RelayClient } from './network.ts';
import { type TablePlayer } from './interactive-client.ts';

export interface TableMeta {
  readonly name: string;
  readonly variant: Variant;
  readonly smallBlind: number;
  readonly bigBlind: number;
  readonly startingStack: number;
  readonly maxSeats: number;
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
}

export function rulesetFromMeta(meta: TableMeta): Ruleset {
  return {
    variant: meta.variant,
    bettingStructure: 'NL',
    forcedBetModel: 'blinds',
    seats: meta.maxSeats,
    blinds: { smallBlind: meta.smallBlind, bigBlind: meta.bigBlind, ante: 0, bringIn: 0 },
    minBuyIn: meta.startingStack,
    maxBuyIn: meta.startingStack,
    timeouts: { decisionMs: 30000, recoveryMs: 120000 },
    signingMode: 'A',
    currency: 'play-regtest',
    suitTiebreakHouseRule: false,
    hiLo: false,
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
    me: { id: string; pub: string },
    meta: TableMeta,
    onPlayers?: (players: Array<{ id: string; pub: string }>) => void,
  ): { seated: Promise<SeatedResult>; abort: () => void } {
    const joined = new Map<string, { id: string; pub: string }>();
    joined.set(me.pub, me);
    let unsub: (() => void) | null = null;
    let aborted = false;

    const seated = new Promise<SeatedResult>((resolve, reject) => {
      unsub = this.relay.subscribe(tableId, (text) => {
        try {
          const env = JSON.parse(text) as JoinEnvelope;
          if (env.t === 'join' && env.pub) {
            if (!joined.has(env.pub)) {
              joined.set(env.pub, { id: env.id, pub: env.pub });
              onPlayers?.([...joined.values()]);
            }
          }
        } catch {
          /* not a join envelope */
        }
      });

      const announce = (): void => {
        void this.relay.publish(
          tableId,
          new TextEncoder().encode(JSON.stringify({ t: 'join', id: me.id, pub: me.pub })),
        );
      };

      const deadline = Date.now() + 120000;
      const tick = (): void => {
        if (aborted) return;
        announce();
        if (joined.size >= meta.maxSeats) {
          // Deterministic seating: first maxSeats players sorted by identity pubkey.
          const sorted = [...joined.values()].sort((a, b) => (a.pub < b.pub ? -1 : 1)).slice(0, meta.maxSeats);
          const players = sorted;
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
