/**
 * The player's OWN local node (NOT a server). A browser webview cannot open raw TCP sockets, so it
 * cannot itself be a peer on the node:net gossip mesh. In a fully peer-to-peer system the standard
 * answer is: each player runs THEIR OWN local node, and the browser talks only to localhost — exactly
 * like a wallet talking to your own bitcoind. There is NO central server: every player runs their own
 * node, and the nodes connect to EACH OTHER over the P2P mesh.
 *
 * This process:
 *   - is a `P2PTransport` peer on the table mesh (dials `--peer`, listens on `--listen`), and
 *   - exposes the EXACT HTTP+SSE surface the in-tree browser `RelayClient` already speaks, bound to
 *     127.0.0.1 ONLY, for the player's OWN browser. Each HTTP request is bridged to this node's
 *     `P2PTransport`, so the browser's publish/subscribe/discovery all travel peer-to-peer.
 *
 * Because it binds loopback only and is one-per-player, it is the player's own node — not a relay
 * everyone shares. The browser never reaches another player directly; it reaches its own node, which
 * gossips to the others. (Capability tokens are a no-op here: there is no central gatekeeper; access
 * control is the high-entropy table id + the signed-seating/seat-key layer above this transport.)
 *
 *   node tools/local-node.ts --http 8090 [--listen 9700] [--peer 127.0.0.1:9701]
 */
import { createServer, type IncomingMessage, type ServerResponse } from 'node:http';
import { P2PTransport } from '@bsv-poker/adapters/p2p-transport';

function arg(name: string, fb?: string): string | undefined {
  const i = process.argv.indexOf(name);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : fb;
}
const HTTP_PORT = Number(arg('--http', '8090'));
const LISTEN_PORT = arg('--listen') ? Number(arg('--listen')) : 0;
const PEER = arg('--peer');

const MAX_BODY = 1 << 20; // 1 MiB publish body bound (CWE-400)

function cors(res: ServerResponse): void {
  res.setHeader('access-control-allow-origin', '*');
  res.setHeader('access-control-allow-headers', 'content-type, authorization');
  res.setHeader('access-control-allow-methods', 'GET, POST, OPTIONS');
}
function json(res: ServerResponse, code: number, body: unknown): void {
  cors(res);
  res.writeHead(code, { 'content-type': 'application/json' });
  res.end(JSON.stringify(body));
}
function readBody(req: IncomingMessage): Promise<Uint8Array> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    let n = 0;
    req.on('data', (d: Buffer) => {
      n += d.length;
      if (n > MAX_BODY) { reject(new Error('body too large')); req.destroy(); return; }
      chunks.push(d);
    });
    req.on('end', () => resolve(new Uint8Array(Buffer.concat(chunks))));
    req.on('error', reject);
  });
}

async function main(): Promise<void> {
  const transport = new P2PTransport(LISTEN_PORT);
  await transport.start(PEER ? [{ host: PEER.split(':')[0]!, port: Number(PEER.split(':')[1]) }] : []);

  const server = createServer((req, res) => {
    void (async () => {
      try {
        const url = new URL(req.url ?? '/', 'http://127.0.0.1');
        const path = url.pathname;
        if (req.method === 'OPTIONS') { cors(res); res.writeHead(204); res.end(); return; }

        if (req.method === 'GET' && path === '/healthz') { json(res, 200, { ok: true }); return; }

        // Capability token: no central gatekeeper over P2P — issue a benign token the client caches.
        const capMatch = path.match(/^\/tables\/([^/]+)\/capability$/);
        if (req.method === 'POST' && capMatch) { json(res, 200, { token: 'local' }); return; }

        if (req.method === 'POST' && path === '/presence') {
          const b = JSON.parse(new TextDecoder().decode(await readBody(req))) as { playerId: string; addr: string };
          await transport.heartbeat(b.playerId, b.addr);
          json(res, 200, { ok: true });
          return;
        }
        if (req.method === 'GET' && path === '/presence') { json(res, 200, await transport.listPresence()); return; }

        if (req.method === 'POST' && path === '/tables') {
          const b = JSON.parse(new TextDecoder().decode(await readBody(req))) as { id: string; name: string; admission?: string };
          const info = await transport.createTable(b.id, b.name, b.admission);
          json(res, 200, { ...info, token: 'local' });
          return;
        }
        if (req.method === 'GET' && path === '/tables') { json(res, 200, await transport.listTables()); return; }

        const subMatch = path.match(/^\/tables\/([^/]+)\/subscribe$/);
        if (req.method === 'GET' && subMatch) {
          const tableId = decodeURIComponent(subMatch[1]!);
          cors(res);
          res.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-cache', connection: 'keep-alive' });
          const unsub = transport.subscribe(tableId, (text) => {
            // SSE frame: one `data:` line per event, terminated by a blank line.
            res.write(`data: ${text.replace(/\n/g, '\\n')}\n\n`);
          });
          const keepalive = setInterval(() => res.write(': ping\n\n'), 15000);
          req.on('close', () => { clearInterval(keepalive); unsub(); });
          return;
        }

        const pubMatch = path.match(/^\/tables\/([^/]+)\/publish$/);
        if (req.method === 'POST' && pubMatch) {
          const tableId = decodeURIComponent(pubMatch[1]!);
          const delivered = await transport.publish(tableId, await readBody(req));
          json(res, 200, { delivered });
          return;
        }

        json(res, 404, { error: 'not found' });
      } catch (e) {
        json(res, 500, { error: (e as Error).message });
      }
    })();
  });

  await new Promise<void>((resolve) => server.listen(HTTP_PORT, '127.0.0.1', () => resolve()));
  console.log(`[local-node] the player's OWN node: HTTP bridge http://127.0.0.1:${HTTP_PORT} ↔ P2P mesh (listen ${transport.boundPort()}${PEER ? `, peer ${PEER}` : ''}). Loopback only — not a central server.`);
}

main().catch((e) => { console.error('[local-node] FAIL:', (e as Error).message); process.exit(1); });
