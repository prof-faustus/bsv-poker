/**
 * Network clients (app §A7, core §8) — the connection manager's transport to the relay
 * (transport/index only, never source of truth) and the indexer (per-table tx projections).
 * Uses the global `fetch` (Node 24 + browsers). The relay/indexer treat payloads as OPAQUE;
 * the client owns truth by re-deriving state from the valid tx set (REQ-NET-001, P3).
 *
 * Dual-path send (REQ-NET-003): an action goes to the indexer/node as a tx record (canonical)
 * AND to table peers via the relay channel (speed). The speed path never overrides canonical.
 */

export interface PresenceEntry {
  playerId: string;
  addr: string;
}
export interface TableInfo {
  id: string;
  name: string;
  members: number;
}
export interface TxRecord {
  txid: string;
  class: string;
  tableId: string;
  /** opaque bytes (base64) — the indexer never parses game logic. */
  raw?: string;
}

type FetchFn = typeof fetch;

/** Hard cap on a buffered HTTP JSON response (CWE-400). The relay/indexer payloads are small. */
const MAX_HTTP_JSON = 16 * 1024 * 1024; // 16 MiB
/** Hard cap on the unframed SSE accumulation buffer (CWE-400) — a frame must arrive within this. */
const MAX_SSE_BUFFER = 1 << 20; // 1 MiB

async function asJson<T>(res: Response): Promise<T> {
  if (!res.ok) throw new Error(`HTTP ${res.status}: ${await res.text()}`);
  // Reject an over-large declared body before buffering it (defense-in-depth; the relay sets
  // content-length). The body is still our own infra, but we never buffer unbounded bytes.
  const len = res.headers?.get?.('content-length') ?? null;
  if (len !== null) {
    const n = Number(len);
    if (Number.isFinite(n) && n > MAX_HTTP_JSON) throw new Error(`response too large: ${n} bytes`);
  }
  return (await res.json()) as T;
}

/** Tier-A discovery + Tier-B opaque fan-out (core §8.2). */
export class RelayClient {
  private readonly base: string;
  private readonly fetchFn: FetchFn;
  // Capability tokens (audit 5): the relay requires a table-scoped token to publish/subscribe. The
  // client mints + caches one per table transparently; gated tables need an admission secret.
  private readonly tokens = new Map<string, string>();
  private readonly admissions = new Map<string, string>();
  constructor(base: string, fetchFn: FetchFn = globalThis.fetch.bind(globalThis)) {
    this.base = base;
    this.fetchFn = fetchFn;
  }

  /** Register the admission secret for a GATED table so this client can mint its capability. */
  setAdmission(tableId: string, secret: string): void {
    this.admissions.set(tableId, secret);
  }

  /**
   * Ensure a capability token for `tableId` is cached, minting one if needed (audit 5). For a gated
   * table the registered admission secret is presented. `force` re-mints (e.g. after a 401/403).
   */
  private async ensureToken(tableId: string, force = false): Promise<string> {
    if (!force) {
      const cached = this.tokens.get(tableId);
      if (cached) return cached;
    }
    const admission = this.admissions.get(tableId);
    const res = await this.fetchFn(`${this.base}/tables/${tableId}/capability`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(admission ? { admission } : {}),
    });
    const r = await asJson<{ token: string }>(res);
    if (!r.token) throw new Error('relay returned no capability token');
    this.tokens.set(tableId, r.token);
    return r.token;
  }

  async health(): Promise<boolean> {
    try {
      const res = await this.fetchFn(`${this.base}/healthz`);
      return res.ok;
    } catch {
      return false;
    }
  }

  async heartbeat(playerId: string, addr: string): Promise<void> {
    await asJson(
      await this.fetchFn(`${this.base}/presence`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ playerId, addr }),
      }),
    );
  }

  async listPresence(): Promise<PresenceEntry[]> {
    return asJson(await this.fetchFn(`${this.base}/presence`));
  }

  /**
   * Create a table. `admission` (optional) GATES the table: only callers presenting this secret can
   * mint a capability for it. The creator's capability token is captured for immediate publish/sub.
   */
  async createTable(id: string, name: string, admission?: string): Promise<TableInfo> {
    if (admission) this.admissions.set(id, admission);
    const r = await asJson<TableInfo & { token?: string }>(
      await this.fetchFn(`${this.base}/tables`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(admission ? { id, name, admission } : { id, name }),
      }),
    );
    if (r.token) this.tokens.set(id, r.token);
    return { id: r.id, name: r.name, members: r.members };
  }

  async listTables(): Promise<TableInfo[]> {
    return asJson(await this.fetchFn(`${this.base}/tables`));
  }

  /**
   * Tier-B subscribe: stream opaque table objects (SSE `data: <json>` frames) to `onEvent`.
   * Returns an unsubscribe function. The relay never interprets the objects (REQ-NET-001).
   */
  subscribe(tableId: string, onEvent: (text: string) => void): () => void {
    const ac = new AbortController();
    void (async () => {
      try {
        // Mint/attach a capability token (audit 5); pass it as a query param since SSE consumers
        // can't always set headers. Re-mint once if the relay rejects a stale token.
        let token = await this.ensureToken(tableId);
        let res = await this.fetchFn(`${this.base}/tables/${tableId}/subscribe?token=${encodeURIComponent(token)}`, {
          signal: ac.signal,
          headers: { accept: 'text/event-stream', authorization: `Bearer ${token}` },
        });
        if (res.status === 401 || res.status === 403) {
          token = await this.ensureToken(tableId, true);
          res = await this.fetchFn(`${this.base}/tables/${tableId}/subscribe?token=${encodeURIComponent(token)}`, {
            signal: ac.signal,
            headers: { accept: 'text/event-stream', authorization: `Bearer ${token}` },
          });
        }
        if (!res.ok || !res.body) return;
        const reader = (res.body as ReadableStream<Uint8Array>).getReader();
        const dec = new TextDecoder();
        let buf = '';
        for (;;) {
          const { value, done } = await reader.read();
          if (done) break;
          buf += dec.decode(value, { stream: true });
          // Bound the accumulation: an event frame must complete within MAX_SSE_BUFFER, else the
          // peer is feeding us an unterminated frame — abort rather than grow without limit.
          if (buf.length > MAX_SSE_BUFFER) {
            ac.abort();
            break;
          }
          let nl: number;
          while ((nl = buf.indexOf('\n\n')) >= 0) {
            const frame = buf.slice(0, nl);
            buf = buf.slice(nl + 2);
            for (const line of frame.split('\n')) {
              if (line.startsWith('data: ')) onEvent(line.slice(6));
            }
          }
        }
      } catch {
        /* aborted or stream closed */
      }
    })();
    return () => ac.abort();
  }

  /** Speed path: publish an opaque object to the table channel; returns delivery count. */
  async publish(tableId: string, object: Uint8Array): Promise<number> {
    // Attach a capability token (audit 5); re-mint once if the relay rejects a stale/expired token.
    let token = await this.ensureToken(tableId);
    let res = await this.fetchFn(`${this.base}/tables/${tableId}/publish`, {
      method: 'POST',
      headers: { 'content-type': 'application/octet-stream', authorization: `Bearer ${token}` },
      body: object,
    });
    if (res.status === 401 || res.status === 403) {
      token = await this.ensureToken(tableId, true);
      res = await this.fetchFn(`${this.base}/tables/${tableId}/publish`, {
        method: 'POST',
        headers: { 'content-type': 'application/octet-stream', authorization: `Bearer ${token}` },
        body: object,
      });
    }
    const r = await asJson<{ delivered: number }>(res);
    return r.delivered;
  }
}

/** Per-table tx projection (core §8.4). The projection is reconstructible by any client (P2). */
export class IndexerClient {
  private readonly base: string;
  private readonly fetchFn: FetchFn;
  constructor(base: string, fetchFn: FetchFn = globalThis.fetch.bind(globalThis)) {
    this.base = base;
    this.fetchFn = fetchFn;
  }

  async health(): Promise<boolean> {
    try {
      const res = await this.fetchFn(`${this.base}/healthz`);
      return res.ok;
    } catch {
      return false;
    }
  }

  /**
   * Register a table's authoritative seat→pubkey map for the indexer's VALIDATING mode (audit 7).
   * This is the lobby's agreed seating; the indexer then authenticates every ingested envelope
   * against these keys. A no-op-safe call against an opaque indexer (it simply 404s/ignores). Returns
   * true on success. Idempotent at the indexer; a conflicting re-registration is refused there.
   */
  async register(tableId: string, seats: ReadonlyArray<{ seat: number; pub: string }>): Promise<boolean> {
    const res = await this.fetchFn(`${this.base}/table/${tableId}/register`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ seats }),
    });
    if (!res.ok) return false;
    const r = (await res.json()) as { registered?: boolean };
    return r.registered === true;
  }

  /** Canonical path: ingest a tx record; returns whether it was newly added (dedup by txid). */
  async ingest(rec: TxRecord): Promise<boolean> {
    const r = await asJson<{ added: boolean }>(
      await this.fetchFn(`${this.base}/ingest`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(rec),
      }),
    );
    return r.added;
  }

  /** The ordered txid projection for a table (deterministic; REQ-NET-006/007). */
  async table(tableId: string): Promise<string[]> {
    const r = await asJson<{ tableId: string; txids: string[] }>(
      await this.fetchFn(`${this.base}/table/${tableId}`),
    );
    return r.txids;
  }

  /** The FULL ordered records (the transcript) — for reconnect/rebuild (REQ-NET-007). */
  async records(tableId: string): Promise<TxRecord[]> {
    const r = await asJson<{ tableId: string; records: TxRecord[] }>(
      await this.fetchFn(`${this.base}/table/${tableId}/records`),
    );
    return r.records ?? [];
  }
}
