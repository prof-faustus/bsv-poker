/**
 * Conformance-bound in-memory FAKES for CT/BS/VA/OB (core §2.6, REQ-DEP-001/003). These are
 * used for ORCHESTRATION wiring in unit/integration tests only. They MUST pass the same
 * conformance suite as the real adapters (./conformance.ts) so a green run against a fake
 * cannot certify a wrong engine. Security-critical paths are NEVER tested against fakes
 * (REQ-DEP-004) — those use the real implementations (crypto-mentalpoker, the real repos).
 */

import { createHash } from 'node:crypto';
import type {
  BSContract,
  CTContract,
  MerkleBundle,
  OBContract,
  ShuffleInput,
  ShuffleResult,
  VAContract,
  ChannelParams,
} from './contracts.ts';

function sha256hex(b: Uint8Array): string {
  return createHash('sha256').update(b).digest('hex');
}
function concat(...parts: Uint8Array[]): Uint8Array {
  const len = parts.reduce((s, p) => s + p.length, 0);
  const out = new Uint8Array(len);
  let o = 0;
  for (const p of parts) {
    out.set(p, o);
    o += p.length;
  }
  return out;
}
const enc = (s: string): Uint8Array => new TextEncoder().encode(s);

/** Fake CT: real hashing for commit/reveal (so it is genuinely conformant), trivial shuffle. */
export function makeFakeCT(): CTContract {
  return {
    async entropyCommit(secret) {
      return sha256hex(secret);
    },
    async entropyReveal(commitment, secret) {
      return sha256hex(secret) === commitment.toLowerCase();
    },
    async runShuffle(input: ShuffleInput): Promise<ShuffleResult> {
      const seed = sha256hex(concat(...input.partyEntropy));
      const combinedKeys = Array.from({ length: input.deckSize }, (_, j) =>
        sha256hex(enc(`${seed}:Q:${j}`)),
      );
      return { orderCommitment: sha256hex(enc(`${seed}:order`)), combinedKeys, seed };
    },
    async conceal(_deckId, _serial, face, blind) {
      return sha256hex(concat(new Uint8Array([face]), blind));
    },
    async verifyReveal(commitment, face, blind) {
      return sha256hex(concat(new Uint8Array([face]), blind)) === commitment.toLowerCase();
    },
  };
}

export function makeFakeBS(): BSContract {
  const spent = new Set<string>();
  const broadcast = new Map<string, string>();
  return {
    async nodeBroadcast(rawTxHex) {
      const txid = sha256hex(sha256hexBytes(enc(rawTxHex)));
      broadcast.set(txid, rawTxHex);
      return { txid, status: 'accepted' };
    },
    async nodeOutpointStatus(txid, vout) {
      const key = `${txid}:${vout}`;
      if (spent.has(key)) return 'spent';
      return broadcast.has(txid) ? 'unspent' : 'unknown';
    },
    async channelOpen(params: ChannelParams) {
      if (params.bondSats !== 1) throw new Error('bond must be exactly 1 sat (INV-BS-2)');
      return sha256hex(enc(params.participants.join('|') + ':' + params.granularityK));
    },
    async channelTransfer() {
      /* in-memory no-op for orchestration */
    },
    reconcileQstar(microBalances, k) {
      // Largest-remainder apportionment to whole satoshis (no fractional output, INV-BS-1).
      const totalMicro = microBalances.reduce((s, x) => s + x, 0);
      const totalSat = Math.round(totalMicro / k);
      const exact = microBalances.map((m) => (m / k) * (totalSat / (totalMicro / k || 1)));
      const floors = exact.map((x) => Math.floor(x));
      let remainder = totalSat - floors.reduce((s, x) => s + x, 0);
      const order = exact
        .map((x, i) => ({ i, frac: x - Math.floor(x) }))
        .sort((a, b) => b.frac - a.frac);
      const out = [...floors];
      for (const { i } of order) {
        if (remainder <= 0) break;
        out[i]!++;
        remainder--;
      }
      return out;
    },
  };
}

function sha256hexBytes(b: Uint8Array): Uint8Array {
  return new Uint8Array(createHash('sha256').update(b).digest());
}

export function makeFakeVA(): VAContract {
  const hashPair = (a: string, b: string): string => sha256hex(enc(a < b ? a + b : b + a));
  return {
    boundary:
      'Establishes inclusion, integrity, selective disclosure, and arithmetic correctness ' +
      'over disclosed records ONLY — never truth-at-origin (INV-VA-2).',
    async merkleProve(records, index) {
      const leaves = records.map((r) => sha256hex(enc(r)));
      const path: { hashHex: string; right: boolean }[] = [];
      let idx = index;
      let level = leaves;
      while (level.length > 1) {
        const next: string[] = [];
        for (let i = 0; i < level.length; i += 2) {
          const left = level[i]!;
          const right = level[i + 1] ?? left;
          if (i === idx || i + 1 === idx) {
            const sibIsRight = idx % 2 === 0;
            path.push({ hashHex: sibIsRight ? right : left, right: sibIsRight });
          }
          next.push(hashPair(left, right));
        }
        idx = Math.floor(idx / 2);
        level = next;
      }
      return { root: level[0]!, leaf: leaves[index]!, path };
    },
    async merkleVerify(bundle: MerkleBundle) {
      let acc = bundle.leaf;
      for (const step of bundle.path) {
        acc = step.right ? hashPair(acc, step.hashHex) : hashPair(step.hashHex, acc);
      }
      return acc === bundle.root;
    },
  };
}

export function makeFakeOB(): OBContract {
  return {
    async wrap(keyHex, memberPubKey) {
      // Authenticated wrap stand-in (never raw XOR): tag = H(key‖member).
      return sha256hex(enc(`${keyHex}|${memberPubKey}`)) + ':' + keyHex;
    },
    async unwrap(wrappedHex, memberPrivKey) {
      void memberPrivKey;
      const idx = wrappedHex.indexOf(':');
      if (idx < 0) throw new Error('bad wrap');
      return wrappedHex.slice(idx + 1);
    },
    async isRevoked(sessionId, height) {
      // Revocation = unspent expiring output (INV-OB-2): revoked once height passes expiry.
      // Fake expiry encoded in the sessionId suffix "@<height>".
      const at = sessionId.lastIndexOf('@');
      if (at < 0) return false;
      const expiry = Number.parseInt(sessionId.slice(at + 1), 10);
      return Number.isFinite(expiry) && height > expiry;
    },
    async thresholdSplit(secretHex, t, n) {
      if (t > n || t < 1) throw new Error('bad threshold');
      return Array.from({ length: n }, (_, i) => sha256hex(enc(`${secretHex}:share:${i}:${t}`)));
    },
  };
}
