using BsvPoker.Core;
using BsvPoker.Crypto;
using BsvPoker.Net.Bsv;

namespace BsvPoker.Tests;

/// <summary>
/// The merkleblock partial merkle tree: a payer's compact SPV proof round-trips (build → encode → parse),
/// recomputes the real merkle root, names exactly the matched txids, and a tampered envelope is rejected.
/// Then the full no-server funding path: verify a funding tx from a merkleblock against a validated header.
/// </summary>
public static class PartialMerkleTreeTests
{
    private const uint EasyBits = 0x207fffff;

    private static BlockHeader MineHeader(byte[] prev, byte[] root)
    {
        for (uint nonce = 1; ; nonce++)
        {
            var h = new BlockHeader(1, prev, root, 1_700_000_000, EasyBits, nonce);
            if (h.MeetsPow()) return h;
        }
    }

    private static List<byte[]> Leaves(int n)
    {
        var l = new List<byte[]>();
        for (int i = 0; i < n; i++) l.Add(Hashes.Sha256d(new byte[] { (byte)i, (byte)(i >> 8), 0xAB }));
        return l;
    }

    public static void All()
    {
        Console.WriteLine("SPV merkleblock (partial merkle tree, no-server funding proof):");

        T.Run("partial tree recomputes the true merkle root for tree sizes 1..16", () =>
        {
            for (int n = 1; n <= 16; n++)
            {
                var leaves = Leaves(n);
                int target = n / 2;
                var (root, matched) = PartialMerkleTree.Extract(n,
                    PartialMerkleTree.Build(leaves, new HashSet<int> { target }).Hashes,
                    PartialMerkleTree.Build(leaves, new HashSet<int> { target }).Bits);
                T.Eq(T.Hex(root), T.Hex(MerkleProof.Root(leaves)), $"root matches (n={n})");
                T.Eq(matched.Count, 1, "one matched leaf");
                T.Eq(matched[0].Index, target, "matched index correct");
                T.Eq(T.Hex(matched[0].Txid), T.Hex(leaves[target]), "matched txid correct");
            }
        });

        T.Run("merkleblock encode/parse round-trips and matches the header root", () =>
        {
            var leaves = Leaves(7);
            int idx = 5;
            var header = MineHeader(new byte[32], MerkleProof.Root(leaves));
            var payload = PartialMerkleTree.BuildMerkleBlock(header, leaves, new HashSet<int> { idx });
            var parsed = PartialMerkleTree.ParseMerkleBlock(payload);
            T.Eq(parsed.Header.HashHex(), header.HashHex(), "header survives");
            T.Eq(T.Hex(parsed.Root), T.Hex(header.MerkleRoot), "recomputed root == header root");
            T.Eq((int)parsed.TotalTx, 7, "total tx count");
            T.Eq(parsed.Matched.Count, 1, "one matched");
            T.Eq(parsed.Matched[0].Index, idx, "matched index");
        });

        T.Run("multiple matched txids are all extracted", () =>
        {
            var leaves = Leaves(11);
            var set = new HashSet<int> { 1, 4, 9 };
            var header = MineHeader(new byte[32], MerkleProof.Root(leaves));
            var parsed = PartialMerkleTree.ParseMerkleBlock(PartialMerkleTree.BuildMerkleBlock(header, leaves, set));
            T.Eq(T.Hex(parsed.Root), T.Hex(header.MerkleRoot), "root matches");
            var gotIdx = parsed.Matched.Select(m => m.Index).OrderBy(x => x).ToArray();
            T.Eq(string.Join(",", gotIdx), "1,4,9", "all matched indices recovered");
        });

        T.Run("HOSTILE: a tampered proof hash yields a wrong root (rejected by the caller)", () =>
        {
            var leaves = Leaves(8);
            var header = MineHeader(new byte[32], MerkleProof.Root(leaves));
            var payload = PartialMerkleTree.BuildMerkleBlock(header, leaves, new HashSet<int> { 3 });
            payload[90] ^= 0xFF; // flip a byte inside the first proof hash
            try
            {
                var parsed = PartialMerkleTree.ParseMerkleBlock(payload);
                T.False(parsed.Root.AsSpan().SequenceEqual(header.MerkleRoot), "tampered root no longer matches the header");
            }
            catch { /* a structurally-broken tree throwing is also an acceptable rejection */ }
        });

        T.Run("HOSTILE: truncated / malformed merkleblock throws, never returns a bogus proof", () =>
        {
            T.Throws(() => PartialMerkleTree.ParseMerkleBlock(new byte[10]), "too short");
            var leaves = Leaves(4);
            var header = MineHeader(new byte[32], MerkleProof.Root(leaves));
            var good = PartialMerkleTree.BuildMerkleBlock(header, leaves, new HashSet<int> { 0 });
            T.Throws(() => PartialMerkleTree.ParseMerkleBlock(good[..^1]), "truncated flags");
        });

        T.Run("no-server funding: verify a funding tx from a merkleblock against a validated header", () =>
        {
            var me = Secp256k1.GenerateKeyPair();
            var other = Secp256k1.GenerateKeyPair();
            var fundTx = new Chain.Tx(2,
                new() { new("cd".PadRight(64, '3'), 0, Array.Empty<byte>(), 0xffffffff) },
                new() { new(8000, Chain.P2pkhLockForPub(other.Pub)),
                        new(42000, Chain.P2pkhLockForPub(me.Pub)) }, 0);
            var fundLeaf = Hashes.Sha256d(Chain.Serialize(fundTx));

            // build a block whose txids include the funding tx
            var leaves = Leaves(6); int idx = 4; leaves[idx] = fundLeaf;
            var header = MineHeader(new byte[32], MerkleProof.Root(leaves));
            var chain = new HeadersChain(); chain.AddGenesis(header);
            var mb = PartialMerkleTree.BuildMerkleBlock(header, leaves, new HashSet<int> { idx });

            var utxo = SpvFunding.VerifyFromMerkleBlock(fundTx, 1, mb, chain, me.Pub, 0, 0);
            T.True(utxo != null, "funding learned from the merkleblock");
            T.Eq(utxo!.Value, 42000L, "value correct");

            // wrong key → rejected
            T.True(SpvFunding.VerifyFromMerkleBlock(fundTx, 1, mb, chain, other.Pub, 0, 0) == null, "vout 1 does not pay 'other'");
            // header not in our validated chain → rejected
            var orphanChain = new HeadersChain(); orphanChain.AddGenesis(MineHeader(new byte[32], MerkleProof.Root(Leaves(3))));
            T.True(SpvFunding.VerifyFromMerkleBlock(fundTx, 1, mb, orphanChain, me.Pub, 0, 0) == null, "unknown block rejected");
        });
    }
}
