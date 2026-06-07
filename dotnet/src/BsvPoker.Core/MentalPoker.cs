using System.Security.Cryptography;
using BsvPoker.Crypto;

namespace BsvPoker.Core;

/// <summary>
/// Dealerless mental poker (no trusted dealer, no single party knows the order). Each player commits
/// SHA-256(entropy) BEFORE anyone reveals (stops late-entropy selection); the deck order is the
/// COMPOSITION of every player's secret permutation, each derived by an UNBIASED (rejection-sampled)
/// Fisher–Yates over an HMAC-SHA256 counter stream. No one can fix the deck alone; the combined per-card
/// key is a real secp256k1 point bound to the combined seed.
/// </summary>
public static class MentalPoker
{
    private const int MaxRejectionDraws = 128;

    public static byte[] Commit(byte[] entropy) => Hashes.Sha256(entropy);

    public static bool VerifyCommit(byte[] commit, byte[] entropy)
        => CryptographicOperations.FixedTimeEquals(commit, Hashes.Sha256(entropy));

    public static byte[] FreshEntropy() => RandomNumberGenerator.GetBytes(32);

    private static IEnumerable<uint> DrawStream(byte[] seed, string info)
    {
        using var hmac = new HMACSHA256(seed);
        long counter = 0;
        while (true)
        {
            var block = hmac.ComputeHash(System.Text.Encoding.ASCII.GetBytes($"{info}:{counter++}"));
            for (int i = 0; i + 4 <= block.Length; i += 4)
                yield return (uint)((block[i] << 24) | (block[i + 1] << 16) | (block[i + 2] << 8) | block[i + 3]);
        }
    }

    /// <summary>An UNBIASED Fisher–Yates permutation of [0..n) seeded by one player's entropy.</summary>
    public static int[] PermutationFromEntropy(byte[] entropy, int n)
    {
        var perm = Enumerable.Range(0, n).ToArray();
        using var it = DrawStream(entropy, "shuffle-perm").GetEnumerator();
        for (int i = n - 1; i > 0; i--)
        {
            ulong bound = (ulong)(i + 1);
            ulong limit = 0x100000000UL / bound * bound; // <= 2^32 (no uint overflow); == 2^32 when bound | 2^32
            it.MoveNext(); ulong r = it.Current;
            int draws = 0;
            while (r >= limit)
            {
                if (++draws > MaxRejectionDraws) throw new InvalidOperationException("rejection sampling exceeded bound");
                it.MoveNext(); r = it.Current;
            }
            int j = (int)(r % bound);
            (perm[i], perm[j]) = (perm[j], perm[i]);
        }
        return perm;
    }

    public static int[] Compose(IReadOnlyList<int[]> perms, int n)
    {
        foreach (var p in perms) MentalPokerEC.ValidatePermutation(p, n); // reject any non-permutation input
        var composed = Enumerable.Range(0, n).ToArray();
        foreach (var p in perms) composed = composed.Select(x => p[x]).ToArray();
        return composed;
    }

    /// <summary>The agreed deck order = composition of every player's secret permutation over [0..52).</summary>
    public static List<Card> ShuffledDeck(IReadOnlyList<byte[]> partyEntropy, int deckSize = 52)
    {
        var perms = partyEntropy.Select(e => PermutationFromEntropy(e, deckSize)).ToList();
        var order = Compose(perms, deckSize);
        return order.Select(Card.FromIndex).ToList();
    }

    /// <summary>Dealerless shuffle over an arbitrary card set (e.g. a variant's 20-card Royal deck).</summary>
    public static List<Card> ShuffledFrom(IReadOnlyList<byte[]> partyEntropy, IReadOnlyList<Card> cardSet)
    {
        int n = cardSet.Count;
        var perms = partyEntropy.Select(e => PermutationFromEntropy(e, n)).ToList();
        var order = Compose(perms, n);
        return order.Select(i => cardSet[i]).ToList();
    }

    public static byte[] CombinedSeed(IReadOnlyList<byte[]> entropies)
    {
        using var ms = new MemoryStream();
        foreach (var e in entropies) ms.Write(e);
        return Hashes.Sha256(ms.ToArray());
    }

    /// <summary>A real secp256k1 combined per-card key bound to the combined seed (rehash on bad scalar).</summary>
    public static byte[] CombinedKey(byte[] seed, int j)
    {
        for (int salt = 0; salt < 256; salt++)
        {
            using var hmac = new HMACSHA256(seed);
            var scalar = hmac.ComputeHash(System.Text.Encoding.ASCII.GetBytes($"Qj:{j}:{salt}"));
            try { return Secp256k1.PublicKeyCompressed(scalar); }
            catch { /* invalid scalar, try next salt */ }
        }
        throw new InvalidOperationException("could not derive combined key");
    }
}
