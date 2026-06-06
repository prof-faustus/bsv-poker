/**
 * Bot daemon — a SIMULATED REMOTE PLAYER, run as its own process, that joins a table PEER-TO-PEER
 * exactly like a human's client (core §8, D4). The bot is NOT in the main app and has no in-process
 * access to anyone else's state (that would be a cheat): it is its OWN P2P node with its own identity
 * + entropy, dials a peer into the mesh, joins the waiting room over the gossip channel, and plays its
 * seat by submitting actions over the same networked protocol a human uses. There is NO relay server.
 * Run two windows (app + bot, or two bots) to really test multiplayer over the wire.
 *
 *   node tools/bot-daemon.ts --peer 127.0.0.1:9700 [--table <id>] [--name alice] [--strategy passive|aggressive]
 */

import { randomBytes } from 'node:crypto';
import { createServer } from 'node:http';
import { createPublicKey, createPrivateKey } from 'node:crypto';
import {
  LobbyClient,
  InteractiveNetworkedTableClient,
  sessionAuthFromSeed,
  deriveSeatSeed,
  assertRealValueReady,
  perHandEntropy,
  type RelayChannel,
  type ClientUpdate,
  type OpenTable,
  type SeatDrop,
} from '@bsv-poker/app-services';
import { P2PTransport } from '@bsv-poker/adapters/p2p-transport';
import { OP, genKeyPair, signPreimage, compressedPub, fairPlayCommitment, fundingLocking, fundingUnlocking, bondRevealOrForfeitLocking, type Script, type KeyPair } from '@bsv-poker/script-templates-ts';
import { bytesToHex, sha256, hexToBytes, cardToString, type Action, type BranchBinding, type GameState, type LegalActions } from '@bsv-poker/protocol-types';
import { RealBsvNode } from '@bsv-poker/adapters/real-node';
import { ForfeitureCoordinator, type SeatBond } from '@bsv-poker/adapters/forfeiture-coordinator';
import { type Tx, type TxOutput, serializeTxWire, txidWire, sighashMessage, buildTimeoutRefund, type FundingRef, type Contributor } from '@bsv-poker/tx-builder';

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

const PEER = arg('--peer'); // host:port of a peer already in the mesh to dial (serverless rendezvous)
const LISTEN_PORT = arg('--listen') ? Number(arg('--listen')) : 0; // this node's own listen port (0 = ephemeral)
const NAME = arg('--name', `bot-${Math.random().toString(36).slice(2, 6)}`)!;
const WANT_TABLE = arg('--table');
const STRATEGY = arg('--strategy', 'passive')!;
const MAX_HANDS = Number(arg('--hands', '100'));
const GUI_PORT = arg('--gui') ? Number(arg('--gui')) : 0;
const NODE_PORT = arg('--node') ? Number(arg('--node')) : 0; // on-chain settlement against this node
const STALL_REVEAL = process.argv.includes('--stall-reveal'); // TEST: commit but never reveal (forfeit demo)
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
  // This bot IS its own P2P node: listen, then dial a peer already in the mesh (serverless rendezvous).
  const baseRelay = new P2PTransport(LISTEN_PORT);
  const peers = PEER ? [{ host: PEER.split(':')[0]!, port: Number(PEER.split(':')[1]) }] : [];
  await baseRelay.start(peers);
  if (PEER) {
    const dl = Date.now() + 30000;
    while (baseRelay.peerCount() < 1) {
      if (Date.now() > dl) throw new Error(`could not connect to peer ${PEER}`);
      await new Promise((r) => setTimeout(r, 100));
    }
  }
  // TEST-ONLY stall: swallow this bot's own deck-handshake REVEAL frames (t==='reveal'), so it commits
  // but never reveals — the survivors drop it and FORFEIT its bond (audit #19 forfeit demonstration).
  // Seating (t==='seat-reveal') is untouched, so the stalling bot still takes its seat.
  const relay: RelayChannel = STALL_REVEAL
    ? {
        subscribe: (t: string, cb: (x: string) => void) => baseRelay.subscribe(t, cb),
        publish: async (t: string, bytes: Uint8Array) => {
          try {
            if ((JSON.parse(new TextDecoder().decode(bytes)) as { t?: string }).t === 'reveal') return 0;
          } catch { /* not JSON — forward */ }
          return baseRelay.publish(t, bytes);
        },
      }
    : baseRelay;
  const lobby = new LobbyClient(baseRelay);
  const id = `${NAME}-${Math.random().toString(36).slice(2, 8)}`;
  // ONE root for this player: the wallet/on-chain key AND the Ed25519 seat key derive from it, so a
  // valid seat signature proves control of the same root that funds the buy-in (audit 3).
  const root = new Uint8Array(randomBytes(32));
  const auth = await sessionAuthFromSeed(deriveSeatSeed(root, 'bsv-poker/seat-ed25519'));
  const walletKey = keyPairFromScalar(deriveSeatSeed(root, 'bsv-poker/wallet')); // same root → wallet key
  const pub = auth.pub; // seat identity = the key it signs envelopes with (rooted in the wallet)

  log(`joining the mesh${PEER ? ` via peer ${PEER}` : ""} as a remote player…`);
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

  // This player's per-table entropy (the per-hand secret + the reveal-bond commitment derive from it).
  const botEntropy = new Uint8Array(randomBytes(32));
  // On-chain accountability (audit #19): a forfeiture coordinator is wired in below (seat 0) once the
  // per-seat reveal bonds are known; the client's drop event records a non-revealer's forfeiture here.
  let coordinator: ForfeitureCoordinator | null = null;

  const client = new InteractiveNetworkedTableClient({
    relay,
    tableId: table.id,
    mySeat: s.mySeat,
    seats: s.seats,
    ruleset: s.ruleset,
    entropy: botEntropy,
    auth, // sign every envelope I emit
    seatPubs: s.players.map((p) => p.pub), // verify peers: seat → registered session key
    ...(node ? { heightSource: cachedHeight } : {}),
    // Wire the on-chain forfeiture into the LIVE play path: when this client drops a non-revealer at
    // the anchored reveal deadline, record the forfeiture of that seat's bond (the coordinator submits
    // it after maturity). Only the pot beneficiary (seat 0) holds the key to claim, so only it acts.
    onSeatDropped: (d: SeatDrop) => coordinator?.record(d),
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
    // PRODUCTION-READINESS GATE (audit #34): before this client touches the on-chain value path it
    // asserts the deployment is ready for its network. This bot runs the regtest node (play-regtest →
    // no real funds), so only the network selection is required; a mainnet deployment would have to
    // satisfy EVERY invariant (signed, BIP-143/FORKID sighash, real custody, managed secret, loopback)
    // or this throws — production readiness is enforced, not assumed.
    assertRealValueReady({
      network: 'play-regtest',
      signingRequired: true, // this client always signs (auth mandatory; allowUnsigned is off)
      sighash: 'bip143-forkid', // settlement signs sighashMessage (real BIP-143/FORKID)
      custody: 'software', // keyPairFromScalar holds the on-chain key
      relaySecretConfigured: true,
      bindHost: '127.0.0.1',
    });
    // ON-CHAIN MODE: exchange on-chain keys over the relay, fund the escrow (seat 0), play one hand,
    // then co-sign the N-of-N settlement of the escrow to the final stacks — all over the relay.
    const n = s.seats.length;
    const ocKey = walletKey; // the on-chain key IS the wallet key derived from this player's root
    log('on-chain mode: exchanging on-chain keys over the relay…');
    const pubHexes = await gatherByIndex(baseRelay, table.id, 'oc-pub', s.mySeat, bytesToHex(ocKey.pubCompressed), n);
    const pubs = pubHexes.map((h) => Uint8Array.from(Buffer.from(h, 'hex')));
    const fundingScript = fundingLocking(BIND, pubs);
    const escrow = n * s.seats[0]!.stack * SCALE;
    if (!node) throw new Error('on-chain mode requires a node');

    // GUARANTEED nLockTime RECOVERY BEFORE RISK (life-critical — memory always-nlocktime-recovery-all-
    // funds): the escrow is N-of-N, so before a single sat is locked on-chain the funder (seat 0) must
    // already hold a unilateral, time-locked recovery returning 100% of the escrow. Seat 0 builds the
    // escrow funding tx but DOES NOT broadcast it; everyone co-signs the recovery referencing its txid;
    // ONLY once the recovery is in hand does seat 0 broadcast the funding. If the game closes poorly,
    // seat 0 broadcasts the recovery after its nLockTime and recovers every sat — no party can strand it.
    let fundingTxid: string;
    let pendingFunding: { raw: string; payoutPub: Uint8Array } | null = null;
    let recoverableAtHeight: number;
    if (s.mySeat === 0) {
      const funder = genKeyPair();
      const cb = await node.generateBlock(bytesToHex(funder.pubCompressed));
      const fundingTx: Tx = { version: 1, inputs: [{ prevTxid: cb.coinbaseTxid, vout: 0, sequence: 0xffffffff }], outputs: [{ satoshis: escrow, locking: fundingScript }, { satoshis: SUBSIDY - escrow - SETTLE_FEE, locking: p2pkh(funder.pubCompressed) }], nLockTime: 0 };
      const fs: Script = [sigT(sighashMessage(fundingTx, 0, p2pkh(funder.pubCompressed), SUBSIDY), funder), funder.pubCompressed];
      fundingTxid = txidWire(fundingTx, [fs]); // known BEFORE broadcast
      pendingFunding = { raw: bytesToHex(serializeTxWire(fundingTx, [fs])), payoutPub: funder.pubCompressed };
      recoverableAtHeight = (await node.height()) + 10_000; // unilateral exit opens far past any hand
      // Share the (not-yet-broadcast) outpoint + the agreed recovery locktime so all seats build the
      // IDENTICAL recovery and co-sign it before any money is locked.
      broadcastValue(baseRelay, table.id, 'escrow-pre', `${fundingTxid}:${recoverableAtHeight}`);
    } else {
      const pre = await awaitValue(baseRelay, table.id, 'escrow-pre');
      const [txid, heightStr] = pre.split(':');
      fundingTxid = txid!;
      recoverableAtHeight = Number(heightStr);
      log(`escrow outpoint ${fundingTxid.slice(0, 12)}… (recovery opens at height ${recoverableAtHeight})`);
    }

    // Every seat co-signs the N-of-N recovery (escrow → seat 0, 100% minus a miner fee, time-locked).
    const funding: FundingRef = { txid: fundingTxid, vout: 0, value: escrow, scriptCode: fundingScript };
    const recoveryContribs: Contributor[] = [{ pub: pubs[0]!, amount: escrow }]; // the funder recovers all
    const recoveryTx = buildTimeoutRefund(BIND, funding, recoveryContribs, { fee: SETTLE_FEE, sequence: 0xfffffffe, nLockTime: recoverableAtHeight });
    const recoveryMsg = sighashMessage(recoveryTx, 0, fundingScript, escrow);
    const recoverySigsHex = await gatherByIndex(baseRelay, table.id, 'recov-sig', s.mySeat, bytesToHex(sigT(recoveryMsg, ocKey)), n);
    const recoveryScriptSig = fundingUnlocking(recoverySigsHex.map((h) => Uint8Array.from(Buffer.from(h, 'hex'))));
    const recoveryRaw = bytesToHex(serializeTxWire(recoveryTx, [recoveryScriptSig])); // held by every seat
    log(`hold unilateral nLockTime recovery (${txidWire(recoveryTx, [recoveryScriptSig]).slice(0, 12)}…) for 100% of the escrow BEFORE funding`);

    if (s.mySeat === 0) {
      // Recovery is in hand → NOW it is safe to lock the escrow on-chain.
      const r = await node.submitTx(pendingFunding!.raw);
      if (!r.ok) throw new Error(`escrow funding rejected: ${r.reason}`);
      await node.generateBlock(bytesToHex(pendingFunding!.payoutPub));
      log(`funded escrow ${escrow} sats (${fundingTxid.slice(0, 12)}…) — recovery already held by all seats`);
      broadcastValue(baseRelay, table.id, 'escrow', fundingTxid);
    } else {
      await awaitValue(baseRelay, table.id, 'escrow'); // wait until the funder confirms it is on-chain
    }
    void recoveryRaw; // the close-condition the player broadcasts (after the locktime) if the game ends badly

    // ACCOUNTABILITY BONDS (audit #19) — every seat posts a per-hand REVEAL bond locked to its hand-0
    // commit (c0 = SHA-256(perHandEntropy(entropy,0))), forfeitable to the pot beneficiary (seat 0) if
    // it COMMITS but never REVEALS. Outpoints + commitments are exchanged over the relay; seat 0 builds
    // the coordinator and forfeits any non-revealer's bond it dropped (recorded via onSeatDropped).
    const BOND = s.seats[0]!.stack * SCALE;
    const benePub = pubs[0]!; // the pot beneficiary that claims forfeited bonds
    const c0 = sha256(perHandEntropy(botEntropy, 0)); // my hand-0 commit == my bond's reveal commitment
    const bondFunder = genKeyPair();
    const bcb = await node.generateBlock(bytesToHex(bondFunder.pubCompressed));
    const bondTx: Tx = { version: 1, inputs: [{ prevTxid: bcb.coinbaseTxid, vout: 0, sequence: 0xffffffff }], outputs: [{ satoshis: BOND, locking: bondRevealOrForfeitLocking(BIND, c0, ocKey.pubCompressed, benePub) }], nLockTime: 0 };
    const bfs: Script = [sigT(sighashMessage(bondTx, 0, p2pkh(bondFunder.pubCompressed), SUBSIDY), bondFunder), bondFunder.pubCompressed];
    const br = await node.submitTx(bytesToHex(serializeTxWire(bondTx, [bfs])));
    if (!br.ok) throw new Error(`bond funding rejected: ${br.reason}`);
    await node.generateBlock(bytesToHex(bondFunder.pubCompressed));
    const myBondTxid = txidWire(bondTx, [bfs]);
    log(`posted reveal bond ${BOND} sats (${myBondTxid.slice(0, 12)}…)`);
    const bondInfos = await gatherByIndex(baseRelay, table.id, 'bond', s.mySeat, `${myBondTxid}:0:${bytesToHex(c0)}`, n);
    if (s.mySeat === 0) {
      const bonds = new Map<number, SeatBond>();
      bondInfos.forEach((info, seat) => {
        const [txid, voutStr, commitment] = info.split(':');
        const script = bondRevealOrForfeitLocking(BIND, hexToBytes(commitment!), pubs[seat]!, benePub);
        bonds.set(seat, { txid: txid!, vout: Number(voutStr), script, value: BOND, commitment: commitment! });
      });
      coordinator = new ForfeitureCoordinator({ node, beneficiary: ocKey, beneficiaryPayout: p2pkh(benePub), bonds });
    }

    log('playing ONE hand over the wire, then settling on-chain…');
    let handOk = true;
    try {
      await client.playSession({ maxHands: 1 });
    } catch (e) {
      // A non-revealer can leave too few active seats to finish the hand; that is exactly the case the
      // bond forfeiture below penalises. Settle the forfeiture regardless of whether the hand completed.
      handOk = false;
      log(`hand aborted: ${(e as Error).message}`);
    }
    // If a seat was dropped for never revealing, FORFEIT its bond on-chain in this LIVE path (seat 0
    // holds the beneficiary key). This is the accountability penalty, invoked from real play.
    if (s.mySeat === 0 && coordinator && coordinator.pendingSeats().length > 0) {
      for (let k = 0; k < 5; k++) await node.generateBlock(bytesToHex(genKeyPair().pubCompressed)); // mature the deadline
      for (const r of (await coordinator.settle()).filter((x) => x.submitted)) log(`FORFEITED non-revealer seat ${r.seat}'s bond on-chain`);
    }
    if (!handOk) {
      log('no settlement — the hand did not complete (a seat was dropped); accountability was enforced via bond forfeiture');
      await node.shutdown();
      view.status = 'ended';
      if (GUI_PORT) await new Promise<void>(() => {});
      return;
    }
    const finalStacks = (client.getState()?.seats ?? s.seats.map((x) => ({ seat: x.seat, stack: x.stack }))).map((x) => x.stack);

    const recips = finalStacks.map((c, i) => ({ i, sats: c * SCALE })).filter((r) => r.sats > 0).sort((a, b) => b.sats - a.sats);
    const outputs: TxOutput[] = recips.map((r, k) => ({ satoshis: k === 0 ? r.sats - SETTLE_FEE : r.sats, locking: p2pkh(pubs[r.i]!) }));
    const settleTx: Tx = { version: 1, inputs: [{ prevTxid: fundingTxid, vout: 0, sequence: 0xffffffff }], outputs, nLockTime: 0 };

    log('co-signing the on-chain settlement over the relay…');
    const res = await coSignSettlement({ relay: baseRelay, tableId: table.id, idx: s.mySeat, myKey: ocKey, settleTx, fundingScript, potValue: escrow, n, submit: s.mySeat === 0, submitTx: (raw) => node.submitTx(raw) });
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
