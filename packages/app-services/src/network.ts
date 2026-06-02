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

async function asJson<T>(res: Response): Promise<T> {
  if (!res.ok) throw new Error(`HTTP ${res.status}: ${await res.text()}`);
  return (await res.json()) as T;
}

/** Tier-A discovery + Tier-B opaque fan-out (core §8.2). */
export class RelayClient {
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

  async createTable(id: string, name: string): Promise<TableInfo> {
    return asJson(
      await this.fetchFn(`${this.base}/tables`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ id, name }),
      }),
    );
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
        const res = await this.fetchFn(`${this.base}/tables/${tableId}/subscribe`, {
          signal: ac.signal,
          headers: { accept: 'text/event-stream' },
        });
        if (!res.ok || !res.body) return;
        const reader = (res.body as ReadableStream<Uint8Array>).getReader();
        const dec = new TextDecoder();
        let buf = '';
        for (;;) {
          const { value, done } = await reader.read();
          if (done) break;
          buf += dec.decode(value, { stream: true });
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
    const r = await asJson<{ delivered: number }>(
      await this.fetchFn(`${this.base}/tables/${tableId}/publish`, {
        method: 'POST',
        headers: { 'content-type': 'application/octet-stream' },
        body: object,
      }),
    );
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
