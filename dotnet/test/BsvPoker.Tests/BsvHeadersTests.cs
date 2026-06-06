using BsvPoker.Net.Bsv;

namespace BsvPoker.Tests;

/// <summary>
/// The node's own header-chain validation: 80-byte header parse/serialize + PoW, and the HeadersChain
/// (parent linkage, proof-of-work, most-cumulative-work tip, reorg, orphan rejection). Synthetic
/// regtest-difficulty headers so PoW passes trivially and the linkage/reorg logic is what is exercised.
/// </summary>
public static class BsvHeadersTests
{
    private const uint EasyBits = 0x207fffff;   // regtest powlimit — target ~ 2^255, any hash passes
    private const uint HardBits = 0x1d00ffff;   // early-mainnet difficulty — a random header will not meet it

    // build a header with VALID PoW by searching a nonce (at EasyBits ~half pass, so a couple of tries)
    private static BlockHeader Mk(byte[] prevInternal, uint salt)
    {
        for (uint nonce = salt * 100000; ; nonce++)
        {
            var h = new BlockHeader(1, prevInternal, new byte[32], 1_700_000_000, EasyBits, nonce);
            if (h.MeetsPow()) return h;
        }
    }
    // a header that does NOT meet its (hard) target — for the bad-PoW cases
    private static BlockHeader MkBad(byte[] prevInternal, uint nonce)
        => new(1, prevInternal, new byte[32], 1_700_000_000, HardBits, nonce);

    public static void All()
    {
        Console.WriteLine("BSV node — header chain validation:");

        T.Run("header serialize/parse round-trips and the hash is stable", () =>
        {
            var h = Mk(new byte[32], 42);
            var bytes = h.Serialize();
            T.Eq(bytes.Length, 80, "80-byte header");
            var back = BlockHeader.Parse(bytes);
            T.Eq(T.Hex(back.Serialize()), T.Hex(bytes), "round-trips");
            T.Eq(back.HashHex(), h.HashHex(), "hash stable");
        });

        T.Run("proof-of-work: easy target passes, hard target fails", () =>
        {
            T.True(Mk(new byte[32], 1).MeetsPow(), "easy target met");
            T.False(MkBad(new byte[32], 1).MeetsPow(), "hard target not met by a random header");
        });

        T.Run("chain extends by parent linkage and tracks height", () =>
        {
            var c = new HeadersChain();
            var g = Mk(new byte[32], 1);
            T.Eq(c.AddGenesis(g).ToString(), "Accepted", "genesis accepted");
            var h1 = Mk(g.Hash(), 2); var h2 = Mk(h1.Hash(), 3);
            T.Eq(c.Add(h1).ToString(), "Accepted"); T.Eq(c.Add(h2).ToString(), "Accepted");
            T.Eq(c.Height, 2, "tip at height 2");
            T.Eq(c.Tip!.HashHex, h2.HashHex(), "tip is the latest header");
            T.Eq(c.Add(h2).ToString(), "Duplicate", "duplicate ignored");
        });

        T.Run("orphan (unknown parent) is rejected", () =>
        {
            var c = new HeadersChain();
            c.AddGenesis(Mk(new byte[32], 1));
            var orphan = Mk(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), 9);
            T.Eq(c.Add(orphan).ToString(), "UnknownParent", "no known parent → rejected");
        });

        T.Run("bad proof-of-work is rejected even with a valid parent", () =>
        {
            var c = new HeadersChain();
            var g = Mk(new byte[32], 1);
            c.AddGenesis(g);
            T.Eq(c.Add(MkBad(g.Hash(), 7)).ToString(), "BadPow", "fails its own PoW target");
        });

        T.Run("reorg: the most-cumulative-work branch becomes the tip", () =>
        {
            var c = new HeadersChain();
            var g = Mk(new byte[32], 1);
            c.AddGenesis(g);
            // main branch height 2
            var b1 = Mk(g.Hash(), 10); var b2 = Mk(b1.Hash(), 11);
            c.Add(b1); c.Add(b2);
            T.Eq(c.Height, 2, "main tip height 2");
            // a competing branch from genesis: at equal height it does NOT take over...
            var a1 = Mk(g.Hash(), 20); var a2 = Mk(a1.Hash(), 21);
            c.Add(a1); T.Eq(c.Tip!.HashHex, b2.HashHex(), "side branch at lower work does not reorg");
            c.Add(a2); T.Eq(c.Tip!.HashHex, b2.HashHex(), "equal work does not reorg");
            // ...but a longer (more-work) branch does
            var a3 = Mk(a2.Hash(), 22);
            T.Eq(c.Add(a3).ToString(), "Reorg", "more-work branch triggers a reorg");
            T.Eq(c.Height, 3, "new tip height 3");
            T.Eq(c.Tip!.HashHex, a3.HashHex(), "tip switched to the heavier branch");
        });
    }
}
