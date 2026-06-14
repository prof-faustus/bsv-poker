# Formal security analysis — the BSV dealerless mental-poker deal

This is a standalone, self-contained security argument for the card-dealing protocol implemented in
`MentalPokerEC`, `RevealProof`, `SeatOrder`, and `NetGame`. It states the construction precisely, the
hardness assumptions, the security definitions, and theorems with proofs. Each claim is mapped to the code
and to an executable test at the end. **Honest scope is stated explicitly** — what is proven and what is
deliberately out of model.

The intended adversary is strong: a static adversary that may corrupt **all but one** of the players at a
table (an `N−1` collusion), observes the entire public transcript across all hands, and chooses its own keys
and messages adaptively, subject only to the stated cryptographic assumptions.

---

## 1. The construction

Let `𝔾` be the secp256k1 group of prime order `q` with generator `G`; `O` is the identity. There are `n`
cards; card `i ∈ {0,…,n−1}` is the fixed public point `Mᵢ = (i+1)·G` (distinct and non-identity, since
`1 ≤ i+1 < q`). `H` is SHA-256, modelled as a random oracle. Players are `P₁,…,P_N`.

**Phase 0 — base deck.** `deck⁰ = (M₀,…,M_{n−1})` (`MentalPokerEC.BaseDeck`).

**Phase 1 — shuffle.** Each `Pₚ` in turn samples a global scalar `cₚ ←$ ℤ_q^*` and a permutation `πₚ`, and
outputs `deckₚ[k] = cₚ · deck_{p−1}[πₚ(k)]` (`MentalPokerEC.ShuffleMask`). After all players,
`deck_N[k] = (∏ₚ cₚ) · M_{σ(k)}` for the composed secret permutation `σ = π_N∘⋯∘π₁`.

**Phase 2 — remask.** Each `Pₚ` in turn removes its global scalar and applies fresh independent per-card
scalars `d_{p,k} ←$ ℤ_q^*`: output`[k] = d_{p,k} · (cₚ⁻¹ · input[k])` (`MentalPokerEC.Remask`). After all
players the deck is `Q_k = (∏ₚ d_{p,k}) · M_{σ(k)}`, broadcast publicly.

**Phase 2c — commitments.** For each position `k` in the reveal set `R` (every seat's hole positions and the
≤5 board positions), each `Pₚ` publishes a **hiding** commitment

```
Com_{p,k} = H( dom ‖ d_{p,k} ‖ r_{p,k} ),   r_{p,k} ←$ {0,1}²⁵⁶      (RevealProof.Commit)
```

broadcast at remask time, before any card is opened.

**Phase 3 — deal.** To deal position `k` to recipient `T`, every `Pₚ` (`p ≠ T`) discloses `(d_{p,k},
r_{p,k})`. `T` checks each opens `Com_{p,k}` (`RevealProof.Verify`), strips all of them and its own `d_{T,k}`
from `Q_k` (`MentalPokerEC.Unmask`), obtaining `M_{σ(k)}`, and identifies the card (`MentalPokerEC.Identify`).
A **board** card is dealt by *all* players disclosing `(d_{p,k}, r_{p,k})`. At **showdown**, each non-folded
player discloses its own hole scalars `(d_{p,k}, r_{p,k})`.

**Authentication.** Every protocol message is signed (ECDSA over secp256k1, low-S, FORKID) over `table-id ‖
canonical(content)` and bound to the sender's seat; a peer can act/reveal only for the seat its key holds
(`NetGame.Verify`, `SeatBound`).

**Seat order.** Before the first hand the seated players run a commit-reveal: each commits `H(dom ‖ νᵢ)` for a
random nonce `νᵢ`, then reveals `νᵢ`; the joint seed `s = H(dom ‖ sorted{(pubᵢ,νᵢ)})` orders the seats by
`H(s ‖ pubᵢ)` (`SeatOrder`, driven by `NetGame.TryAssignSeats`).

---

## 2. Assumptions

- **(DL)** Discrete logarithm is hard in `𝔾`.
- **(CR)** `H` is collision-resistant.
- **(RO)** `H` behaves as a random oracle (used for the hiding/joint-randomness arguments).
- **(EUF-CMA)** ECDSA over secp256k1 is existentially unforgeable under chosen-message attack.

Card secrecy (Thm 2) needs **no computational assumption beyond (RO)** — it is statistical. (DL)/(CR)/(EUF-CMA)
are used for binding, shuffle integrity, seat fairness, and authentication.

---

## 3. Security definitions

- **Correctness.** With all players honest, every dealt position decodes to a unique card and the deck is a
  permutation of the `n` cards.
- **Card secrecy.** For an honest player `T` and a position whose own scalar `d_{T,k}` is never disclosed, the
  adversary's view is (statistically) independent of the card `σ(k)`.
- **Reveal binding (no substitution).** No PPT player can open a commitment to a scalar other than the one
  committed; hence the card it contributes at deal/showdown is exactly the committed card.
- **Shuffle soundness.** No card is added, dropped, or duplicated; a dishonest shuffle/remask is detected.
- **Seat fairness.** No player can bias its own seat; with ≥1 honest nonce the order is uniform.
- **Transcript authenticity.** No player can forge another player's message or act for a seat it does not hold.

---

## 4. Theorems and proofs

### Theorem 1 (Correctness)
*If all players follow the protocol, every dealt position `k` decodes to the unique card `σ(k)`, and `σ` is a
uniformly random permutation provided at least one honest player samples `(cₚ, πₚ)` uniformly and independently.*

**Proof.** Scalar multiplication commutes (`a·(b·P) = b·(a·P)`) and each nonzero scalar is invertible
(`s⁻¹·(s·P) = P`). `Unmask` removes all `N` per-card scalars from `Q_k`, leaving `M_{σ(k)}`, which `Identify`
matches to the unique base point `σ(k)` (base points are distinct). Multiset preservation: a nonzero scalar is
a bijection on `𝔾`, and a permutation reorders, so each phase preserves the card multiset; hence the final
deck is a permutation of `{M₀,…,M_{n−1}}`. Uniformity: `σ` is a composition of permutations; composing any
permutation with one uniform, independent permutation yields a uniform permutation. ∎

### Theorem 2 (Card secrecy — the core privacy property)
*Let `T` be honest and let position `k` be one whose scalar `d_{T,k}` is never disclosed (e.g. `T` folds, or
pre-showdown). Against an adversary `A` corrupting all other `N−1` players, the view of `A` is independent of
`σ(k)` up to statistical distance `q_H / 2²⁵⁶`, where `q_H` is the number of random-oracle queries.*

**Proof.** `A`'s view that could depend on position `k` is:
1. the public deck value `Q_k = (∏ₚ d_{p,k}) · M_{σ(k)}`;
2. the scalars `{(d_{p,k}, r_{p,k})}_{p≠T}` — `A` controls these players, so it knows them;
3. `T`'s published commitment `Com_{T,k} = H(dom ‖ d_{T,k} ‖ r_{T,k})`.

From (1) and (2), `A` can strip the masks it knows and obtain
`X = d_{T,k} · M_{σ(k)} = d_{T,k}·(σ(k)+1)·G`. Because `d_{T,k} ←$ ℤ_q^*` is uniform and secret and `(σ(k)+1)`
is a fixed non-zero scalar, the product `d_{T,k}·(σ(k)+1) mod q` is uniform over `ℤ_q^*`; therefore `X` is
uniform over `𝔾∖{O}` **independently of `σ(k)`**. So (1)+(2) carry no information about the card.

For (3): `r_{T,k}` is uniform with 256 bits of entropy and never disclosed for this position. In the random
oracle, `Com_{T,k}` is a fresh uniform value unless `A` queries `H` at the exact point `dom ‖ d_{T,k} ‖ r_{T,k}`;
that happens with probability ≤ `q_H/2²⁵⁶`. Conditioned on no such query, `Com_{T,k}` is independent of both
`d_{T,k}` and `σ(k)`.

The per-card scalars are independent across positions (`NewPerCardScalars`) and across hands (regenerated each
hand), so other revealed scalars/commitments are independent of `d_{T,k}`. Combining, `A`'s view is
independent of `σ(k)` up to `q_H/2²⁵⁶`. ∎

**Remark (why the commitment must be hiding).** With the earlier commitment `Com_{T,k} = d_{T,k}·G`, step (3)
breaks the proof: `A` computes `(j+1)·Com_{T,k} = d_{T,k}·(j+1)·G` and tests equality with `X` for every
candidate `j`, matching at `j = σ(k)` and reading the card. The hiding commitment removes exactly this curve
relation. This is the leak that this analysis surfaced and the implementation fixed; it is demonstrated in
`RevealProofTests` ("HIDING — read-the-hidden-card attack: works against a plain d·G commitment, defeated by
the hiding one").

### Theorem 3 (Reveal binding — no card substitution)
*Under (CR), no PPT player opens its commitment to a scalar different from the one committed; in particular a
forged reveal that decodes to a different valid card is rejected.*

**Proof.** Opening `Com_{p,k}` to `(d', r') ≠ (d_{p,k}, r_{p,k})` with `H(dom‖d'‖r') = H(dom‖d_{p,k}‖r_{p,k})`
is a collision in `H`, which occurs with negligible probability under (CR). The substitution scalar
`d' = d·(m+1)·(m'+1)⁻¹` (which makes `P = d·M_m` decode to `M_{m'}`) satisfies `d' ≠ d` whenever `m ≠ m'`, so
it cannot open the commitment. The nonce `r_{p,k}` was fixed at remask, before the player learned its card, so
it cannot be chosen to assist a substitution. ∎

### Theorem 4 (Shuffle soundness)
*No card is added, dropped, or duplicated, and a dishonest shuffle/remask is detected.*

**Proof.** `ValidateDeck` rejects any deck containing an off-curve point or a repeated point, and
`ValidatePermutation` rejects any non-bijective permutation; both run on every shuffle/remask input
(`MentalPokerEC`). Since masking by a non-zero scalar is injective on `𝔾` and a valid permutation reorders, the
card multiset is invariant at every step. `ShuffleProof` recomputes the masked output from the revealed
`(global, permutation, per-card)` secrets and compares against the committed output, so any deviation is
detected upon opening. ∎

**Honest scope.** `ShuffleProof` is **commit-and-open**, not zero-knowledge: the shuffle transform is revealed
at dispute/showdown, so it provides *detection with certainty*, not a ZK argument. Card secrecy (Thm 2) does
**not** rely on the shuffle staying hidden — dealt cards are masked by the independent per-card scalars, and
only the per-card scalars for *revealed* positions are ever opened.

### Theorem 5 (Seat fairness)
*If at least one seated player samples its nonce `νᵢ` uniformly, independently, and keeps it secret until all
commitments are published, then the seat order is a uniformly random ordering (in RO) and no player can bias
its own seat.*

**Proof.** Each nonce is bound by `H(dom‖νᵢ)` before any is revealed; by (CR) a player cannot change `νᵢ`
after seeing others (no adaptive choice). The joint seed `s = H(dom ‖ sorted{(pubᵢ,νᵢ)})` includes the honest
`νₕ`; in the RO, `s` is uniform and unpredictable to every player until `νₕ` is revealed, regardless of the
other (possibly adversarial) nonces. The order sorts seats by `H(s‖pubᵢ)`, a uniform random ordering in the
RO. A player cannot bias its position by key choice (the order depends on the unpredictable `s`) nor by nonce
choice (one honest nonce already randomises `s`). A revealed nonce that does not open its commitment is
rejected. ∎

### Theorem 6 (Transcript authenticity)
*Under (EUF-CMA), no PPT player forges a protocol message attributed to another player, nor acts/reveals for a
seat it does not hold.*

**Proof.** Every message carries an ECDSA signature over `table-id ‖ canonical(content)` verified against the
claimed identity key, and seat-bound operations require the signer's key to equal the key seated at the claimed
seat (`SeatBound`). A message accepted for a key whose secret the adversary does not hold is an ECDSA forgery,
which is negligible under (EUF-CMA). Replays are dropped by frame-id dedup and hand-number tagging. ∎

---

## 5. Out of model (stated honestly, not hidden)

- **Side-channels.** The proofs are in the computational model. The secp256k1 scalar multiplication is
  variable-time `BigInteger` code and is **not** constant-time; timing/cache side-channels are out of scope.
  Constant-time hardening is future work and does not affect any theorem above.
- **On-chain consensus acceptance.** Settlement/escrow/recovery transactions are built and strictly verified by
  an internal Script interpreter (a documented subset), not by a full BSV consensus node. Consensus acceptance
  is validated separately (`SPV.md`, `RED_TEST.md`); it is orthogonal to the card-dealing security here.
- **Liveness vs. punishment.** A withholding peer triggers an *accountable abort* → unilateral nLockTime /
  2-of-2 escrow recovery, so **no honest player loses funds**. On-chain economic *penalty* for a griefer
  (stake slashing) is **not** claimed.
- **Legacy `LiveDeal`.** The two-party `LiveDeal` demo reveals the full transform at showdown and does not use
  `RevealProof`; it is superseded by `NetGame` and is **out of scope** of this analysis (it should be removed
  or gated; the production path is `NetGame`).
- **Folded-card privacy.** In `NetGame` a folded player never discloses its hole scalars (showdown reveals only
  non-folded seats), so folded cards remain hidden by Theorem 2.

---

## 6. Code and test mapping

| Claim | Code | Test |
|-------|------|------|
| Correctness, valid permutation | `MentalPokerEC` (`ShuffleMask`/`Remask`/`Unmask`/`Identify`/`ValidateDeck`) | `MentalPokerECTests` (full 52-card deal, all distinct) |
| Card secrecy (Thm 2) | per-card masking + hiding `RevealProof` | `MentalPokerECTests` (privacy, multiway) + `RevealProofTests` (hiding defeats the read-the-card attack) |
| Reveal binding (Thm 3) | `RevealProof.Commit/Verify`, `NetGame.MergeShares` | `RevealProofTests` (substitution rejected); `NetGame` `CheatDetected` |
| Shuffle soundness (Thm 4) | `ValidateDeck`/`ValidatePermutation`, `ShuffleProof` | `ShuffleProofTests` (substituted / dropped / lied-permutation detected) |
| Seat fairness (Thm 5) | `SeatOrder`, `NetGame.TryAssignSeats` | `SeatOrderTests`; `NetGameJoinTests` (peers agree on the fair order) |
| Transcript authenticity (Thm 6) | `NetGame.Verify`/`SeatBound` | `NetGameTests` (forged/unsigned/wrong-seat rejected) |
| End-to-end | `NetGame` | `NetGameJoinTests`, `MultiPlayerGameTests`, `NetGameStressTests` (100×) |
