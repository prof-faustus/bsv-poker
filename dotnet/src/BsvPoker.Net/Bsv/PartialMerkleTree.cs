using System.Buffers.Binary;

namespace BsvPoker.Net.Bsv;

/// <summary>
/// The Bitcoin <c>merkleblock</c> partial merkle tree: a compact SPV proof a peer hands over showing that
/// one (or a few) transactions are in a block, without sending the whole block. This is the no-server way a
/// payer proves to a recipient that the funding transaction was mined: the payer builds the partial tree for
/// the funding txid over the block's txids and sends a merkleblock envelope; the recipient parses it,
/// recomputes the merkle root, and checks it equals the root in a header it has itself validated
/// (<see cref="HeaderStore"/> / <see cref="HeadersChain"/>). All hashes are internal (little-endian) order.
/// </summary>
public static class PartialMerkleTree
{
    public sealed record Parsed(BlockHeader Header, uint TotalTx, byte[] Root, List<(byte[] Txid, int Index)> Matched);

    private static int TreeWidth(int total, int height) => (total + (1 << height) - 1) >> height;
    private static int TreeHeight(int total) { int h = 0; while (TreeWidth(total, h) > 1) h++; return h; }

    // ---- Build (payer side): partial tree for the matched leaves over the full txid list ----

    /// <summary>Build the (hashes, flag-bits) of a partial merkle tree for the matched leaf indices.</summary>
    public static (List<byte[]> Hashes, List<bool> Bits) Build(IReadOnlyList<byte[]> leaves, ISet<int> matched)
    {
        if (leaves.Count == 0) throw new ArgumentException("no leaves");
        var hashes = new List<byte[]>(); var bits = new List<bool>();
        BuildNode(TreeHeight(leaves.Count), 0, leaves, matched, hashes, bits);
        return (hashes, bits);
    }

    private static byte[] SubtreeHash(int height, int pos, IReadOnlyList<byte[]> leaves)
    {
        if (height == 0) return leaves[pos];
        var left = SubtreeHash(height - 1, pos * 2, leaves);
        var right = (pos * 2 + 1 < TreeWidth(leaves.Count, height - 1))
            ? SubtreeHash(height - 1, pos * 2 + 1, leaves) : left; // duplicate last on an odd row
        return MerkleProof.HashPair(left, right);
    }

    private static void BuildNode(int height, int pos, IReadOnlyList<byte[]> leaves, ISet<int> matched, List<byte[]> hashes, List<bool> bits)
    {
        bool parentOfMatch = false;
        int total = leaves.Count;
        for (int p = pos << height; p < ((pos + 1) << height) && p < total; p++) parentOfMatch |= matched.Contains(p);
        bits.Add(parentOfMatch);
        if (height == 0 || !parentOfMatch) { hashes.Add(SubtreeHash(height, pos, leaves)); return; }
        BuildNode(height - 1, pos * 2, leaves, matched, hashes, bits);
        if (pos * 2 + 1 < TreeWidth(total, height - 1)) BuildNode(height - 1, pos * 2 + 1, leaves, matched, hashes, bits);
    }

    // ---- Extract (recipient side): recompute the root + collect matched (txid, index) ----

    /// <summary>
    /// Recompute the merkle root from a partial tree and collect the matched (txid, index) pairs. Throws on a
    /// malformed tree (too many/few hashes or flag bits, an illegitimate duplicated right branch, etc.) so a
    /// hostile peer cannot smuggle a tree that "verifies" against an unrelated root.
    /// </summary>
    public static (byte[] Root, List<(byte[] Txid, int Index)> Matched) Extract(int total, IReadOnlyList<byte[]> hashes, IReadOnlyList<bool> bits)
    {
        if (total <= 0) throw new ArgumentException("total must be positive");
        int bitsUsed = 0, hashUsed = 0;
        var matched = new List<(byte[], int)>();
        var root = ExtractNode(TreeHeight(total), 0, total, hashes, bits, ref bitsUsed, ref hashUsed, matched);
        // every hash must be consumed; flag bits must be consumed to within the final byte's padding (< 8)
        if (hashUsed != hashes.Count) throw new ArgumentException("not all hashes consumed");
        if ((bitsUsed + 7) / 8 != (bits.Count + 7) / 8) throw new ArgumentException("flag bits not consumed");
        return (root, matched);
    }

    private static byte[] ExtractNode(int height, int pos, int total, IReadOnlyList<byte[]> hashes, IReadOnlyList<bool> bits,
        ref int bitsUsed, ref int hashUsed, List<(byte[], int)> matched)
    {
        if (bitsUsed >= bits.Count) throw new ArgumentException("ran out of flag bits");
        bool parentOfMatch = bits[bitsUsed++];
        if (height == 0 || !parentOfMatch)
        {
            if (hashUsed >= hashes.Count) throw new ArgumentException("ran out of hashes");
            var hash = hashes[hashUsed++];
            if (height == 0 && parentOfMatch) matched.Add(((byte[])hash.Clone(), pos));
            return hash;
        }
        var left = ExtractNode(height - 1, pos * 2, total, hashes, bits, ref bitsUsed, ref hashUsed, matched);
        byte[] right;
        if (pos * 2 + 1 < TreeWidth(total, height - 1))
        {
            right = ExtractNode(height - 1, pos * 2 + 1, total, hashes, bits, ref bitsUsed, ref hashUsed, matched);
            if (right.AsSpan().SequenceEqual(left)) throw new ArgumentException("illegitimate duplicated right branch");
        }
        else right = left; // legitimately duplicated last node on an odd row
        return MerkleProof.HashPair(left, right);
    }

    // ---- merkleblock wire encoding ----

    /// <summary>Assemble a full <c>merkleblock</c> payload: 80-byte header, total tx count, hashes, flag bytes.</summary>
    public static byte[] BuildMerkleBlock(BlockHeader header, IReadOnlyList<byte[]> leaves, ISet<int> matched)
    {
        var (hashes, bits) = Build(leaves, matched);
        var b = new List<byte>();
        b.AddRange(header.Serialize());
        var tot = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(tot, (uint)leaves.Count); b.AddRange(tot);
        BsvVersion.WriteVarInt(b, (ulong)hashes.Count);
        foreach (var h in hashes) b.AddRange(h);
        var flags = PackBits(bits);
        BsvVersion.WriteVarInt(b, (ulong)flags.Length);
        b.AddRange(flags);
        return b.ToArray();
    }

    /// <summary>Parse a <c>merkleblock</c> payload, recompute the root, and return the matched (txid, index) pairs.</summary>
    public static Parsed ParseMerkleBlock(byte[] payload)
    {
        if (payload.Length < 84) throw new ArgumentException("merkleblock too short");
        var header = BlockHeader.Parse(payload.AsSpan(0, 80));
        int o = 80;
        uint total = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(o, 4)); o += 4;
        ulong nHashes = BsvVersion.ReadVarInt(payload, ref o);
        var hashes = new List<byte[]>();
        for (ulong i = 0; i < nHashes; i++)
        {
            if (o + 32 > payload.Length) throw new ArgumentException("truncated hashes");
            hashes.Add(payload.AsSpan(o, 32).ToArray()); o += 32;
        }
        ulong nFlags = BsvVersion.ReadVarInt(payload, ref o);
        if (o + (int)nFlags > payload.Length) throw new ArgumentException("truncated flags");
        var bits = UnpackBits(payload, o, (int)nFlags);
        var (root, matched) = Extract((int)total, hashes, bits);
        return new Parsed(header, total, root, matched);
    }

    private static byte[] PackBits(List<bool> bits)
    {
        var bytes = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++) if (bits[i]) bytes[i / 8] |= (byte)(1 << (i % 8));
        return bytes;
    }

    private static List<bool> UnpackBits(byte[] src, int offset, int len)
    {
        var bits = new List<bool>(len * 8);
        for (int i = 0; i < len * 8; i++) bits.Add((src[offset + i / 8] & (1 << (i % 8))) != 0);
        return bits;
    }
}
