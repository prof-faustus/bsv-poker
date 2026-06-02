/**
 * Bot daemon — a SIMULATED REMOTE PLAYER, run as its own process, that connects to a table over the
 * relay socket exactly like a human's client (core §8, D4). The bot is NOT in the main app and has
 * no in-process access to anyone else's state (that would be a cheat): it has its own identity +
 * entropy, joins the waiting room over the relay, and plays its seat by submitting actions over the
 * same networked protocol a human uses. Run two windows (app + bot, or two bots) to really test
 * multiplayer over the wire.
 *
 *   node tools/bot-daemon.ts --relay http://127.0.0.1:8091 [--table <id>] [--name alice] [--strategy passive|aggressive]
 */

import { randomBytes } from 'node:crypto';
import {
  RelayClient,
  LobbyClient,
  InteractiveNetworkedTableClient,
  type ClientUpdate,
  type OpenTable,
} from '@bsv-poker/app-services';
import { genKeyPair } from '@bsv-poker/script-templates-ts';
import { bytesToHex, type Action, type LegalActions } from '@bsv-poker/protocol-types';

function arg(name: string, fallback?: string): string | undefined {
  const i = process.argv.indexOf(name);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : fallback;
}

const RELAY = arg('--relay', 'http://127.0.0.1:8091')!;
const NAME = arg('--name', `bot-${Math.random().toString(36).slice(2, 6)}`)!;
const WANT_TABLE = arg('--table');
const STRATEGY = arg('--strategy', 'passive')!;
const MAX_HANDS = Number(arg('--hands', '100'));

const log = (m: string): void => console.log(`[bot ${NAME}] ${m}`);

function chooseAction(legal: LegalActions, seat: number): Action {
  if (STRATEGY === 'aggressive') {
    if (legal.raise) return { kind: 'raise', seat, amount: legal.raise.max };
    if (legal.bet) return { kind: 'bet', seat, amount: legal.bet.max };
  }
  if (legal.check) return { kind: 'check', seat, amount: 0 };
  if (legal.draw) return { kind: 'stand', seat, amount: 0 };
  if (legal.call) return { kind: 'call', seat, amount: legal.call.amount };
  return { kind: 'fold', seat, amount: 0 };
}

async function findTable(lobby: LobbyClient): Promise<OpenTable> {
  const deadline = Date.now() + 120000;
  for (;;) {
    const tables = await lobby.listTables().catch(() => [] as OpenTable[]);
    const t = WANT_TABLE ? tables.find((x) => x.id === WANT_TABLE) : tables[0];
    if (t) return t;
    if (Date.now() > deadline) throw new Error('no open table to join (timed out)');
    log('waiting for an open table…');
    await new Promise((r) => setTimeout(r, 1000));
  }
}

async function main(): Promise<void> {
  const relay = new RelayClient(RELAY);
  const lobby = new LobbyClient(relay);
  const id = `${NAME}-${Math.random().toString(36).slice(2, 8)}`;
  const pub = bytesToHex(genKeyPair().pubCompressed); // the bot's OWN identity key

  log(`connecting to relay ${RELAY} as a remote player…`);
  const table = await findTable(lobby);
  log(`joining table ${table.id} (${table.meta.variant}, ${table.meta.maxSeats} seats) over the socket`);

  const { seated } = lobby.joinWaitingRoom(table.id, { id, pub }, table.meta, (players) =>
    log(`waiting room: ${players.length}/${table.meta.maxSeats} players`),
  );
  const s = await seated;
  log(`SEATED at seat ${s.mySeat}; opponents are remote over the relay`);

  const client = new InteractiveNetworkedTableClient({
    relay,
    tableId: table.id,
    mySeat: s.mySeat,
    seats: s.seats,
    ruleset: s.ruleset,
    entropy: new Uint8Array(randomBytes(32)),
  });

  client.onUpdate((u: ClientUpdate) => {
    if (u.legal) {
      const a = chooseAction(u.legal, s.mySeat);
      client.submitAction(a);
      log(`my turn → ${a.kind}${a.amount ? ' ' + a.amount : ''}`);
    }
  });

  log(`playing up to ${MAX_HANDS} hands over the wire…`);
  await client.playSession({ maxHands: MAX_HANDS });
  log('session ended (busted, table empty, or hand cap reached)');
}

main().then(() => process.exit(0), (e) => { console.error(`[bot ${NAME}] FAIL:`, (e as Error).message); process.exit(1); });
