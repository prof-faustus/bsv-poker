/**
 * Dependency adapter contracts CT / BS / VA / OB — core §2, §15.8. The platform core depends
 * ONLY on these contracts (REQ-DEP-001); a repo's concrete API is absorbed in its adapter
 * (REQ-DEP-002). Each contract has a fake (./fakes.ts) bound to reality by a single
 * conformance suite run against BOTH the fake and the real adapter (REQ-DEP-003, ./conformance.ts).
 * Security-critical behaviours are tested against the REAL implementations, never fakes
 * (REQ-DEP-004).
 */

// ---- CT: cardtable mental-poker substrate (core §2.1) ----------------------
export interface CTContract {
  /** commit-reveal entropy for shuffle randomness (core §4.1). */
  entropyCommit(secret: Uint8Array): Promise<string>; // commitment hex
  entropyReveal(commitment: string, secret: Uint8Array): Promise<boolean>;
  /**
   * Verifiable distributed shuffle over an N-party set (core §4.2–§4.4). Returns the public
   * shuffle artifacts: the per-card combined public keys and a commitment to the composed
   * order. No single party learns the order (INV-CT-1).
   */
  runShuffle(input: ShuffleInput): Promise<ShuffleResult>;
  /** Conceal a card as (deck_id, card_serial, ciphertext_commitment) (core §4.5). */
  conceal(deckId: string, cardSerial: number, face: number, blind: Uint8Array): Promise<string>; // commitment hex
  /** Verify a reveal opening H(face‖blind)=cmt (core §4.6). */
  verifyReveal(commitment: string, face: number, blind: Uint8Array): Promise<boolean>;
}

export interface ShuffleInput {
  readonly deckId: string;
  /** Canonical party order: lexicographic 33-byte SEC-1 compressed pubkeys (REQ-CRYPTO-003). */
  readonly partyPubKeys: readonly string[]; // hex, already canonically ordered
  /** Per-party revealed entropy r_p (after commit-reveal closes). */
  readonly partyEntropy: readonly Uint8Array[];
  readonly deckSize: number;
}

export interface ShuffleResult {
  /** Commitment to the composed permutation (dispute-replay anchor, core §12.3). */
  readonly orderCommitment: string; // hex
  /** Combined public key per card index (core §4.3 Q_j). */
  readonly combinedKeys: readonly string[]; // hex per card
  /** The combined seed σ = H(r_1‖…‖r_N) (core §4.1). */
  readonly seed: string; // hex
}

// ---- BS: bonded sub-satoshi channel + node (in-tree: adapters/bonded-channel + regtest-node, core §2.2) ----
export interface BSContract {
  /** Local regtest node lifecycle / queries (core §8.4, §10.2). */
  nodeBroadcast(rawTxHex: string): Promise<{ txid: string; status: BroadcastStatus }>;
  nodeOutpointStatus(txid: string, vout: number): Promise<'unspent' | 'spent' | 'unknown'>;
  /** Sub-satoshi channel lifecycle (core §5.7); flag-gated early. */
  channelOpen(params: ChannelParams): Promise<string>; // channel id
  channelTransfer(channelId: string, microAmount: number): Promise<void>;
  /** Whole-satoshi largest-remainder reconciliation Q* (core §2.2). Deterministic (P2). */
  reconcileQstar(microBalances: readonly number[], k: number): number[];
}

export type BroadcastStatus = 'accepted' | 'seen' | 'double-spend-attempted' | 'rejected';

export interface ChannelParams {
  readonly participants: readonly string[]; // pubkeys hex
  readonly granularityK: number;
  /** Fixed one-satoshi anti-cheat bond per participant (INV-BS-2). */
  readonly bondSats: 1;
}

// ---- VA: verifiable-accounting (core §2.3) ---------------------------------
export interface VAContract {
  /** Merkle inclusion against a block header merkleroot (Layer A). */
  merkleProve(records: readonly string[], index: number): Promise<MerkleBundle>;
  merkleVerify(bundle: MerkleBundle): Promise<boolean>;
  /**
   * The stated boundary (INV-VA-2) that MUST be surfaced wherever audit output is shown:
   * establishes inclusion/integrity/selective-disclosure/arithmetic ONLY — never
   * truth-at-origin.
   */
  readonly boundary: string;
}

export interface MerkleBundle {
  readonly root: string; // hex
  readonly leaf: string; // hex
  readonly path: readonly { hashHex: string; right: boolean }[];
}

// ---- OB: overlay-broadcast (core §2.4) -------------------------------------
export interface OBContract {
  /** Authenticated key-wrap (never raw XOR) for a key-graph member (core §2.4). */
  wrap(keyHex: string, memberPubKey: string): Promise<string>;
  unwrap(wrappedHex: string, memberPrivKey: string): Promise<string>;
  /** Revocation = unspent expiring output (INV-OB-2): true iff revoked at `height`. */
  isRevoked(sessionId: string, height: number): Promise<boolean>;
  /** Threshold custody: split into shares; reconstruct only at threshold (core §2.4). */
  thresholdSplit(secretHex: string, t: number, n: number): Promise<string[]>;
}
