# Red-team findings — the peer-to-peer / recovery / multi-network work

**Method.** Adversarial review of the surface that was *added or changed* when the central relay/indexer
were deleted and the system became peer-to-peer (the gossip transport, the local-node browser bridge,
the lobby/discovery, the settlement/key coordinator, the nLockTime recovery, and the network model).
Each finding is written so an individual can pick it up and build the fix: severity, exact location,
the attack, the impact, and a concrete remediation.

**Honesty note.** Several of these are **regressions I introduced** by deleting `relay-go`/`indexer-go`:
the old servers had a CORS allowlist, capability-token admission, and publish rate-limiting; the P2P
replacement currently has none of those. "No central server" was achieved, but it **moved trust to an
open mesh + a permissive local bridge** without re-adding the mitigations the servers provided. The
unchanged deterministic core (mental-poker shuffle, Script interpreter, BIP-143 sighash) was **not**
re-audited in this pass — these findings are about the new/changed surface only.

Severity: **CRITICAL** (funds loss / remote takeover), **HIGH** (funds stuck / forgery / strong DoS),
**MEDIUM** (DoS / griefing / weakened control), **LOW** (hygiene / honesty / unproven claim).

---

## CRITICAL

### RT-01 — Local node bridge allows ANY website to drive the player's node (CORS `*`, no Origin/Host check)
- **Where:** `tools/local-node.ts` — `res.setHeader('access-control-allow-origin', '*')` (and the
  capability endpoint returns a token for anyone; no `Origin`/`Host` validation).
- **Attack:** the local node binds `127.0.0.1:8090` and answers cross-origin to `*`. Any web page the
  user visits (or a DNS-rebinding attacker resolving a name to `127.0.0.1`) can POST `/tables`,
  `/tables/{id}/publish`, GET `/tables`, and open the SSE stream — i.e. **drive the player's node**:
  create/poison tables, publish frames into live games, enumerate the gossiped directory, and pump the
  mesh through the victim.
- **Impact:** remote, unprivileged control of a participant's node from a random browser tab. This is a
  regression — `relay-go` shipped a CORS allowlist (loopback + the desktop vhost).
- **Fix:** drop the wildcard. Allow only the desktop/web app origin (the WebView2 vhost / the served
  client origin); **reject requests whose `Host` is not `127.0.0.1[:port]` and whose `Origin` is not the
  allowlisted app** (defeats DNS-rebinding). Optionally bind the bridge to a random per-launch port +
  per-launch bearer token injected into the page via `__BSV_RUNTIME`.

### RT-02 — Settlement / key-exchange coordinator accepts UNAUTHENTICATED frames (forgery + griefing)
- **Where:** `tools/settlement-coordinator.ts` — `coSignSettlement` collects `e.sig` with
  `sigs.set(e.idx, ...)` **without verifying** the signature is valid for seat `idx`'s key (line ~95);
  `gatherByIndex` likewise collects `oc-pub`/`bond`/`escrow` values by `idx` with no authentication.
- **Attack (settlement griefing):** any peer on the open mesh injects a `settle-sig` frame for an `idx`
  before the honest one arrives (`!sigs.has(e.idx)` takes the first). The assembled N-of-N tx then
  carries a bogus signature and the on-chain submit fails — the honest submitter cannot settle.
- **Attack (key substitution):** an attacker injects a fake `oc-pub` for a seat. The funding script is
  built from the gathered pubs, so the escrow locks to a key nobody (or the attacker) controls — funds
  bricked or redirectable; the recovery built on the same poisoned script is also wrong.
- **Impact:** denial of settlement (forces everyone onto the slow recovery path) and, via key
  substitution, potential fund lock/redirect.
- **Fix:** authenticate every coordinator frame with the seat's **Ed25519 session key** (the same key
  the seating/seat-key layer already established) and verify before collecting. For `settle-sig`,
  additionally verify the **ECDSA** signature against the seat's registered on-chain pubkey over the
  exact settlement sighash before accepting it into the set. Bind `oc-pub` announcements to the seat's
  session-key signature so on-chain keys cannot be substituted.

### RT-03 — Pre-signed recovery has a FIXED fee → can become unconfirmable → "no sat ever lost" fails on mainnet
- **Where:** `packages/tx-builder/src/fallback.ts` (`buildTimeoutRefund`/`presignNlocktimeRecovery`,
  `fee` fixed at signing time); wired in `tools/bot-daemon.ts` with `fee: SETTLE_FEE`.
- **Attack/condition:** the recovery is signed now with a fee fixed now, but it is broadcastable only
  far in the future (after `nLockTime`). If the fee market rises before then, the recovery may be too
  cheap to relay/confirm — and it cannot be re-fee'd because it is already signed N-of-N and the
  counterparties may be gone (the whole point of recovery is that they vanished).
- **Impact:** funds can be **stuck** exactly in the failure scenario the recovery exists for. This
  directly undercuts the life-critical "no sat ever lost" guarantee on a real fee market. (Also: the
  default `fee: 0` returns a non-relayable tx; the "100% recovery" claim is really "100% minus an
  unavoidable, fixed, possibly-insufficient fee".)
- **Fix:** make the recovery fee-bumpable without a counterparty: add a **CPFP anchor output** the
  recovering party can spend to child-pay-for-parent at broadcast time, or pre-sign a small family of
  recoveries across a fee schedule, or use an anchor/ephemeral-anchor pattern. Document the chosen
  fee-bumping path and test it (broadcast under a raised fee floor).

### RT-04 — Selected network is NOT bound to the node it talks to (wrong-chain footgun)
- **Where:** `tools/bot-daemon.ts` — `--network` flows into the readiness gate, but the node is
  `new RealBsvNode('127.0.0.1', NODE_PORT)` regardless of the selected network; nothing checks the
  node's actual chain.
- **Attack/condition:** select `mainnet` (pass the ack) while `NODE_PORT` points at a regtest/testnet
  node — or vice versa. The gate says "ready, real funds"; the value path then signs/broadcasts against
  the wrong chain. There is no verification that the node's network magic / genesis matches the tag.
- **Impact:** real-funds operations can be performed against the wrong chain (or test flows believed to
  be safe actually hit mainnet). The "same model, network is just a tag" property becomes dangerous
  without a binding check.
- **Fix:** before any value op, query the node's network identity (genesis hash / network magic / chain
  info) and **assert it equals the selected `Network`**; refuse otherwise. Tie the node endpoint to the
  network selection (per-network default endpoints), not a free `--node` port.

### RT-05 — Live recovery wiring refunds 100% to seat 0 only (breaks multi-funder games)
- **Where:** `tools/bot-daemon.ts` — `recoveryContribs = [{ pub: pubs[0]!, amount: escrow }]`.
- **Attack/condition:** the sim has seat 0 fund the whole escrow, so this is "correct" there. But the
  primitive supports N contributors and a real game has **each seat fund its own buy-in**. As wired, the
  recovery routes the **entire** escrow back to seat 0 — i.e. on recovery, every other seat's stake is
  paid to seat 0.
- **Impact:** in any real per-buy-in deployment this is theft-by-recovery; it also means non-funders'
  "recovery" returns them nothing.
- **Fix:** build `recoveryContribs` from the **actual per-seat contributions** (each seat's own on-chain
  pub + its buy-in), so the recovery returns each player their own funds. Add a multi-funder recovery
  e2e (each seat funds, game stalls, each recovers exactly their stake).

---

## HIGH

### RT-06 — No rate-limiting / no max-connections / binds all interfaces → mesh DoS + internet exposure
- **Where:** `packages/adapters/src/p2p-transport.ts` — `bindHost` defaults to `'0.0.0.0'`; `peers` is
  an unbounded `Set`; `flood`/`publish` have no per-peer or per-topic rate limit. (`relay-go` had a
  50/s sustained / 100 burst per-table limit; that mitigation is gone.)
- **Attack:** any internet host (0.0.0.0 bind) opens many TCP connections and floods 1 MiB frames; every
  honest node re-floods to all peers. Memory is bounded (dedup `MAX_SEEN`) but **bandwidth, CPU, and
  socket count are not**; there is no peer scoring or ban.
- **Impact:** a single malicious peer can saturate/partition the mesh; connection exhaustion on every
  node.
- **Fix:** per-peer + per-topic token-bucket rate limits; a max-connections cap; peer scoring with
  disconnect/ban on abuse; default the P2P listener to **loopback** with an explicit `--public` opt-in
  (the cross-machine bind should be a deliberate choice, mirroring `resolveBindHost`/REQ-APP-106).

### RT-07 — Directory/presence eviction DoS (hide real tables)
- **Where:** `packages/adapters/src/p2p-transport.ts` — `onDirectoryAnnounce`/`onPresenceAnnounce`:
  `if (!map.has(id) && map.size >= MAX_DIRECTORY) return;` (drops NEW entries when full).
- **Attack:** an attacker floods `MAX_DIRECTORY` (10 000) fake table/presence announces; once full,
  **new real announces are dropped**, so honest tables/players cannot be discovered.
- **Impact:** discovery denial-of-service (no central directory to fall back on).
- **Fix:** per-peer announce quotas; signed announces (bind to a session key) with an LRU keyed by
  trust; separate pools so one peer cannot evict others; optionally a small proof-of-work on announce.

### RT-08 — "Gated tables" are not actually gated (admission is advisory)
- **Where:** `packages/adapters/src/p2p-transport.ts` — `setAdmission`/`createTable(admission)` store the
  secret but **never enforce** it; over P2P there is no gatekeeper. The API still advertises gating.
- **Attack:** Sybil identities (fresh keypairs are free) join any table's waiting room; nothing checks an
  admission secret. The commit-reveal seating is non-grindable for a *fixed* set, but the set is
  open-join — an attacker can flood joins to grab/deny seats or bias the participant set.
- **Impact:** access control advertised but absent; seat-grabbing / waiting-room flooding.
- **Fix:** make admission real over P2P — derive the table topic/id from `H(admission)` so only holders
  can find/publish to it, OR require a signed admission proof checked at the seating layer; rate-limit
  joins per identity.

### RT-09 — Reconnect-from-peer trusts the providing peer (forged-but-legal history)
- **Where:** `tools/reconnect-e2e.ts` `persistChannel` records frames **without** authentication;
  `packages/app-services/src/transcript.ts` `rebuildHand` validates commit/reveal consistency +
  legality, **not** seat-key signatures (it assumes records were authenticated upstream — see its own
  note at line ~122).
- **Attack:** a malicious peer serves a reconnecting client a transcript containing forged frames that
  are structurally legal; `rebuildHand` accepts it and the client rebuilds a fabricated state.
- **Impact:** a reconnecting/observing client can be fed a false history by any single peer.
- **Fix:** the reconnecting client must **re-verify every record's Ed25519 seat-key signature** (exactly
  as the live `InteractiveNetworkedTableClient.subscribe` path does) before feeding records to
  `rebuildHand`; reject unsigned/forged records.

### RT-10 — Non-funders don't validate the recovery locktime they're asked to sign
- **Where:** `tools/bot-daemon.ts` — non-seat-0 receives `escrow-pre = "{txid}:{recoverableAtHeight}"`
  from seat 0 and signs the recovery for that height without bounds-checking it.
- **Attack:** a malicious funder broadcasts an absurd far-future `recoverableAtHeight`; everyone signs a
  recovery that cannot be broadcast for, e.g., years, while the escrow is locked.
- **Impact:** the close-condition is technically present but useless (funds locked far longer than any
  game), undermining "no sat ever lost".
- **Fix:** each seat independently validates `recoverableAtHeight ∈ [tip + MIN_Δ, tip + MAX_Δ]` (a
  sane window) and refuses to sign otherwise; derive the height from each node's own tip rather than
  trusting the funder's value.

---

## MEDIUM / LOW

### RT-11 — Reserved gossip namespace is forgeable; comment says NUL but code uses a space (LOW)
- **Where:** `packages/adapters/src/p2p-transport.ts` — `DIR_TOPIC = ' bsvp/dir'` etc. (space prefix),
  while the comment claims a NUL prefix "keeps them disjoint." A crafted frame with `t = ' bsvp/dir'`
  can inject directory/presence traffic, and the namespace isn't structurally unforgeable.
- **Fix:** separate the control plane from table ids with a real frame-type field (not a magic string),
  or a length-prefixed/reserved control character; correct the comment.

### RT-12 — Local node reports fake liveness/membership (LOW, honesty)
- **Where:** `tools/local-node.ts` `/healthz` always returns `{ok:true}`; `P2PTransport.createTable`
  hardcodes `members: 1`.
- **Impact:** the UI can show "connected" / a member count that doesn't reflect actual mesh
  connectivity (zero peers looks healthy).
- **Fix:** `/healthz` should reflect `peerCount()`/mesh state; report a real (best-effort) member count.

### RT-13 — "Same model on testnet/mainnet" is proven as *no-branch*, NOT as *works on a real node* (LOW→MEDIUM, unproven claim)
- **Where:** `tools/network-uniform-e2e.ts` compares byte-identity across network **tags** that are
  never fed into the model (so the equality is near-tautological), and runs only against the in-tree
  `RegtestNode`, which **documents** that it disables coinbase maturity and uses wall-clock locktimes.
- **Impact:** the test proves the model doesn't branch on network — good — but it does **not** prove
  testnet/mainnet actually work (real fee floors, coinbase maturity = 100 blocks, address/network
  params, mempool policy). The README/commit wording "works on real BSV" is stronger than what is
  tested.
- **Fix:** add an integration test against a real testnet node (or a node sim that enforces maturity +
  a fee floor), and relabel the current proof precisely as "network-independent (no branch)".

### RT-14 — Mainnet ack is an accident-guard, not a control against a malicious local operator (LOW, note)
- **Where:** `packages/app-services/src/network-gate.ts` — `MAINNET_ACK_TOKEN` is a constant string.
- **Note:** this is by design (it prevents *accidental* mainnet, not a hostile local process). Document
  it as such so it is not mistaken for a security boundary.

---

## Suggested build order (for the individual process)

1. **RT-01** (local-node CORS/Origin/Host) — smallest change, closes remote takeover.
2. **RT-02** (authenticate the coordinator) — reuse the existing Ed25519 seat keys; closes forgery/grief.
3. **RT-04 + RT-05 + RT-03** (network↔node binding, multi-funder recovery, fee-bumping) — the funds-safety
   trio; do them together with a multi-funder, fee-bumped, wrong-chain-refused recovery e2e.
4. **RT-06 + RT-07 + RT-08** (rate-limit, anti-eviction, real admission) — restore the mesh DoS/admission
   mitigations the deleted servers had.
5. **RT-09 + RT-10** (reconnect re-verification, locktime bounds) — close the remaining trust gaps.
6. **RT-11..RT-14** (hygiene + honest claims + real-network integration test).

Every fix should land with a positive **and** a hostile-negative test (the repo standard), and the
relevant e2e/CI stage updated.
