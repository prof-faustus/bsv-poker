# Network protocol

> **⚠ Peer-to-peer update.** bsv-poker is now **fully peer-to-peer** — there is **NO central relay or
> indexer server** (`apps/relay-go` and `apps/indexer-go` are deleted). Where this document still says
> "relay"/"indexer", read it as the gossip transport (`packages/adapters/src/p2p-transport.ts`) and the
> player's own local node (`tools/local-node.ts`): the SAME signed envelope protocol carried directly
> between peers, with discovery as a gossiped directory. The same model runs on regtest, testnet, and
> real BSV. Authoritative description: [`P2P_MODEL.md`](./P2P_MODEL.md).

The relay envelope protocol: every table message is a formal, signed object. No implicit protocol, no
undocumented field. Source: `packages/app-services/src/{session-auth,message-validation,
interactive-client,lobby}.ts`, `apps/relay-go`.

## Transport

The relay (`apps/relay-go`) is **transport + index only**, never the source of truth. It offers:
presence (Tier A), a table directory, capability minting, and a per-table opaque SSE fan-out (Tier B).
It treats every published object as opaque bytes. Publish/subscribe require a **capability token**
(see below) and fail closed.

## Capability tokens (admission)

A table-scoped, expiring, scope-limited HMAC token (`relay-go/capability.go`):
`HMAC-SHA256(serverSecret, "v1|tableId|scope|exp")`, scope ∈ {`pub`,`sub`,`pubsub`}. Minted by
`POST /tables` (creator) or `POST /tables/{id}/capability` (a gated table requires the admission
secret). Verified on publish/subscribe with a constant-time MAC compare; table-scope, expiry, and
scope are all checked. Rotating the server secret revokes all tokens.

## Signed envelopes

Every join / commit / reveal / action is **Ed25519-signed** by the seat's session key. The signed
message is canonical (`session-auth.ts` `envelopeMessage`):

```
JSON.stringify([tableId, t, seat, hand, kind??'', amount??0, c??'', r??'', discard??[], prev??''])
```

This is byte-exact across clients (and the Go indexer re-derives it identically; pinned by
`INV-IXV-0`). Receivers verify the signature against the public key **registered to the acting seat**.

### Envelope object

| field | type | meaning | who sets / signs | rejection |
|---|---|---|---|---|
| `t` | `'commit'`\|`'reveal'`\|`'action'` | message type | the acting seat | unknown type → reject |
| `seat` | int ≥ 0 | the acting seat | the acting seat | non-int / negative → reject |
| `hand` | int ≥ 0 | hand index | the acting seat | non-int / negative → reject |
| `c` | hex | commit: `SHA-256(entropy)` | commit only | non-hex → reject |
| `r` | hex | reveal: entropy | reveal only | non-hex → reject |
| `kind` | string | action kind | action only | empty → reject |
| `amount` | number | action wager | action (optional) | non-finite → reject |
| `discard` | int[] | draw discard slots | action (optional) | non-int member → reject |
| `prev` | hex | prior **state hash** bound into the action | action | (validating indexer: required) |
| `sig` | hex | Ed25519 over `envelopeMessage` | the acting seat | bad/absent → reject |

Structural validation: `message-validation.ts` `validateEnvelope` (returns the typed envelope or
`null` — never partial trust). Inbound frames are **bounded-parsed** (`safeJsonParse`) before
validation. `prev` binds an action to the prior transcript position so a signed action cannot be
replayed elsewhere (audit-02 #8).

## States in which an envelope is valid

`commit` then `reveal` in the handshake phase; `action` only when the engine has the seat on the
clock. The engine (`STATE_MACHINE.md`) is the authority on action legality; the relay/indexer never
adjudicate game rules.

## Dual path

An action goes to peers via the relay (speed) AND to the indexer as a transcript record (rebuild). In
the indexer's validating mode the record is authenticated before storage (`apps/indexer-go`,
`INV-IXV-*`). The relay channel is best-effort; the client reconciles from the transcript.

## Timeout-claim envelope (accountable drop — audit 3)

A `timeout-claim` envelope drops a non-responding seat at an anchored block-height deadline. It is
signed BY the claimant ABOUT a `subject` seat and binds the anchored deadline `d`; `reveal`/`action`
envelopes carry the anchored height `h` they were emitted at (the floor a deadline must clear).

| field | type | meaning | rejection |
|---|---|---|---|
| `t` | `'timeout-claim'` | drop a seat | — |
| `seat` | int ≥ 0 | the CLAIMANT (signer) | must not equal `subject` (self-claim rejected) |
| `subject` | int ≥ 0 | the seat being dropped | missing / `=== seat` → reject |
| `hand` | int ≥ 0 | hand index | — |
| `d` | int ≥ 0 | anchored deadline block height | missing / negative / non-integer → reject; `d < floor+window` (premature) → ignored |
| `sig` | hex | Ed25519 over `envelopeMessage` by the claimant | verified against `seatPubs[seat]`; bad → reject |

A claim takes effect only once the shared chain height passes `d` and `d` clears the agreed floor, so
every honest client applies the identical drop and converges (`timeout-claim.test.ts`). Both the
betting/draw ACTION phase (engine check-or-fold default) and the commit/reveal HANDSHAKE phase
(drop + re-derive the deck among survivors) use it. Structural validation: `message-validation.ts`.
