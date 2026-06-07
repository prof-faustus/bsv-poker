using System.Text;

namespace BsvPoker.Core;

/// <summary>Every distinct on-chain action type. Each is its own documented transaction template.</summary>
public enum TxKind
{
    Payment, KeepAlive,
    ChatDirect, ChatGroup,
    CardNft, Commitment, Reveal, ShuffleStage, Deal, BoardReveal, Showdown,
    Bet, PotEscrow, Settlement, Recovery,
    Bid, Auction, RoleClaim,
    TableGenesis, GameStart, HandStart,
    Announce
}

/// <summary>
/// The typed-transaction-template registry — the thorough version. A transaction is like a frame in IP:
/// every kind has its OWN type, with a unique tag ("frame type"), a version, a documented purpose, AND a
/// documented FIELD STRUCTURE (the named data fields that ride in the output). The type and its fields are
/// stamped into the script as a sequence of data pushes each followed by <c>OP_DROP</c> (never
/// <c>OP_RETURN</c>), ending in the owner's spend condition. Any peer can recognize the type AND read the
/// fields back out. Nothing is generic or assumed; every field is named and explicit.
/// </summary>
public static class TxTemplates
{
    /// <param name="Fields">The named, ordered data fields this type carries (its documented structure).</param>
    public sealed record Template(TxKind Kind, string Tag, int Version, string Purpose, string[] Fields);

    private const byte OP_0 = 0x00, OP_1NEGATE = 0x4f, OP_DROP = 0x75, OP_PUSHDATA1 = 0x4c, OP_PUSHDATA2 = 0x4d, OP_CHECKSIG = 0xac;

    private static readonly Dictionary<TxKind, Template> Registry = new[]
    {
        new Template(TxKind.Payment,      "BSVP:PAY:1",  1, "A plain value transfer of satoshis between wallets.", new[] { "memo" }),
        new Template(TxKind.KeepAlive,    "BSVP:KA:1",   1, "A liveness heartbeat proving a peer/seat is still present.", new[] { "seat", "nonce" }),
        new Template(TxKind.ChatDirect,   "BSVP:DM:1",   1, "A one-to-one encrypted chat message (ECDH+AES).", new[] { "senderPub", "recipientPub", "ciphertext" }),
        new Template(TxKind.ChatGroup,    "BSVP:GC:1",   1, "A group encrypted chat message (per-recipient ECDH+AES).", new[] { "groupId", "senderPub", "ciphertext" }),
        new Template(TxKind.CardNft,      "BSVP:NFT:1",  1, "A card as a 1-sat NFT, sealed to its owner.", new[] { "sealCommitment" }),
        new Template(TxKind.Commitment,   "BSVP:CMT:1",  1, "A hash commitment published before a reveal.", new[] { "commitHash" }),
        new Template(TxKind.Reveal,       "BSVP:RVL:1",  1, "The reveal of a prior commitment.", new[] { "commitHash", "preimage" }),
        new Template(TxKind.ShuffleStage, "BSVP:SHF:1",  1, "One seat's masking+shuffle stage of the deal.", new[] { "handId", "step", "deck" }),
        new Template(TxKind.Deal,         "BSVP:DEAL:1", 1, "Dealing a card to a seat (per-position mask reveal).", new[] { "handId", "position", "mask" }),
        new Template(TxKind.BoardReveal,  "BSVP:BRD:1",  1, "A community-board card revealed by all seats.", new[] { "handId", "street", "mask" }),
        new Template(TxKind.Showdown,     "BSVP:SHO:1",  1, "Showdown reveal of a seat's hole cards.", new[] { "handId", "seat", "holeMasks" }),
        new Template(TxKind.Bet,          "BSVP:BET:1",  1, "A betting action committed on-chain.", new[] { "handId", "seat", "action", "amount" }),
        new Template(TxKind.PotEscrow,    "BSVP:POT:1",  1, "The pot in a threshold (n-of-n) escrow contract.", new[] { "handId", "members", "amount" }),
        new Template(TxKind.Settlement,   "BSVP:STL:1",  1, "Cooperative settlement paying the pot to the winner(s).", new[] { "handId", "winnerPub" }),
        new Template(TxKind.Recovery,     "BSVP:REC:1",  1, "A pre-signed nLockTime refund if a peer stalls.", new[] { "handId", "lockHeight" }),
        new Template(TxKind.Bid,          "BSVP:BID:1",  1, "A conditional bid in an on-chain auction.", new[] { "auctionId", "bidderPub", "amount", "commit" }),
        new Template(TxKind.Auction,      "BSVP:AUC:1",  1, "An auction genesis defining the item/role and rules.", new[] { "auctionId", "item", "reserve", "deadline" }),
        new Template(TxKind.RoleClaim,    "BSVP:ROLE:1", 1, "Claiming an auctioned role (banker, dealer, draw).", new[] { "auctionId", "role", "winnerPub" }),
        new Template(TxKind.TableGenesis, "BSVP:TBL:1",  1, "Creating a table (its genesis).", new[] { "tableId", "variant", "seats", "stakes" }),
        new Template(TxKind.GameStart,    "BSVP:GAME:1", 1, "Starting a game at a table.", new[] { "tableId", "gameId" }),
        new Template(TxKind.HandStart,    "BSVP:HAND:1", 1, "Starting a hand within a game.", new[] { "gameId", "handId", "button" }),
        new Template(TxKind.Announce,     "BSVP:ANN:1",  1, "A player announcing its public key and IP endpoint so peers auto-discover it (no manual key exchange).", new[] { "playerPub", "endpoint" }),
    }.ToDictionary(t => t.Kind);

    public static Template Of(TxKind kind) => Registry[kind];
    public static IReadOnlyCollection<Template> All => Registry.Values;
    public static byte[] Marker(TxKind kind) => Encoding.ASCII.GetBytes(Of(kind).Tag);

    /// <summary>
    /// Build a typed output script for a transaction of this kind:
    ///   &lt;marker&gt; OP_DROP  (&lt;field&gt; OP_DROP)*  &lt;ownerPub&gt; OP_CHECKSIG
    /// The field count MUST match the template's documented schema (no assumptions). Owner spends it.
    /// </summary>
    public static byte[] BuildOutput(TxKind kind, IReadOnlyList<byte[]> fields, byte[] ownerPub33)
    {
        var tpl = Of(kind);
        if (fields.Count != tpl.Fields.Length) throw new ArgumentException($"{kind} expects {tpl.Fields.Length} fields ({string.Join(",", tpl.Fields)}), got {fields.Count}");
        if (ownerPub33.Length != 33) throw new ArgumentException("ownerPub must be 33-byte compressed");
        var b = new List<byte>();
        PushDrop(b, Marker(kind));
        foreach (var f in fields) PushDrop(b, f);
        Push(b, ownerPub33); b.Add(OP_CHECKSIG);
        return b.ToArray();
    }

    public sealed record Parsed(TxKind Kind, byte[][] Fields, byte[] OwnerPub);

    /// <summary>Parse a typed output back to its kind, fields, and owner pubkey; null if not a typed output.</summary>
    public static Parsed? Parse(byte[] script)
    {
        try
        {
            int p = 0;
            var marker = ReadPush(script, ref p); if (marker == null || script[p++] != OP_DROP) return null;
            var tag = Encoding.ASCII.GetString(marker);
            TxKind? kind = null; foreach (var t in Registry.Values) if (t.Tag == tag) { kind = t.Kind; break; }
            if (kind == null) return null;
            var fields = new List<byte[]>();
            while (true)
            {
                int save = p;
                var data = ReadPush(script, ref p); if (data == null) return null;
                if (p < script.Length && script[p] == OP_DROP) { p++; fields.Add(data); continue; }
                // not followed by OP_DROP → this push is the owner pubkey, then OP_CHECKSIG
                if (data.Length == 33 && p < script.Length && script[p] == OP_CHECKSIG && p + 1 == script.Length)
                    return Of(kind.Value).Fields.Length == fields.Count ? new Parsed(kind.Value, fields.ToArray(), data) : null;
                return null;
            }
        }
        catch { return null; }
    }

    // MINIMAL push encoding (BSV consensus MINIMALDATA): empty → OP_0; a single byte 1..16 → OP_1..OP_16; a
    // single 0x81 → OP_1NEGATE; otherwise the shortest length-prefixed push. Non-minimal pushes are rejected
    // by the network ("Data push larger than necessary"), so typed outputs MUST be encoded this way to spend.
    private static void Push(List<byte> b, byte[] d)
    {
        if (d.Length == 0) { b.Add(OP_0); return; }
        if (d.Length == 1 && d[0] >= 1 && d[0] <= 16) { b.Add((byte)(0x50 + d[0])); return; } // OP_1..OP_16
        if (d.Length == 1 && d[0] == 0x81) { b.Add(OP_1NEGATE); return; }
        if (d.Length < OP_PUSHDATA1) b.Add((byte)d.Length);
        else if (d.Length <= 0xff) { b.Add(OP_PUSHDATA1); b.Add((byte)d.Length); }
        else { b.Add(OP_PUSHDATA2); b.Add((byte)(d.Length & 0xff)); b.Add((byte)(d.Length >> 8)); }
        b.AddRange(d);
    }
    private static void PushDrop(List<byte> b, byte[] d) { Push(b, d); b.Add(OP_DROP); }
    private static byte[]? ReadPush(byte[] s, ref int p)
    {
        if (p >= s.Length) return null;
        byte op = s[p++];
        if (op == OP_0) return Array.Empty<byte>();
        if (op == OP_1NEGATE) return new byte[] { 0x81 };
        if (op >= 0x51 && op <= 0x60) return new byte[] { (byte)(op - 0x50) }; // OP_1..OP_16
        int len;
        if (op < OP_PUSHDATA1) len = op;
        else if (op == OP_PUSHDATA1) { if (p >= s.Length) return null; len = s[p++]; }
        else if (op == OP_PUSHDATA2) { if (p + 2 > s.Length) return null; len = s[p] | s[p + 1] << 8; p += 2; }
        else return null;
        if (len < 0 || p + len > s.Length) return null;
        var d = s[p..(p + len)]; p += len; return d;
    }
}
