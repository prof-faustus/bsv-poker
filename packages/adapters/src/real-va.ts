/**
 * Binds the REAL `verifiable-accounting-chain` Merkle implementation to the poker audit trail
 * (REQ-DEP-004, core §17). No reimplementation: the real `@vaa/bsv` (`hashLeaf`/`hashNode`,
 * double-SHA-256 via @bsv/sdk) and `@vaa/merkle` (`buildTree`/`merkleProof`/`verifyProof`) hash
 * each per-hand settlement record, build the Merkle root, and prove/verify inclusion. The VA
 * library is loaded from its built dist (its own node_modules resolve @bsv/sdk), exactly as the
 * real BSV node is bound by process — a genuine dependency, not a conformant fake.
 */

import { pathToFileURL } from 'node:url';
import { join } from 'node:path';
import type { VAContract } from './contracts.ts';

const VA_DIR = process.env.BSV_VA_DIR ?? 'D:\\claude\\verifiable-accounting-chain';

// The real library's branded Hash and its Result/VerifyResult envelopes.
type Hash = unknown;
interface Ok<T> { readonly ok: true; readonly value: T }
interface VerifyOk { readonly ok: true }
type Result<T> = Ok<T> | { readonly ok: false };
type VerifyResult = VerifyOk | { readonly ok: false };

interface VaBsv {
  hashLeaf(data: Uint8Array): Hash;
  HashOps: { toDisplayHex(h: Hash): string; fromDisplayHex(hex: string): Result<Hash> };
}
interface VaMerkle {
  buildTree(leaves: Hash[]): Result<{ root: Hash }>;
  merkleProof(leaves: Hash[], index: number): Result<{ index: number; siblings: Hash[] }>;
  verifyProof(leaf: Hash, proof: { index: number; siblings: Hash[] }, root: Hash): VerifyResult;
  reconstructRoot(leaf: Hash, proof: { index: number; siblings: Hash[] }): Hash;
}

let cache: { bsv: VaBsv; merkle: VaMerkle } | null = null;
async function lib(): Promise<{ bsv: VaBsv; merkle: VaMerkle }> {
  if (cache) return cache;
  const bsv = (await import(pathToFileURL(join(VA_DIR, 'packages/bsv/dist/index.js')).href)) as unknown as VaBsv;
  const merkle = (await import(pathToFileURL(join(VA_DIR, 'packages/merkle/dist/index.js')).href)) as unknown as VaMerkle;
  cache = { bsv, merkle };
  return cache;
}

export interface VaInclusionProof {
  readonly leafHex: string;
  readonly index: number;
  readonly siblingsHex: string[];
  readonly rootHex: string;
}

/** The poker audit trail anchored through the real verifiable-accounting Merkle library. */
export class RealVa {
  /** Merkle-root the per-hand audit records (raw bytes), using the real VA leaf/node hashing. */
  async anchor(records: Uint8Array[]): Promise<string> {
    const { bsv, merkle } = await lib();
    const leaves = records.map((r) => bsv.hashLeaf(r));
    const tree = merkle.buildTree(leaves);
    if (!tree.ok) throw new Error('VA buildTree failed');
    return bsv.HashOps.toDisplayHex(tree.value.root);
  }

  /** A real inclusion proof for record `index` against the anchored root. */
  async prove(records: Uint8Array[], index: number): Promise<VaInclusionProof> {
    const { bsv, merkle } = await lib();
    const leaves = records.map((r) => bsv.hashLeaf(r));
    const proof = merkle.merkleProof(leaves, index);
    const tree = merkle.buildTree(leaves);
    if (!proof.ok || !tree.ok) throw new Error('VA prove failed');
    return {
      leafHex: bsv.HashOps.toDisplayHex(leaves[index]!),
      index: proof.value.index,
      siblingsHex: proof.value.siblings.map((s) => bsv.HashOps.toDisplayHex(s)),
      rootHex: bsv.HashOps.toDisplayHex(tree.value.root),
    };
  }

  /** Verify an inclusion proof against a root, through the real VA verifier. */
  async verify(p: VaInclusionProof): Promise<boolean> {
    const { bsv, merkle } = await lib();
    const dec = (h: string): Hash => {
      const r = bsv.HashOps.fromDisplayHex(h);
      if (!r.ok) throw new Error(`bad hash hex ${h}`);
      return r.value;
    };
    const leaf = dec(p.leafHex);
    const proof = { index: p.index, siblings: p.siblingsHex.map(dec) };
    return merkle.verifyProof(leaf, proof, dec(p.rootHex)).ok;
  }
}

/**
 * The RealVa exposed as the contract the orchestration suite tests, so the SAME `runVAConformance`
 * runs against both the fake and this real adapter (REQ-DEP-003, RT-02 F2). merkleVerify routes
 * through the real `@vaa/merkle` verifier; the path's `right` flags encode the leaf index.
 */
export function realVAContract(): VAContract {
  const va = new RealVa();
  const enc = (s: string): Uint8Array => new TextEncoder().encode(s);
  return {
    boundary: 'verifiable-accounting: audit output is independently checkable, never truth-at-origin',
    async merkleProve(records, index) {
      const p = await va.prove(records.map(enc), index);
      // At level L the leaf is a left child iff bit L of its index is 0 → its sibling is on the right.
      const path = p.siblingsHex.map((hashHex, level) => ({ hashHex, right: ((index >> level) & 1) === 0 }));
      return { root: p.rootHex, leaf: p.leafHex, path };
    },
    async merkleVerify(bundle) {
      let index = 0;
      bundle.path.forEach((step, level) => { if (!step.right) index |= 1 << level; });
      return va.verify({ leafHex: bundle.leaf, index, siblingsHex: bundle.path.map((s) => s.hashHex), rootHex: bundle.root });
    },
  };
}
