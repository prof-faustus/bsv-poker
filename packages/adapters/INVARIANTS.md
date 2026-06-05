# `@bsv-poker/adapters` — Invariants

Run: `node --test "packages/adapters/test/**/*.test.ts"`

## Conformance (fakes must pass the same suite as the real adapters) — `conformance.test.ts`

| ID | Claim | Proof (test) |
|---|---|---|
| INV-ADP-1 | The CT fake passes the CT conformance suite. | `CT fake passes the CT conformance suite` |
| INV-ADP-2 | The BS fake passes the BS conformance suite. | `BS fake passes the BS conformance suite` |
| INV-ADP-3 | The VA fake passes the VA conformance suite. | `VA fake passes the VA conformance suite` |
| INV-ADP-4 | The OB fake passes the OB conformance suite. | `OB fake passes the OB conformance suite` |

The real CT adapter passes the SAME CT suite (`crypto-mentalpoker` INV-MP-1), so a green fake run and a
green real run certify the same contract.

## Architecture / seams — `architecture.test.ts`, `seams.test.ts`

| ID | Claim | Proof |
|---|---|---|
| INV-ADP-5 | The contract seams are honoured (no security-critical path is wired to a fake). | `architecture.test.ts`, `seams.test.ts` |
| INV-ADP-6 | Fake commit/reveal is constant-time and uses real hashing (genuinely conformant). | `fakes.ts` uses `constantTimeEqualHex` (shared `INV-CT-2`) |

## Real node client (boundary)

| ID | Claim | Proof |
|---|---|---|
| INV-ADP-7 | The node response is bounded-parsed and the socket buffer capped (CWE-400). | `real-node.ts` (`safeJsonParse` + `MAX_NODE_FRAME`); exercised by the on-chain e2es |

## In-tree regtest node (standalone) — `regtest-node.test.ts`

The project's OWN BSV regtest node — no external process. Runs the real interpreter + BIP-143 sighash
over the hardened parsers and enforces the consensus rules the on-chain layer relies on.

| ID | Claim | Proof (test) |
|---|---|---|
| INV-NODE-1 | A P2PKH coinbase spend is accepted; a wrong key fails IN-SCRIPT. | `INV-NODE-1: P2PKH coinbase spend accepted; wrong key rejected in-script` |
| INV-NODE-2 | **nLockTime finality** is enforced: a non-final tx with a future locktime is rejected before maturity, accepted at maturity (the bond-forfeiture maturity gate). | `INV-NODE-2: nLockTime finality gate enforced (premature rejected, accepted at maturity)` |
| INV-NODE-3 | A final-sequence input (0xffffffff) makes a future-locktime tx admissible. | `INV-NODE-3: a final-sequence input makes a future-locktime tx admissible` |
| INV-NODE-4 | Sequence replacement: a strictly-higher-sequence tx replaces a conflicting one; equal/lower is rejected. | `INV-NODE-4: higher-sequence tx replaces a conflicting mempool tx` |
| INV-NODE-5 | An unknown UTXO and a value-creating tx are rejected; hostile hex never throws. | `INV-NODE-5: unknown UTXO + value creation rejected; hostile hex never throws` |

End-to-end: `tools/onchain-forfeit-e2e.ts` proves the full bond reveal-or-forfeit on this node,
including the premature-FORFEIT rejection by the finality gate.

## In-tree bonded sub-satoshi channel (standalone) — `bonded-channel.test.ts`

The project's OWN bonded micro-payment channel (BS contract) — no external process. Settles the
cooperative close ON-CHAIN through the in-tree node.

| ID | Claim | Proof (test) |
|---|---|---|
| INV-BS-1 | Q* cooperative close pays WHOLE satoshis that conserve funded + bonds (no fractional output); property-proven over 5000 random splits. | `INV-BS-1: cooperative close pays whole satoshis…`, `INV-BS-1 property: Q* apportionment sums exactly to the funded total` |
| INV-BS-2 | A contested close forfeits exactly the 1-satoshi bond (bounded at-risk capital). | `INV-BS-2: contested close forfeits exactly the 1-sat bond` |
| INV-BS-3 | An overdraw transfer is rejected; transfers conserve the sub-unit supply. | `INV-BS-3: overdraw rejected; transfers conserve the sub-unit supply` |
| INV-BS-4 | The cooperative close settles ON-CHAIN through the in-tree node (real close tx accepted). | `INV-BS-4: close settles on-chain through the in-tree node` |

End-to-end: `tools/microbet-e2e.ts` (sub-satoshi transfers → whole-satoshi Q* close on the in-tree
node → 1-sat bond forfeiture), standalone.

## To extend

A new adapter adds a conformance test that BOTH the fake and the real implementation must pass, and
(for any network adapter) a bounded-input guard with a negative test.
