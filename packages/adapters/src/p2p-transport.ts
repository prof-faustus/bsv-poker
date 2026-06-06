/**
 * Peer-to-peer table transport (NO SERVER). bsv-poker is fully P2P: players exchange every
 * commit/reveal/action/seating frame DIRECTLY peer-to-peer over a gossip mesh — there is no relay
 * server, no central pub/sub. This is the drop-in replacement for the (removed) central relay: it
 * exposes the same `subscribe(table, cb)` / `publish(table, bytes)` seam the clients use, but the
 * bytes travel peer→peer.
 *
 * MODEL: every node is equal (listener + dialer). A published frame is delivered to the node's OWN
 * subscribers (the protocol relies on seeing its own commits, like an echo) AND flooded to every
 * connected peer; each peer delivers locally and RE-FLOODS to its other peers, with per-frame dedup
 * so the message reaches the whole mesh over any connected graph (no full mesh required, no loops).
 *
 * Stdlib only (node:net) — no external P2P library. Discovery (which peers to dial) is supplied
 * out-of-band here (a peer list); BSV IP-to-IP / Bitmessage-style serverless discovery layers on top
 * without changing this transport. Frames are newline-delimited JSON `{t, d, id}` (d = base64 bytes).
 */
import { createServer, connect, type Server, type Socket } from 'node:net';
import { randomBytes } from 'node:crypto';

interface Frame {
  t: string; // table id
  d: string; // base64 payload
  id: string; // dedup id
}

const MAX_FRAME_BYTES = 1 << 20; // 1 MiB per frame (CWE-400 bound)
const MAX_SEEN = 100_000; // dedup memory bound

export interface PeerAddr {
  readonly host: string;
  readonly port: number;
}

export class P2PTransport {
  private readonly port: number;
  private server: Server | null = null;
  private readonly peers = new Set<Socket>();
  private readonly subs = new Map<string, Set<(text: string) => void>>();
  private readonly seen = new Set<string>();
  private readonly seenOrder: string[] = [];
  private readonly dialing = new Map<string, ReturnType<typeof setInterval>>();
  private closed = false;

  constructor(port: number) {
    this.port = port;
  }

  /** Start listening and dial the given peers (retrying until connected). Resolves once listening. */
  async start(peers: readonly PeerAddr[] = []): Promise<void> {
    this.server = createServer((sock) => this.adopt(sock));
    await new Promise<void>((resolve, reject) => {
      this.server!.once('error', reject);
      this.server!.listen(this.port, '127.0.0.1', () => resolve());
    });
    for (const p of peers) this.dial(p);
  }

  /** Dial a peer, retrying until connected (a peer may not be listening yet). */
  dial(addr: PeerAddr): void {
    const key = `${addr.host}:${addr.port}`;
    if (this.dialing.has(key)) return;
    const attempt = (): void => {
      if (this.closed) return;
      const sock = connect(addr.port, addr.host);
      sock.once('connect', () => {
        const t = this.dialing.get(key);
        if (t) { clearInterval(t); this.dialing.delete(key); }
        this.adopt(sock);
      });
      sock.once('error', () => sock.destroy());
    };
    attempt();
    this.dialing.set(key, setInterval(attempt, 200));
  }

  private adopt(sock: Socket): void {
    this.peers.add(sock);
    sock.setNoDelay(true);
    let buf = '';
    sock.setEncoding('utf8');
    sock.on('data', (chunk: string) => {
      buf += chunk;
      if (buf.length > MAX_FRAME_BYTES) { sock.destroy(); return; } // unterminated/oversize frame
      let nl: number;
      while ((nl = buf.indexOf('\n')) >= 0) {
        const line = buf.slice(0, nl);
        buf = buf.slice(nl + 1);
        if (line) this.onFrame(line, sock);
      }
    });
    const drop = (): void => { this.peers.delete(sock); };
    sock.on('close', drop);
    sock.on('error', drop);
  }

  private onFrame(line: string, from: Socket): void {
    let f: Frame;
    try {
      const o = JSON.parse(line) as Frame;
      if (!o || typeof o.t !== 'string' || typeof o.d !== 'string' || typeof o.id !== 'string') return;
      f = o;
    } catch {
      return; // not a frame
    }
    if (this.markSeen(f.id)) return; // already processed → dedup (stops flood loops)
    this.deliverLocal(f);
    this.flood(line, from); // re-flood to other peers
  }

  private markSeen(id: string): boolean {
    if (this.seen.has(id)) return true;
    this.seen.add(id);
    this.seenOrder.push(id);
    if (this.seenOrder.length > MAX_SEEN) {
      const old = this.seenOrder.shift();
      if (old) this.seen.delete(old);
    }
    return false;
  }

  private deliverLocal(f: Frame): void {
    const set = this.subs.get(f.t);
    if (!set) return;
    const text = Buffer.from(f.d, 'base64').toString('utf8');
    for (const cb of [...set]) {
      try { cb(text); } catch { /* a subscriber callback must not break the mesh */ }
    }
  }

  private flood(line: string, except?: Socket): void {
    const payload = line + '\n';
    for (const peer of this.peers) {
      if (peer === except) continue;
      try { peer.write(payload); } catch { /* dropped peer */ }
    }
  }

  // ---- RelayClient-shaped seam (subscribe / publish) the clients use ----

  subscribe(tableId: string, onEvent: (text: string) => void): () => void {
    let set = this.subs.get(tableId);
    if (!set) this.subs.set(tableId, (set = new Set()));
    set.add(onEvent);
    return () => set!.delete(onEvent);
  }

  /** Publish a frame: deliver to OWN subscribers (echo) AND flood to peers. Returns the peer count. */
  async publish(tableId: string, object: Uint8Array): Promise<number> {
    const f: Frame = { t: tableId, d: Buffer.from(object).toString('base64'), id: randomBytes(12).toString('hex') };
    this.markSeen(f.id);
    this.deliverLocal(f); // the protocol relies on seeing its own commit/reveal
    const line = JSON.stringify(f);
    this.flood(line);
    return this.peers.size;
  }

  /** Number of currently-connected peers. */
  peerCount(): number {
    return this.peers.size;
  }

  /** Stop listening, drop all peers (ends the node's participation in the mesh). */
  close(): void {
    this.closed = true;
    for (const t of this.dialing.values()) clearInterval(t);
    this.dialing.clear();
    for (const s of this.peers) s.destroy();
    this.peers.clear();
    this.server?.close();
    this.server = null;
  }
}
