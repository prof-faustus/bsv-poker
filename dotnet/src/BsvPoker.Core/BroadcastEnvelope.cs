using System.Text;
using System.Text.Json;
using BsvPoker.Crypto;

namespace BsvPoker.Core;

/// <summary>
/// A self-contained, on-chain-travellable GROUP message built on key-graph broadcast encryption
/// (GB 2623780 B, see <see cref="BroadcastEncryption"/>). The sender packages, in ONE envelope:
///   • the message encrypted ONCE under the graph's root/message key,
///   • the published "encrypted data items" (each non-root node's key wrapping its parent's),
///   • per-MEMBER leaf keys, each ECDH-sealed to that member's PUBLIC key (so only that member's private key
///     opens it) together with the member's leaf node id.
/// Any eligible member opens the envelope with their private key alone — no shared state, no sender graph,
/// no per-recipient re-encryption of the body — so the envelope can sit on-chain (store-and-forward) and be
/// delivered when an offline member returns. This is the GROUP ("broadcast encryption") send mode the chat
/// uses, NOT a public broadcast: only the selected members can read it.
/// </summary>
public sealed class BroadcastEnvelope
{
    /// <summary>A member's wrapped leaf key: their pubkey (hex), leaf node id, and the ECDH-sealed leaf key.</summary>
    public sealed record Member(string PubHex, int Leaf, string SealedLeafKeyHex);

    public string SenderPubHex { get; init; } = "";
    public string SealedMessageHex { get; init; } = "";
    public List<BroadcastEncryption.Item> Items { get; init; } = new();
    public List<Member> Members { get; init; } = new();

    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    /// <summary>
    /// Seal <paramref name="plaintext"/> to the selected members. <paramref name="memberPubs"/> are 33-byte
    /// compressed pubkeys (hex); duplicates are removed and order is irrelevant. The sender needs only the
    /// members' PUBLIC keys.
    /// </summary>
    public static BroadcastEnvelope Seal(IReadOnlyList<string> memberPubs, byte[] senderPriv32, byte[] senderPub33, byte[] plaintext)
    {
        var pubs = memberPubs.Select(p => p.ToLowerInvariant()).Distinct().ToList();
        if (pubs.Count == 0) throw new ArgumentException("at least one member required");

        // The graph needs a power-of-two leaf count; pad with synthetic slots that nobody holds a key for.
        int n = 1; while (n < pubs.Count) n <<= 1;
        var userIds = new List<ulong>(n);
        for (ulong i = 0; i < (ulong)n; i++) userIds.Add(i + 1);   // synthetic graph user ids 1..n

        var g = BroadcastEncryption.Build(userIds);
        var items = g.EncryptedDataItems();
        var sealedMsg = g.EncryptMessage(plaintext);

        var members = new List<Member>(pubs.Count);
        for (int k = 0; k < pubs.Count; k++)
        {
            ulong uid = userIds[k];
            var leafKey = g.UserLeafKey(uid)!;                      // this slot's leaf key
            int leaf = g.LeafOf(uid);
            var pub33 = Convert.FromHexString(pubs[k]);
            var sealedLeaf = DistributedKeyGen.SealScalar(leafKey, pub33, pub33);   // ECDH-sealed to the member's pubkey (bound as AAD)
            members.Add(new Member(pubs[k], leaf, sealedLeaf));
        }

        return new BroadcastEnvelope
        {
            SenderPubHex = Convert.ToHexString(senderPub33).ToLowerInvariant(),
            SealedMessageHex = Convert.ToHexString(sealedMsg).ToLowerInvariant(),
            Items = items,
            Members = members,
        };
    }

    /// <summary>
    /// Open the envelope as the holder of <paramref name="myPriv32"/> / <paramref name="myPub33"/>. Finds this
    /// member's wrapped leaf key, ECDH-unwraps it, then decrypts up the leaf→root path to the message key.
    /// Throws if this key is not one of the selected members (or the envelope was tampered with).
    /// </summary>
    public byte[] Open(byte[] myPriv32, byte[] myPub33)
    {
        var myHex = Convert.ToHexString(myPub33).ToLowerInvariant();
        var mine = Members.FirstOrDefault(m => m.PubHex == myHex)
                   ?? throw new InvalidOperationException("not a member of this group message");
        var leafKey = DistributedKeyGen.OpenScalar(mine.SealedLeafKeyHex, myPriv32, myPub33);   // throws on wrong key / tamper
        var sealedMsg = Convert.FromHexString(SealedMessageHex);
        return BroadcastEncryption.DecryptPath(mine.Leaf, leafKey, Items, sealedMsg);
    }

    /// <summary>True iff this key can open the envelope (is a selected member and the envelope is intact).</summary>
    public bool CanOpen(byte[] myPriv32, byte[] myPub33)
    {
        try { Open(myPriv32, myPub33); return true; } catch { return false; }
    }

    /// <summary>Serialize to JSON for on-chain store-and-forward (Items use hex-encoded wrapped keys via the record).</summary>
    public string ToJson() => JsonSerializer.Serialize(new Wire(this), Json);

    /// <summary>Parse an envelope serialized by <see cref="ToJson"/>.</summary>
    public static BroadcastEnvelope FromJson(string json)
    {
        var w = JsonSerializer.Deserialize<Wire>(json, Json) ?? throw new ArgumentException("invalid envelope json");
        return w.ToEnvelope();
    }

    // The on-the-wire DTO: Item.WrappedParentKey is a byte[], hex-encoded here so the JSON stays compact/portable.
    private sealed class Wire
    {
        public string SenderPubHex { get; set; } = "";
        public string SealedMessageHex { get; set; } = "";
        public List<WireItem> Items { get; set; } = new();
        public List<Member> Members { get; set; } = new();

        public Wire() { }
        public Wire(BroadcastEnvelope e)
        {
            SenderPubHex = e.SenderPubHex;
            SealedMessageHex = e.SealedMessageHex;
            Items = e.Items.Select(i => new WireItem { Node = i.Node, Parent = i.Parent, WrappedParentKeyHex = Convert.ToHexString(i.WrappedParentKey).ToLowerInvariant() }).ToList();
            Members = e.Members.ToList();
        }
        public BroadcastEnvelope ToEnvelope() => new()
        {
            SenderPubHex = SenderPubHex,
            SealedMessageHex = SealedMessageHex,
            Items = Items.Select(i => new BroadcastEncryption.Item(i.Node, i.Parent, Convert.FromHexString(i.WrappedParentKeyHex))).ToList(),
            Members = Members,
        };
    }

    private sealed class WireItem
    {
        public int Node { get; set; }
        public int Parent { get; set; }
        public string WrappedParentKeyHex { get; set; } = "";
    }
}
