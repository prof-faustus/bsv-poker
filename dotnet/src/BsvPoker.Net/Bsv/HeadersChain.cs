using System.Numerics;

namespace BsvPoker.Net.Bsv;

/// <summary>
/// The validated block-header chain the node maintains itself. Each accepted header must link to a known
/// parent and meet its proof-of-work; the active tip is the branch with the most cumulative work, so the
/// node follows the real chain and handles reorgs without trusting any peer. (Header validation + the work
/// rule; full difficulty-retarget enforcement is layered on next.)
/// </summary>
public sealed class HeadersChain
{
    public sealed record Entry(BlockHeader Header, int Height, BigInteger Work, string HashHex, string PrevHex);

    private readonly Dictionary<string, Entry> _byHash = new();
    public Entry? Tip { get; private set; }
    public int Height => Tip?.Height ?? -1;
    public int Count => _byHash.Count;

    public enum AddResult { Accepted, Duplicate, UnknownParent, BadPow, Reorg }

    /// <summary>Seed the chain with its genesis header (no parent, PoW still checked).</summary>
    public AddResult AddGenesis(BlockHeader h)
    {
        if (!h.MeetsPow()) return AddResult.BadPow;
        var hex = h.HashHex();
        if (_byHash.ContainsKey(hex)) return AddResult.Duplicate;
        var e = new Entry(h, 0, BlockHeader.Work(h.Bits), hex, PrevHex(h));
        _byHash[hex] = e; Tip = e;
        return AddResult.Accepted;
    }

    /// <summary>Add a header: must link to a known parent and meet PoW; updates the tip on more work (reorg).</summary>
    public AddResult Add(BlockHeader h)
    {
        var hex = h.HashHex();
        if (_byHash.ContainsKey(hex)) return AddResult.Duplicate;
        if (!h.MeetsPow()) return AddResult.BadPow;
        var prevHex = PrevHex(h);
        if (!_byHash.TryGetValue(prevHex, out var parent)) return AddResult.UnknownParent;
        var e = new Entry(h, parent.Height + 1, parent.Work + BlockHeader.Work(h.Bits), hex, prevHex);
        _byHash[hex] = e;
        if (Tip == null || e.Work > Tip.Work) { bool reorg = Tip != null && e.PrevHex != Tip.HashHex; Tip = e; return reorg ? AddResult.Reorg : AddResult.Accepted; }
        return AddResult.Accepted; // stored on a side branch
    }

    public bool Knows(string hashHex) => _byHash.ContainsKey(hashHex);
    public Entry? Get(string hashHex) => _byHash.TryGetValue(hashHex, out var e) ? e : null;

    // a header's parent hash in display (big-endian) form, matching HashHex()
    private static string PrevHex(BlockHeader h) { var p = (byte[])h.PrevHash.Clone(); Array.Reverse(p); return Convert.ToHexString(p).ToLowerInvariant(); }
}
