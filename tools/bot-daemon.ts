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
import { createServer } from 'node:http';
import { createPublicKey, createPrivateKey } from 'node:crypto';
import {
  RelayClient,
  LobbyClient,
  InteractiveNetworkedTableClient,
  sessionAuthFromSeed,
  deriveSeatSeed,
  type ClientUpdate,
  type OpenTable,
} from '@bsv-poker/app-services';
import { OP, genKeyPair, signPreimage, compressedPub, fairPlayCommitment, fundingLocking, type Script, type KeyPair } from '@bsv-poker/script-templates-ts';
import { bytesToHex, cardToString, type Action, type BranchBinding, type GameState, type LegalActions } from '@bsv-poker/protocol-types';
import { RealBsvNode } from '@bsv-poker/adapters/real-node';
import { type Tx, type TxOutput, serializeTxWire, txidWire, sighashMessage } from '@bsv-poker/tx-builder';

/** Build a secp256k1 wallet/on-chain KeyPair from a 32-byte scalar (derived from the root) — same
 *  PKCS8-DER construction the custody layer uses, inlined to keep the daemon self-contained. */
function keyPairFromScalar(d: Uint8Array): KeyPair {
  const ecPriv = Uint8Array.from([0x30, 0x25, 0x02, 0x01, 0x01, 0x04, 0x20, ...d]);
  const algId = Uint8Array.from([0x30, 0x10, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01, 0x06, 0x05, 0x2b, 0x81, 0x04, 0x00, 0x0a]);
  const inner = Uint8Array.from([0x04, ecPriv.length, ...ecPriv]);
  const bodyArr = Uint8Array.from([0x02, 0x01, 0x00, ...algId, ...inner]);
  const der = Uint8Array.from([0x30, bodyArr.length, ...bodyArr]);
  const priv = createPrivateKey({ key: Buffer.from(der), format: 'der', type: 'pkcs8' });
  const pub = createPublicKey(priv);
  return { priv, pub, pubCompressed: compressedPub(pub) };
}
import { coSignSettlement, gatherByIndex, broadcastValue, awaitValue } from './settlement-coordinator.ts';

function arg(name: string, fallback?: string): string | undefined {
  const i = process.argv.indexOf(name);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : fallback;
}

const RELAY = arg('--relay', 'http://127.0.0.1:8091')!;
const NAME = arg('--name', `bot-${Math.random().toString(36).slice(2, 6)}`)!;
const WANT_TABLE = arg('--table');
const STRATEGY = arg('--strategy', 'passive')!;
const MAX_HANDS = Number(arg('--hands', '100'));
const GUI_PORT = arg('--gui') ? Number(arg('--gui')) : 0;
const NODE_PORT = arg('--node') ? Number(arg('--node')) : 0; // on-chain settlement against this node
const SUBSIDY = 5_000_000_000;
const SCALE = 1_000_000;
const SETTLE_FEE = 1000;
const BIND: BranchBinding = { gid: 'a1'.repeat(8), rulesetHash: 'b2'.repeat(32), round: 0, stateHash: 'c3'.repeat(32), actingSeat: -1, successorCommitment: '00'.repeat(32) };
const p2pkh = (pub: Uint8Array): Script => [OP.OP_DUP, OP.OP_HASH160, fairPlayCommitment(pub), OP.OP_EQUALVERIFY, OP.OP_CHECKSIG];
const sigT = (msg: Uint8Array, k: KeyPair): Uint8Array => signPreimage(msg, k.priv);

// Live view the GUI polls — so you can WATCH the bot play in its own browser window.
const view: { seat: number; status: string; state: GameState | null; log: string[] } = { seat: -1, status: 'starting', state: null, log: [] };
const log = (m: string): void => {
  console.log(`[bot ${NAME}] ${m}`);
  view.log.push(`${new Date().toLocaleTimeString()}  ${m}`);
  if (view.log.length > 100) view.log.shift();
};

function startGui(port: number): void {
  createServer((req, res) => {
    if (req.url === '/state') {
      const s = view.state;
      const body = {
        name: NAME,
        seat: view.seat,
        status: view.status,
        phase: s?.phase ?? '—',
        handComplete: s?.handComplete ?? false,
        hole: s && view.seat >= 0 ? (s.hole?.[view.seat] ?? []).map(cardToString) : [],
        board: (s?.board ?? []).map(cardToString),
        seats: (s?.seats ?? []).map((x) => ({ seat: x.seat, stack: x.stack, folded: x.folded })),
        pot: (s?.pots ?? []).reduce((a, p) => a + p.amount, 0),
        log: view.log.slice(-30),
      };
      res.writeHead(200, { 'content-type': 'application/json', 'access-control-allow-origin': '*' });
      res.end(JSON.stringify(body));
      return;
    }
    res.writeHead(200, { 'content-type': 'text/html' });
    res.end(`<!doctype html><meta charset=utf8><title>BOT ${NAME}</title>
<style>body{background:#0b3d2e;color:#eee;font:14px system-ui;margin:0;padding:16px}
.card{display:inline-block;background:#fff;color:#111;border-radius:4px;padding:4px 7px;margin:2px;font-weight:700}
h1{font-size:18px}.seat{padding:2px 0}.log{white-space:pre-wrap;background:#06241b;border-radius:6px;padding:8px;height:240px;overflow:auto;font-family:monospace;font-size:12px}</style>
<h1>🤖 BOT "${NAME}" — remote player over the socket</h1>
<div id=hdr></div><div>Your hand: <span id=hole></span></div><div>Board: <span id=board></span></div>
<div id=seats style=margin:8px:0></div><div class=log id=log></div>
<script>
async function tick(){try{const r=await fetch('/state');const s=await r.json();
document.getElementById('hdr').textContent='seat '+s.seat+' · '+s.status+' · phase '+s.phase+' · pot '+s.pot+(s.handComplete?' · HAND COMPLETE':'');
const cards=a=>a.map(c=>'<span class=card>'+c+'</span>').join('')||'—';
document.getElementById('hole').innerHTML=cards(s.hole);
document.getElementById('board').innerHTML=cards(s.board);
document.getElementById('seats').innerHTML=s.seats.map(x=>'<div class=seat>seat '+x.seat+(x.seat===s.seat?' (me)':'')+': '+x.stack+(x.folded?' folded':'')+'</div>').join('');
document.getElementById('log').textContent=s.log.join('\\n');
}catch(e){}}
setInterval(tick,500);tick();
</script>`);
  }).listen(port, '127.0.0.1', () => console.log(`[bot ${NAME}] GUI: watch this bot at http://127.0.0.1:${port}/`));
}

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
  if (process.argv.includes('--create')) {
    const meta = {
      name: arg('--name', 'bot-table')!,
      variant: (arg('--variant', 'holdem') as OpenTable['meta']['variant']),
      smallBlind: Number(arg('--sb', '1')),
      bigBlind: Number(arg('--bb', '2')),
      startingStack: Number(arg('--stack', '100')),
      maxSeats: Number(arg('--seats', '2')),
    };
    const id = await lobby.createTable(meta);
    log(`hosting a new table ${id} (${meta.variant}, ${meta.maxSeats} seats)`);
    return { id, meta, members: 0 };
  }
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
  // ONE root for this player: the wallet/on-chain key AND the Ed25519 seat key derive from it, so a
  // valid seat signature proves control of the same root that funds the buy-in (audit 3).
  const root = new Uint8Array(randomBytes(32));
  const auth = await sessionAuthFromSeed(deriveSeatSeed(root, 'bsv-poker/seat-ed25519'));
  const walletKey = keyPairFromScalar(deriveSeatSeed(root, 'bsv-poker/wallet')); // same root → wallet key
  const pub = auth.pub; // seat identity = the key it signs envelopes with (rooted in the wallet)

  log(`connecting to relay ${RELAY} as a remote player…`);
  const table = await findTable(lobby);
  log(`joining table ${table.id} (${table.meta.variant}, ${table.meta.maxSeats} seats) over the socket`);

  if (GUI_PORT) startGui(GUI_PORT);
  view.status = 'joining';

  const { seated } = lobby.joinWaitingRoom(table.id, { id, pub, sign: (m) => auth.sign(m) }, table.meta, (players) =>
    log(`waiting room: ${players.length}/${table.meta.maxSeats} players`),
  );
  const s = await seated;
  view.seat = s.mySeat;
  view.status = 'playing';
  log(`SEATED at seat ${s.mySeat}; opponents are remote over the relay`);

  // In on-chain mode the embedded node's chain tip is the SHARED clock for the accountable action
  // timeout (audit 3): every seated client reads the same height, so an unresponsive seat is dropped
  // (with the engine's check-or-fold default) at the same anchored deadline on every client.
  const node = NODE_PORT ? new RealBsvNode('127.0.0.1', NODE_PORT) : null;
  // Cache the tip for ~1s: the timeout loop polls the height every ~25ms, but the chain tip changes on
  // the order of blocks, so re-querying the node socket each poll is wasteful.
  let tipCache = { h: 0, at: 0 };
  const cachedHeight = async (): Promise<number> => {
    const now = Date.now();
    if (now - tipCache.at > 1000) tipCache = { h: await node!.height(), at: now };
    return tipCache.h;
  };

  const client = new InteractiveNetworkedTableClient({
    relay,
    tableId: table.id,
    mySeat: s.mySeat,
    seats: s.seats,
    ruleset: s.ruleset,
    entropy: new Uint8Array(randomBytes(32)),
    auth, // sign every envelope I emit
    seatPubs: s.players.map((p) => p.pub), // verify peers: seat → registered session key
    ...(node ? { heightSource: cachedHeight } : {}),
  });

  client.onUpdate((u: ClientUpdate) => {
    view.state = u.state;
    if (u.legal) {
      const a = chooseAction(u.legal, s.mySeat);
      client.submitAction(a);
      log(`my turn → ${a.kind}${a.amount ? ' ' + a.amount : ''}`);
    }
  });

  if (NODE_PORT) {
    // ON-CHAIN MODE: exchange on-chain keys over the relay, fund the escrow (seat 0), play one hand,
    // then co-sign the N-of-N settlement of the escrow to the final stacks — all over the relay.
    const n = s.seats.length;
    const ocKey = walletKey; // the on-chain key IS the wallet key derived from this player's root
    log('on-chain mode: exchanging on-chain keys over the relay…');
    const pubHexes = await gatherByIndex(RELAY, table.id, 'oc-pub', s.mySeat, bytesToHex(ocKey.pubCompressed), n);
    const pubs = pubHexes.map((h) => Uint8Array.from(Buffer.from(h, 'hex')));
    const fundingScript = fundingLocking(BIND, pubs);
    const escrow = n * s.seats[0]!.stack * SCALE;
    if (!node) throw new Error('on-chain mode requires a node');

    let fundingTxid: string;
    if (s.mySeat === 0) {
      const funder = genKeyPair();
      const cb = await node.generateBlock(bytesToHex(funder.pubCompressed));
      const fundingTx: Tx = { version: 1, inputs: [{ prevTxid: cb.coinbaseTxid, vout: 0, sequence: 0xffffffff }], outputs: [{ satoshis: escrow, locking: fundingScript }, { satoshis: SUBSIDY - escrow - SETTLE_FEE, locking: p2pkh(funder.pubCompressed) }], nLockTime: 0 };
      const fs: Script = [sigT(sighashMessage(fundingTx, 0, p2pkh(funder.pubCompressed), SUBSIDY), funder), funder.pubCompressed];
      const r = await node.submitTx(bytesToHex(serializeTxWire(fundingTx, [fs])));
      if (!r.ok) throw new Error(`escrow funding rejected: ${r.reason}`);
      await node.generateBlock(bytesToHex(funder.pubCompressed));
      fundingTxid = txidWire(fundingTx, [fs]);
      log(`funded escrow ${escrow} sats (${fundingTxid.slice(0, 12)}…), broadcasting outpoint`);
      broadcastValue(RELAY, table.id, 'escrow', fundingTxid);
    } else {
      fundingTxid = await awaitValue(RELAY, table.id, 'escrow');
      log(`received escrow outpoint ${fundingTxid.slice(0, 12)}…`);
    }

    log('playing ONE hand over the wire, then settling on-chain…');
    await client.playSession({ maxHands: 1 });
    const finalStacks = (client.getState()?.seats ?? s.seats.map((x) => ({ seat: x.seat, stack: x.stack }))).map((x) => x.stack);

    const recips = finalStacks.map((c, i) => ({ i, sats: c * SCALE })).filter((r) => r.sats > 0).sort((a, b) => b.sats - a.sats);
    const outputs: TxOutput[] = recips.map((r, k) => ({ satoshis: k === 0 ? r.sats - SETTLE_FEE : r.sats, locking: p2pkh(pubs[r.i]!) }));
    const settleTx: Tx = { version: 1, inputs: [{ prevTxid: fundingTxid, vout: 0, sequence: 0xffffffff }], outputs, nLockTime: 0 };

    log('co-signing the on-chain settlement over the relay…');
    const res = await coSignSettlement({ relayUrl: RELAY, tableId: table.id, idx: s.mySeat, myKey: ocKey, settleTx, fundingScript, potValue: escrow, n, submit: s.mySeat === 0, submitTx: (raw) => node.submitTx(raw) });
    if (s.mySeat === 0 && res.txid) {
      await node.generateBlock(bytesToHex(genKeyPair().pubCompressed));
      const o0 = await node.outpointStatus(res.txid, 0);
      log(`SETTLED ON-CHAIN: settlement ${res.txid.slice(0, 12)}… output0=${o0.value} (confirmed=${o0.unspent})`);
    } else {
      log(`co-signed settlement (collected ${res.collected}/${n} sigs over the relay)`);
    }
    await node.shutdown();
    view.status = 'ended';
    if (GUI_PORT) await new Promise<void>(() => {});
    return;
  }

  log(`playing up to ${MAX_HANDS} hands over the wire…`);
  await client.playSession({ maxHands: MAX_HANDS });
  view.status = 'ended';
  log('session ended (busted, table empty, or hand cap reached)');
  // Keep the GUI up after the session so you can still watch the final table.
  if (GUI_PORT) await new Promise<void>(() => {});
}

main().then(() => process.exit(0), (e) => { console.error(`[bot ${NAME}] FAIL:`, (e as Error).message); process.exit(1); });
