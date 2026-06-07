using System.Buffers.Binary;
using BsvPoker.Crypto;

namespace BsvPoker.Core;

/// <summary>
/// Verifiable shuffle/remask correctness for the commutative-encryption deal (audit-critical).
///
/// Each player, BEFORE the deal, publishes a COMMITMENT to the secret transformation it will apply (its
/// global scalar + permutation for a shuffle pass, or its global + per-card scalars for a remask pass). At
/// reveal/showdown the player OPENS the commitment, and any party recomputes the transformation on the
/// known input deck and checks that it reproduces the claimed output exactly AND that the output is a valid
/// bijection (no card dropped, duplicated, or substituted). A cheating shuffle/remask is therefore detected
/// — provably and accountably — because the recomputation will not match the committed-and-claimed output.
///
/// This is the commit-then-reveal correctness proof: it does not reveal secrets during play (only the
/// commitment is public until showdown), and at showdown — when masks are revealed anyway — every step is
/// checkable. Combined with <see cref="MentalPokerEC.ValidateDeck"/> (immediate no-duplicate/point checks),
/// it closes the "signed malicious points can enter the protocol" hole.
/// </summary>
public static class ShuffleProof
{
    /// <summary>Commitment to a shuffle pass (global scalar + permutation), published before the deal.</summary>
    public static byte[] CommitShuffle(byte[] global, int[] perm)
        => Hashes.Sha256(Concat(Domain("shuffle"), global, EncodePerm(perm)));

    /// <summary>Commitment to a remask pass (global scalar + per-card scalars), published before the deal.</summary>
    public static byte[] CommitRemask(byte[] global, byte[][] perCard)
        => Hashes.Sha256(Concat(Domain("remask"), global, ConcatAll(perCard)));

    /// <summary>
    /// Verify a revealed shuffle pass: the commitment matches, the permutation is genuine, the recomputed
    /// ShuffleMask(input, global, perm) equals the claimed output, and the output is a well-formed bijection.
    /// </summary>
    public static bool VerifyShuffle(byte[][] input, byte[][] claimedOutput, byte[] global, int[] perm, byte[] commitment)
    {
        try
        {
            if (!ConstEq(CommitShuffle(global, perm), commitment)) return false;
            MentalPokerEC.ValidateDeck(input);
            var recomputed = MentalPokerEC.ShuffleMask(input, global, perm); // validates global + perm too
            return EqualDecks(recomputed, claimedOutput) && MentalPokerEC.IsWellFormedDeck(claimedOutput);
        }
        catch { return false; }
    }

    /// <summary>
    /// Verify a revealed remask pass: the commitment matches, the recomputed Remask(input, global, perCard)
    /// equals the claimed output, and the output is a well-formed bijection.
    /// </summary>
    public static bool VerifyRemask(byte[][] input, byte[][] claimedOutput, byte[] global, byte[][] perCard, byte[] commitment)
    {
        try
        {
            if (!ConstEq(CommitRemask(global, perCard), commitment)) return false;
            MentalPokerEC.ValidateDeck(input);
            var recomputed = MentalPokerEC.Remask(input, global, perCard);
            return EqualDecks(recomputed, claimedOutput) && MentalPokerEC.IsWellFormedDeck(claimedOutput);
        }
        catch { return false; }
    }

    private static bool EqualDecks(byte[][] a, byte[][] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (!a[i].AsSpan().SequenceEqual(b[i])) return false;
        return true;
    }

    private static byte[] Domain(string s) => System.Text.Encoding.ASCII.GetBytes("bsvpoker-shuffleproof-v1:" + s);
    private static byte[] EncodePerm(int[] perm)
    {
        var b = new byte[perm.Length * 4];
        for (int i = 0; i < perm.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i * 4, 4), perm[i]);
        return b;
    }
    private static byte[] ConcatAll(byte[][] parts) { var ms = new List<byte>(); foreach (var p in parts) ms.AddRange(p); return ms.ToArray(); }
    private static byte[] Concat(params byte[][] parts) { var ms = new List<byte>(); foreach (var p in parts) ms.AddRange(p); return ms.ToArray(); }
    private static bool ConstEq(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        int d = 0; for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i]; return d == 0;
    }
}
