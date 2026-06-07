using System.Security.Cryptography;
using BsvPoker.Crypto;

namespace BsvPoker.Core;

/// <summary>
/// Dealerless mental poker with TRUE per-card privacy, using commutative encryption on secp256k1 — the
/// BSV curve, no new dependency. Each card i is a fixed public curve point Mᵢ = (i+1)·G. A card is
/// "encrypted" by scalar-multiplying its point by a secret scalar; because a·(b·P) == b·(a·P), masks
/// applied by different players COMMUTE and can be stripped in any order. This is the standard
/// Barnett–Smart shuffle.
///
/// Protocol (works for any number of players; each step is one player acting in turn):
///   0. Deck starts as the public base points [M₀ … M_{n-1}]  (<see cref="BaseDeck"/>).
///   1. SHUFFLE: each player applies one secret GLOBAL scalar c to every card and a secret permutation,
///      then passes the deck on. After everyone, deck[k] = (∏c)·M_{σ(k)} in a hidden order.
///   2. REMASK: each player removes their global c and re-applies an INDEPENDENT per-card scalar d_k.
///      After everyone, deck[k] = (∏_p d_{p,k})·M_{σ(k)} — every position has its own product key.
///   3. DEAL a position to player T: every OTHER player privately sends T their d for that position; T
///      strips them and T's own d, recovering M_{σ(k)} and identifying the card. No one else can: they
///      lack T's secret d. A BOARD card is dealt by ALL players revealing their d for that position.
///
/// Privacy is unconditional under the DDH-style hardness of the curve: a non-recipient who strips only
/// the masks they know is left with d_T·M, which matches no base point. Correct play is verifiable: at
/// showdown a player reveals the d's for their hole cards and everyone recomputes the same M.
///
/// This module is the pure math (no I/O), so it is fully simulated and proven in the test suite.
/// </summary>
public static class MentalPokerEC
{
    /// <summary>A fresh random non-zero scalar (a mask). 32 bytes; reduced/validated on use.</summary>
    public static byte[] NewScalar()
    {
        while (true)
        {
            var s = RandomNumberGenerator.GetBytes(32);
            try { _ = Secp256k1.ScalarInverse(s); return s; } // ensures it is a valid, invertible scalar
            catch { /* zero scalar: retry */ }
        }
    }

    /// <summary>n fresh independent per-card scalars (one player's d_{·} for a deal).</summary>
    public static byte[][] NewPerCardScalars(int n)
    {
        var a = new byte[n][];
        for (int i = 0; i < n; i++) a[i] = NewScalar();
        return a;
    }

    // The base points Mᵢ are fixed and public; cache them per deck size so repeated deals/identifies do
    // not recompute scalar multiplications.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte[][]> BaseCache = new();
    private static byte[][] BasePoints(int n) => BaseCache.GetOrAdd(n, m =>
    {
        var d = new byte[m][];
        for (int i = 0; i < m; i++) d[i] = Secp256k1.CardBasePoint(i);
        return d;
    });

    /// <summary>The initial deck: the public base points M₀ … M_{n-1}.</summary>
    public static byte[][] BaseDeck(int n) => (byte[][])BasePoints(n).Clone();

    /// <summary>
    /// Phase 1 — a player masks every card with one global scalar and applies a secret permutation.
    /// <paramref name="perm"/> must be a permutation of [0..n): the output at position i is the masked
    /// card that was at position perm[i].
    /// </summary>
    public static byte[][] ShuffleMask(byte[][] deck, byte[] global, int[] perm)
    {
        ValidateDeck(deck);
        if (!Secp256k1.IsValidScalar(global)) throw new ArgumentException("invalid global scalar");
        ValidatePermutation(perm, deck.Length);   // no out-of-range, no duplicates, no dropped/duplicated cards
        var masked = new byte[deck.Length][];
        for (int i = 0; i < deck.Length; i++) masked[i] = Secp256k1.PointMul(deck[i], global);
        var outp = new byte[deck.Length][];
        for (int i = 0; i < deck.Length; i++) outp[i] = masked[perm[i]];
        return outp;
    }

    /// <summary>
    /// Validate that every entry is a valid on-curve compressed point AND that there are NO duplicate points
    /// (a duplicate means a card was dropped and another duplicated). Rejects hostile/malformed decks.
    /// </summary>
    public static void ValidateDeck(byte[][] deck)
    {
        if (deck == null || deck.Length == 0) throw new ArgumentException("empty deck");
        var seen = new HashSet<string>(deck.Length);
        foreach (var p in deck)
        {
            if (p == null || !Secp256k1.IsValidPoint(p)) throw new ArgumentException("deck contains an invalid point");
            if (!seen.Add(Convert.ToHexString(p))) throw new ArgumentException("deck contains a DUPLICATE point (card dropped/duplicated)");
        }
    }

    /// <summary>True iff the deck is all-valid points with no duplicates (non-throwing form of <see cref="ValidateDeck"/>).</summary>
    public static bool IsWellFormedDeck(byte[][] deck)
    {
        try { ValidateDeck(deck); return true; } catch { return false; }
    }

    /// <summary>Validate that <paramref name="perm"/> is a genuine permutation of [0..n) — no dup, no out-of-range.</summary>
    public static void ValidatePermutation(int[] perm, int n)
    {
        if (perm == null || perm.Length != n) throw new ArgumentException("permutation length mismatch");
        var seen = new bool[n];
        foreach (var v in perm)
        {
            if (v < 0 || v >= n) throw new ArgumentException("permutation index out of range");
            if (seen[v]) throw new ArgumentException("permutation has a duplicate index (card dropped/duplicated)");
            seen[v] = true;
        }
    }

    /// <summary>
    /// Phase 2 — a player removes their global scalar from every card and re-applies an independent
    /// per-card scalar, so each position ends up with its own key. No reordering here.
    /// </summary>
    public static byte[][] Remask(byte[][] deck, byte[] global, byte[][] perCard)
    {
        ValidateDeck(deck);
        if (!Secp256k1.IsValidScalar(global)) throw new ArgumentException("invalid global scalar");
        if (perCard == null || perCard.Length != deck.Length) throw new ArgumentException("per-card scalar count mismatch");
        foreach (var d in perCard) if (!Secp256k1.IsValidScalar(d)) throw new ArgumentException("invalid per-card scalar");
        var inv = Secp256k1.ScalarInverse(global);
        var outp = new byte[deck.Length][];
        for (int i = 0; i < deck.Length; i++)
            outp[i] = Secp256k1.PointMul(Secp256k1.PointMul(deck[i], inv), perCard[i]); // strip c, apply d_i
        return outp;
    }

    /// <summary>Strip the given per-card scalars from a card point (each removed via its inverse). Validates all inputs.</summary>
    public static byte[] Unmask(byte[] point33, IEnumerable<byte[]> scalarsToRemove)
    {
        if (point33 == null || !Secp256k1.IsValidPoint(point33)) throw new ArgumentException("invalid card point");
        var p = point33;
        foreach (var s in scalarsToRemove)
        {
            if (!Secp256k1.IsValidScalar(s)) throw new ArgumentException("invalid scalar share");
            p = Secp256k1.PointMul(p, Secp256k1.ScalarInverse(s));
        }
        return p;
    }

    /// <summary>
    /// Identify a fully-unmasked card point as a card index by matching it to the base points; returns
    /// -1 if it matches none (e.g. a non-recipient who could not strip the owner's mask).
    /// </summary>
    public static int Identify(byte[] point33, int n)
    {
        var bp = BasePoints(n);
        for (int i = 0; i < n; i++)
            if (CryptographicOperations.FixedTimeEquals(point33, bp[i])) return i;
        return -1;
    }
}
