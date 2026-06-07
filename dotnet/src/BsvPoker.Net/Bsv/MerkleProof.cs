using BsvPoker.Crypto;

namespace BsvPoker.Net.Bsv;

/// <summary>
/// Bitcoin merkle trees + SPV inclusion proofs. A node verifies that a transaction is in a block by
/// folding its txid up a merkle branch of sibling hashes and checking the result equals the block header's
/// merkle root (which the node has already validated via the header chain). This is how on-chain game
/// state is confirmed from the chain without trusting any peer. All hashes are internal (little-endian)
/// byte order; an odd row duplicates its last node, per Bitcoin consensus.
/// </summary>
public static class MerkleProof
{
    private static byte[] Pair(byte[] l, byte[] r) { var b = new byte[64]; l.CopyTo(b, 0); r.CopyTo(b, 32); return Hashes.Sha256d(b); }

    /// <summary>The parent hash of two merkle children (SHA-256d of the 64-byte concatenation, internal order).</summary>
    public static byte[] HashPair(byte[] left, byte[] right) => Pair(left, right);

    /// <summary>The merkle root of a list of txids (internal byte order).</summary>
    public static byte[] Root(IReadOnlyList<byte[]> txids)
    {
        if (txids.Count == 0) return new byte[32];
        var level = txids.Select(x => (byte[])x.Clone()).ToList();
        while (level.Count > 1)
        {
            var next = new List<byte[]>();
            for (int i = 0; i < level.Count; i += 2)
                next.Add(Pair(level[i], i + 1 < level.Count ? level[i + 1] : level[i])); // duplicate last if odd
            level = next;
        }
        return level[0];
    }

    /// <summary>The sibling-hash branch proving the leaf at <paramref name="index"/> is in the tree.</summary>
    public static byte[][] Branch(IReadOnlyList<byte[]> txids, int index)
    {
        if (index < 0 || index >= txids.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var branch = new List<byte[]>();
        var level = txids.Select(x => (byte[])x.Clone()).ToList();
        int idx = index;
        while (level.Count > 1)
        {
            int partner = (idx % 2 == 0) ? (idx + 1 < level.Count ? idx + 1 : idx) : idx - 1; // self if last+odd
            branch.Add((byte[])level[partner].Clone());
            var next = new List<byte[]>();
            for (int i = 0; i < level.Count; i += 2)
                next.Add(Pair(level[i], i + 1 < level.Count ? level[i + 1] : level[i]));
            level = next; idx /= 2;
        }
        return branch.ToArray();
    }

    /// <summary>Verify that <paramref name="txid"/> at <paramref name="index"/> folds up <paramref name="branch"/> to <paramref name="root"/>.</summary>
    public static bool Verify(byte[] txid, int index, byte[][] branch, byte[] root)
    {
        if (txid.Length != 32 || root.Length != 32 || index < 0) return false;
        var h = (byte[])txid.Clone();
        int idx = index;
        foreach (var sib in branch)
        {
            if (sib.Length != 32) return false;
            h = (idx % 2 == 0) ? Pair(h, sib) : Pair(sib, h);
            idx /= 2;
        }
        return h.AsSpan().SequenceEqual(root);
    }
}
