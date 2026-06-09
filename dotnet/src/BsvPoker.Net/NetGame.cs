using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BsvPoker.Core;
using BsvPoker.Crypto;

namespace BsvPoker.Net;

/// <summary>
/// A networked N-player poker SESSION over the P2P table channel — NO server, NO dealer, TRUE per-card
/// privacy, and continuous multi-hand play. Every peer runs this identically. Each HAND uses a fresh
/// commutative-encryption deal (<see cref="MentalPokerEC"/>): in seat order players mask+shuffle, then
/// re-mask with per-card scalars; a card is dealt by every OTHER player revealing only that position's
/// scalar, so a player learns ONLY its own hole cards (board revealed per street, opponents' holes only at
/// showdown). Between hands, stacks carry over, the button rotates, eliminated players (0 chips) drop out,
/// and the session ends when one player holds all the chips. The shared <see cref="HoldemState"/>
/// (deferred-showdown mode) adjudicates betting identically on every peer, including multiway side pots.
/// Seat count is carried in the table id ("t-&lt;hex&gt;~&lt;Variant&gt;~p&lt;N&gt;", default 2). Each
/// message is tagged with the hand number so stale frames from a finished hand are ignored.
/// (The transport is not yet authenticated — see the security docs.)
/// </summary>
public sealed class NetGame
{
    public enum Phase { WaitingForPlayer, Dealing, Playing, Done }

    private readonly P2PNode _node;
    private readonly string _table;
    private readonly byte[] _priv;
    private readonly string _myPubHex;
    private readonly int _seatCount;          // table capacity (admission target)
    private readonly long _startStack;        // starting chips per player (buy-in)
    private readonly long _bigBlind;          // big blind (small blind = bb/2, min 1)
    private Action? _unsub;
    private System.Threading.Timer? _ticker;
    private readonly object _gate = new();

    private readonly ConcurrentDictionary<string, byte> _players = new();

    // session state (persists across hands)
    private string[]? _sessionSeats;          // the admitted players, fixed order (sorted pubkey)
    private readonly Dictionary<string, long> _stacks = new();
    private int _handNo = -1;

    // per-hand state (reset at the start of each hand)
    private string[] _inPubs = Array.Empty<string>();   // players still holding chips, this hand
    private int _myHandSeat = -1;
    private int _handButton;
    private readonly IReadOnlyList<Card> _cardSet;
    private readonly int _n;
    private byte[] _ecGlobal = Array.Empty<byte>();
    private byte[][] _ecPerCard = Array.Empty<byte[]>();
    private int[] _ecPerm = Array.Empty<int>();
    private readonly Dictionary<int, byte[][]> _shuf = new();
    private readonly Dictionary<int, byte[][]> _rem = new();
    private byte[][]? _final;
    private readonly Dictionary<int, Dictionary<int, byte[]>> _maskShares = new();
    private readonly HashSet<int> _boardStreetsSupplied = new();
    private int _applied;
    private bool _handFinalized;
    private bool _sentShuf, _sentRem, _sentHoleD, _sentShowD;   // one-shot immediate-send guards (per hand)
    private readonly HashSet<int> _sentBoardKeys = new();

    public Phase State { get; private set; } = Phase.WaitingForPlayer;
    public int MySeat => _myHandSeat;
    public string[] SeatPubs => _inPubs;
    public HoldemState? Hand { get; private set; }
    public string Status { get; private set; } = "Waiting for players to join…";
    public event Action? OnUpdate;
    public Variant Variant { get; }
    public int SeatCount => _seatCount;
    public int HandNumber => _handNo;
    public long TableChips => _stacks.Values.Sum();
    public bool Eliminated { get; private set; }

    // Accountable abort: if the deal or a reveal makes no progress within this window (a peer is
    // withholding), the hand is aborted instead of hanging forever. With real funds this is where the
    // pre-signed nLockTime recovery would be broadcast so no satoshi is stranded.
    public int AbortMs { get; set; } = 30000;
    public bool Aborted { get; private set; }
    private long _progressTick = Environment.TickCount64;

    private readonly List<string> _handLog = new();
    /// <summary>A running log of completed hands (most recent last) — who won what.</summary>
    public IReadOnlyList<string> HandLog => _handLog;
    /// <summary>Session chip standings across all admitted players (fixed seat order), marking you and the busted.</summary>
    public string Standings => _sessionSeats == null ? "" :
        string.Join("    ", _sessionSeats.Select((p, i) => $"P{i}: {_stacks[p]}{(p == _myPubHex ? " (you)" : "")}{(_stacks[p] == 0 ? " ✗" : "")}"));

    private int HandSeats => _inPubs.Length;
    private int HoleCount => Variants.HoleCards(Variant);
    private int BoardStart => HoleCount * HandSeats;
    private IEnumerable<int> HolePositions(int seat) => Enumerable.Range(seat * HoleCount, HoleCount);
    private IEnumerable<int> OtherSeatsHolePositions() => Enumerable.Range(0, HandSeats).Where(s => s != _myHandSeat).SelectMany(HolePositions);

    public NetGame(P2PNode node, string tableId, byte[] myPriv, byte[] myPub)
    {
        _node = node; _table = tableId; _priv = myPriv; _myPubHex = Convert.ToHexString(myPub).ToLowerInvariant();
        _players[_myPubHex] = 1;
        var parts = tableId.Split('~');
        Variant = parts.Length > 1 ? Variants.Parse(parts[1]) : Variant.TexasHoldem;
        _seatCount = 2; _startStack = 100; _bigBlind = 2;
        for (int i = 2; i < parts.Length; i++)
        {
            var seg = parts[i];
            if (seg.StartsWith("p", StringComparison.Ordinal) && int.TryParse(seg[1..], out var pc) && pc is >= 2 and <= 10) _seatCount = pc;
            else if (seg.StartsWith("s", StringComparison.Ordinal) && long.TryParse(seg[1..], out var st) && st is >= 2 and <= 1_000_000_000) _startStack = st;
            else if (seg.StartsWith("b", StringComparison.Ordinal) && long.TryParse(seg[1..], out var bb) && bb is >= 2 and <= 1_000_000) _bigBlind = bb;
        }
        if (_bigBlind > _startStack) _bigBlind = _startStack; // a sane table
        _cardSet = Variants.CardSet(Variant);
        _n = _cardSet.Count;
    }

    private static int[] RandomPerm(int n)
    {
        var p = Enumerable.Range(0, n).ToArray();
        for (int i = n - 1; i > 0; i--) { int j = (int)System.Security.Cryptography.RandomNumberGenerator.GetInt32(i + 1); (p[i], p[j]) = (p[j], p[i]); }
        return p;
    }

    public void Start()
    {
        _unsub = _node.Subscribe(_table, OnMessage);
        _ticker = new System.Threading.Timer(_ => Tick(), null, 0, 120);
    }

    public void Stop() { _ticker?.Dispose(); _unsub?.Invoke(); }

    public int Rejected { get; private set; }
    public string? LastReject { get; private set; }
    private void Reject(string why) { Rejected++; LastReject = why; }

    // Every message is signed by the sender's identity key and bound to this table; the signature covers a
    // canonical (sorted-key) form of the message excluding the pub/sig fields, prefixed with the table id.
    private void Send(object o)
    {
        var json = JsonSerializer.Serialize(o);
        using var doc = JsonDocument.Parse(json);
        var sig = Convert.ToHexString(Secp256k1.SignDigest(_priv, Digest(doc.RootElement))).ToLowerInvariant();
        var node = JsonNode.Parse(json)!.AsObject();
        node["pub"] = _myPubHex; node["sig"] = sig;
        _ = _node.PublishAsync(_table, Encoding.UTF8.GetBytes(node.ToJsonString()));
    }

    private byte[] Digest(JsonElement msg)
    {
        var sb = new StringBuilder(_table); sb.Append('|'); Canon(msg, sb);
        return Hashes.Sha256d(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static void Canon(JsonElement e, StringBuilder sb)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{'); bool first = true;
                foreach (var p in e.EnumerateObject().Where(p => p.Name is not ("pub" or "sig")).OrderBy(p => p.Name, StringComparer.Ordinal))
                { if (!first) sb.Append(','); first = false; sb.Append(p.Name).Append('='); Canon(p.Value, sb); }
                sb.Append('}'); break;
            case JsonValueKind.Array:
                sb.Append('['); bool f = true;
                foreach (var i in e.EnumerateArray()) { if (!f) sb.Append(','); f = false; Canon(i, sb); }
                sb.Append(']'); break;
            case JsonValueKind.String: sb.Append('"').Append(e.GetString()).Append('"'); break;
            default: sb.Append(e.GetRawText()); break;
        }
    }

    /// <summary>Verify a message's signature and return the signer's pubkey hex; false if missing/invalid.</summary>
    private bool Verify(JsonElement root, out string pub)
    {
        pub = root.TryGetProperty("pub", out var pe) ? pe.GetString() ?? "" : "";
        if (pub.Length != 66 || !root.TryGetProperty("sig", out var se)) return false;
        try { return Secp256k1.VerifyDigest(Convert.FromHexString(pub), Digest(root), Convert.FromHexString(se.GetString()!)); }
        catch { return false; }
    }

    private static string[] PtsHex(byte[][] pts) => pts.Select(p => Convert.ToHexString(p).ToLowerInvariant()).ToArray();
    private static byte[][] PtsFrom(JsonElement arr) => arr.EnumerateArray().Select(e => Convert.FromHexString(e.GetString()!)).ToArray();
    private Dictionary<string, string> ScalarMap(IEnumerable<int> positions) => positions.ToDictionary(p => p.ToString(), p => Convert.ToHexString(_ecPerCard[p]).ToLowerInvariant());

    private void Tick()
    {
        try
        {
            lock (_gate)
            {
                Send(new { t = "hello", pub = _myPubHex });
                if (_sessionSeats == null) { TryAssignSeats(); return; }
                if (Eliminated || State == Phase.Done) return;
                // a deal or reveal that makes no progress within AbortMs means a peer is withholding → abort
                if (_myHandSeat >= 0 && (Hand == null || Hand.AwaitingBoard || Hand.AwaitingShowdown)
                    && Environment.TickCount64 - _progressTick > AbortMs) { Abort(); return; }
                DriveDeal();
                DriveStreet();
                Broadcast();
            }
        }
        catch { }
    }

    // Periodic re-broadcast of my current artifacts (fallback for a dropped frame). Immediate one-shot
    // sends in DriveDeal/DriveStreet handle the common case so the deal progresses at network latency.
    private void Broadcast()
    {
        if (_myHandSeat < 0) return;
        if (Hand == null) { SendShuf(); SendRem(); }
        SendHoleD(); SendBoardD(); SendShowD();
    }

    private void SendShuf() { if (_shuf.TryGetValue(_myHandSeat, out var p)) Send(new { t = "shuf", h = _handNo, step = _myHandSeat, pts = PtsHex(p) }); }
    private void SendRem() { if (_rem.TryGetValue(_myHandSeat, out var p)) Send(new { t = "rem", h = _handNo, step = _myHandSeat, pts = PtsHex(p) }); }
    private void SendHoleD() { if (_final != null) Send(new { t = "holeD", h = _handNo, seat = _myHandSeat, d = ScalarMap(OtherSeatsHolePositions()) }); }
    private void SendBoardD() { if (Hand is { AwaitingBoard: true }) Send(new { t = "boardD", h = _handNo, seat = _myHandSeat, d = ScalarMap(Enumerable.Range(BoardStart + Hand.Board.Count, Hand.PendingBoardCount)) }); }
    private void SendShowD() { if (Hand is { AwaitingShowdown: true }) Send(new { t = "showD", h = _handNo, seat = _myHandSeat, d = ScalarMap(HolePositions(_myHandSeat)) }); }

    private void TryAssignSeats()
    {
        if (_sessionSeats != null) return;
        if (_players.Count < _seatCount) { Status = $"Waiting for players… ({_players.Count}/{_seatCount})"; return; }
        _sessionSeats = _players.Keys.OrderBy(x => x, StringComparer.Ordinal).Take(_seatCount).ToArray();
        foreach (var p in _sessionSeats) _stacks[p] = _startStack;
        _handNo = -1;
        StartHand();
    }

    // Begin the next hand: re-seat among players who still have chips, rotate the button, reset the deal.
    private void StartHand()
    {
        _handNo++;
        _inPubs = _sessionSeats!.Where(p => _stacks[p] > 0).ToArray();
        if (_inPubs.Length < 2) { SessionOver(); return; }
        _myHandSeat = Array.IndexOf(_inPubs, _myPubHex);
        if (_myHandSeat < 0) { Eliminated = true; State = Phase.Done; Status = "You were eliminated. The remaining players continue."; Raise(); return; }
        _handButton = _handNo % _inPubs.Length;
        _ecGlobal = MentalPokerEC.NewScalar();
        _ecPerCard = MentalPokerEC.NewPerCardScalars(_n);
        _ecPerm = RandomPerm(_n);
        _shuf.Clear(); _rem.Clear(); _final = null; _maskShares.Clear(); _boardStreetsSupplied.Clear();
        _applied = 0; _handFinalized = false; Hand = null;
        _sentShuf = _sentRem = _sentHoleD = _sentShowD = false; _sentBoardKeys.Clear();
        State = Phase.Dealing;
        Status = $"Hand #{_handNo + 1} — you are seat {_myHandSeat}. Shuffling the deck (encrypted)…";
        Raise();
    }

    private void SessionOver()
    {
        State = Phase.Done;
        var winner = _stacks.FirstOrDefault(kv => kv.Value > 0).Key;
        Status = winner == _myPubHex ? $"You win the session with {_stacks[winner]} chips!" : "Session over — a player has won all the chips.";
        Raise();
    }

    private void DriveDeal()
    {
        if (Hand != null || _myHandSeat < 0) return;
        if (!_shuf.ContainsKey(_myHandSeat))
        {
            byte[][]? input = _myHandSeat == 0 ? MentalPokerEC.BaseDeck(_n) : (_shuf.TryGetValue(_myHandSeat - 1, out var prev) ? prev : null);
            if (input != null) _shuf[_myHandSeat] = MentalPokerEC.ShuffleMask(input, _ecGlobal, _ecPerm);
        }
        if (_shuf.ContainsKey(_myHandSeat) && !_sentShuf) { _sentShuf = true; SendShuf(); } // emit as soon as produced
        if (!_rem.ContainsKey(_myHandSeat) && _shuf.ContainsKey(HandSeats - 1))
        {
            byte[][]? input = _myHandSeat == 0 ? _shuf[HandSeats - 1] : (_rem.TryGetValue(_myHandSeat - 1, out var prev) ? prev : null);
            if (input != null) _rem[_myHandSeat] = MentalPokerEC.Remask(input, _ecGlobal, _ecPerCard);
        }
        if (_rem.ContainsKey(_myHandSeat) && !_sentRem) { _sentRem = true; SendRem(); }
        if (_final == null && _rem.TryGetValue(HandSeats - 1, out var fin)) _final = fin;
        if (_final != null && !_sentHoleD) { _sentHoleD = true; SendHoleD(); }
        TryCreateHand();
    }

    private Card? TryUnmask(int pos)
    {
        if (_final == null) return null;
        var masks = new List<byte[]>(HandSeats);
        for (int s = 0; s < HandSeats; s++)
        {
            if (s == _myHandSeat) masks.Add(_ecPerCard[pos]);
            else if (_maskShares.TryGetValue(pos, out var m) && m.TryGetValue(s, out var d)) masks.Add(d);
            else return null;
        }
        int idx = MentalPokerEC.Identify(MentalPokerEC.Unmask(_final[pos], masks), _n);
        return idx < 0 ? null : _cardSet[idx];
    }

    private void TryCreateHand()
    {
        if (Hand != null || _final == null || _myHandSeat < 0) return;
        var myCards = new Card[HoleCount];
        int k = 0;
        foreach (var p in HolePositions(_myHandSeat))
        {
            var c = TryUnmask(p);
            if (c == null) return;
            myCards[k++] = c.Value;
        }
        var deck = new Card[HoleCount * HandSeats];
        for (int i = 0; i < deck.Length; i++) deck[i] = Card.FaceDown;
        k = 0; foreach (var p in HolePositions(_myHandSeat)) deck[p] = myCards[k++];
        var stacks = _inPubs.Select(p => _stacks[p]).ToArray();
        Hand = HoldemState.Create(stacks, button: _handButton, sb: Math.Max(1, _bigBlind / 2), bb: _bigBlind, deck, Variant, deferShowdown: true);
        State = Phase.Playing;
        Status = $"Hand #{_handNo + 1} dealt. " + Hand.Message;
        Raise();
        if (Hand.Complete) FinalizeHand(); // a hand can be instantly over only in degenerate cases
    }

    private void DriveStreet()
    {
        if (Hand == null) return;
        if (Hand.AwaitingBoard)
        {
            var positions = Enumerable.Range(BoardStart + Hand.Board.Count, Hand.PendingBoardCount).ToList();
            int key = Hand.Board.Count;
            if (_sentBoardKeys.Add(key)) SendBoardD(); // emit my masks for this street's board once
            var cards = positions.Select(TryUnmask).ToList();
            if (cards.All(c => c != null) && _boardStreetsSupplied.Add(key))
            {
                Hand.SupplyBoard(cards.Select(c => c!.Value).ToList());
                Status = Hand.Message; Raise();
                if (Hand.Complete) { FinalizeHand(); return; }
                DriveStreet();
            }
        }
        else if (Hand.AwaitingShowdown)
        {
            if (!_sentShowD) { _sentShowD = true; SendShowD(); } // reveal my own holes at showdown
            var live = Hand.Seats.Where(s => !s.Folded && s.Seat != _myHandSeat).ToList();
            var revealed = new Dictionary<int, Card[]>();
            foreach (var t in live)
            {
                var hs = HolePositions(t.Seat).Select(TryUnmask).ToList();
                if (hs.Any(c => c == null)) return;
                revealed[t.Seat] = hs.Select(c => c!.Value).ToArray();
            }
            foreach (var (seat, cards) in revealed) Hand.SetRevealedHole(seat, cards);
            Hand.CompleteShowdown();
            Status = Hand.Message; Raise();
            FinalizeHand();
        }
    }

    // Record results back into the session stacks, then start the next hand (or end the session).
    private void FinalizeHand()
    {
        if (_handFinalized || Hand is not { Complete: true }) return;
        _handFinalized = true;
        for (int i = 0; i < _inPubs.Length; i++) _stacks[_inPubs[i]] = Hand.Seats[i].Stack;
        _handLog.Add($"Hand #{_handNo + 1}: {Hand.Message}");
        if (_handLog.Count > 100) _handLog.RemoveAt(0);
        StartHand();
    }

    private void OnMessage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var t = root.GetProperty("t").GetString();
            // AUTHENTICATION: every message must carry a valid signature by its claimed identity key.
            if (!Verify(root, out var pub)) { Reject($"bad/missing signature ({t})"); return; }
            if (t == "hello")
            {
                // the valid signature is proof of possession of this identity key
                if (_players.TryAdd(pub, 1)) { lock (_gate) TryAssignSeats(); }
                return;
            }
            // all other messages are tagged with the hand number; ignore stale frames from a finished hand
            if (!root.TryGetProperty("h", out var hEl) || hEl.GetInt32() != _handNo) return;
            lock (_gate)
            {
                if (hEl.GetInt32() != _handNo) return;
                switch (t)
                {
                    case "shuf":
                        if (!SeatBound(root, "step", pub, out var s1)) return;
                        _shuf.TryAdd(s1, PtsFrom(root.GetProperty("pts"))); DriveDeal(); break;
                    case "rem":
                        if (!SeatBound(root, "step", pub, out var s2)) return;
                        _rem.TryAdd(s2, PtsFrom(root.GetProperty("pts"))); DriveDeal(); break;
                    case "holeD":
                        if (!SeatBound(root, "seat", pub, out var s3)) return;
                        MergeShares(s3, root.GetProperty("d")); DriveDeal(); break;
                    case "showD":
                        if (!SeatBound(root, "seat", pub, out var s4)) return;
                        MergeShares(s4, root.GetProperty("d")); DriveStreet(); break;
                    case "boardD":
                        if (!SeatBound(root, "seat", pub, out var s5)) return;
                        MergeShares(s5, root.GetProperty("d")); DriveStreet(); break;
                    case "act":
                        if (!SeatBound(root, "seat", pub, out _)) return;
                        ApplyRemote(root); break;
                }
            }
        }
        catch { }
    }

    // SEAT BINDING: the message's claimed seat must be the seat the signing key actually holds this hand,
    // so a peer cannot act, reveal, or shuffle on behalf of any seat but its own.
    private bool SeatBound(JsonElement root, string field, string pub, out int seat)
    {
        seat = root.TryGetProperty(field, out var se) ? se.GetInt32() : -1;
        if (seat < 0 || seat >= HandSeats || _inPubs[seat] != pub) { Reject($"seat/pub mismatch ({root.GetProperty("t").GetString()})"); return false; }
        return true;
    }

    private void MergeShares(int seat, JsonElement dMap)
    {
        if (seat < 0 || seat >= HandSeats || seat == _myHandSeat) return;
        foreach (var kv in dMap.EnumerateObject())
        {
            int pos = int.Parse(kv.Name);
            if (!_maskShares.TryGetValue(pos, out var m)) { m = new(); _maskShares[pos] = m; }
            m[seat] = Convert.FromHexString(kv.Value.GetString()!);
        }
    }

    private void ApplyRemote(JsonElement root)
    {
        if (root.GetProperty("h").GetInt32() != _handNo) return;
        if (Hand == null || Hand.Complete) return;
        if (root.GetProperty("seq").GetInt32() != _applied) return;
        int seat = root.GetProperty("seat").GetInt32();
        if (seat != Hand.ToAct) return;
        var kind = Enum.Parse<ActionKind>(root.GetProperty("kind").GetString()!);
        long amt = root.GetProperty("amount").GetInt64();
        try
        {
            Hand.Apply(new GameAction(kind, seat, amt)); _applied++; Status = Hand.Message; Raise();
            if (Hand.Complete) FinalizeHand(); else DriveStreet();
        }
        catch { }
    }

    /// <summary>Called by the UI when it is MY turn. Applies locally and broadcasts to the peers.</summary>
    public void Act(ActionKind kind, long amount)
    {
        lock (_gate)
        {
            if (Hand == null || Hand.Complete || Hand.ToAct != _myHandSeat) return;
            int seq = _applied;
            try { Hand.Apply(new GameAction(kind, _myHandSeat, amount)); _applied++; }
            catch (Exception ex) { Status = ex.Message; Raise(); return; }
            Send(new { t = "act", h = _handNo, seat = _myHandSeat, seq, kind = kind.ToString(), amount });
            Status = Hand.Message; Raise();
            if (Hand.Complete) FinalizeHand(); else DriveStreet();
        }
    }

    private void Abort()
    {
        Aborted = true; State = Phase.Done;
        Status = "Hand aborted — a player stalled (no progress within the timeout). With real funds the nLockTime recovery applies.";
        Raise();
    }

    private void Raise() { _progressTick = Environment.TickCount64; try { OnUpdate?.Invoke(); } catch { } }
}
