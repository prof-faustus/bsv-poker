using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BsvPoker.Core;
using BsvPoker.Core.Games;
using BsvPoker.Crypto;

namespace BsvPoker.Net;

/// <summary>
/// MULTIPLAYER (group) Blackjack over the P2P table channel — NO server, NO single dealer, real per-deal
/// fairness. N players (2..6) join a table like poker; the deck is the dealerless mental-poker deal
/// (<see cref="MentalPokerEC"/> + hiding <see cref="RevealProof"/> commitments). The "dealer" is computed
/// jointly: its cards come from the shared deck, revealed to everyone — its HOLE card stays sealed until every
/// player has finished, then it plays to 17. Each player plays their own hand (hit/stand/double). The result is
/// adjudicated identically on every node by <see cref="GroupBlackjack"/>; the pot is the n-of-n
/// <see cref="BlackjackPot"/>. Every message is signed + table/seat-bound (same transport as NetGame).
///
/// Card layout in the shared deck: player p holes = positions 2p, 2p+1; dealer up = 2N; dealer hole = 2N+1;
/// draw pile = 2N+2.. Cards are public (blackjack is face-up) EXCEPT the dealer hole, which no one can read
/// until its scalar is revealed at dealer-play. A position opens when ALL seats reveal their per-card scalar.
/// </summary>
public sealed class NetBlackjack
{
    public enum Phase { WaitingForPlayer, Dealing, Playing, DealerPlay, Done }

    private readonly P2PNode _node;
    private readonly string _table;
    private readonly byte[] _priv;
    private readonly string _myPubHex;
    private readonly int _seatCount;
    private readonly long _bet;
    private Action? _unsub;
    private System.Threading.Timer? _ticker;
    private readonly object _gate = new();

    private readonly ConcurrentDictionary<string, byte> _players = new();

    // anti-grind seating (same as NetGame)
    private string[]? _seatCandidates;
    private byte[] _seatNonce = Array.Empty<byte>();
    private readonly ConcurrentDictionary<string, byte[]> _seatCommits = new();
    private readonly ConcurrentDictionary<string, byte[]> _seatReveals = new();
    private bool _sentSeatCommit, _sentSeatReveal;

    private string[]? _seats;                 // fixed seat order
    private int _mySeat = -1;
    private readonly IReadOnlyList<Card> _cardSet;
    private readonly int _n;

    // deal material
    private byte[] _ecGlobal = Array.Empty<byte>();
    private byte[][] _ecPerCard = Array.Empty<byte[]>();
    private byte[][] _ecNonce = Array.Empty<byte[]>();
    private int[] _ecPerm = Array.Empty<int>();
    private readonly Dictionary<int, byte[][]> _shuf = new();
    private readonly Dictionary<int, byte[][]> _rem = new();
    private readonly Dictionary<int, byte[][]> _comm = new();
    private byte[][]? _final;
    private readonly Dictionary<int, Dictionary<int, byte[]>> _maskShares = new();   // pos -> seat -> scalar
    private readonly Dictionary<int, Card> _opened = new();                           // pos -> revealed card
    private bool _sentShuf, _sentRem;

    // game state (replicated identically on every node)
    private List<Card>[] _hands = Array.Empty<List<Card>>();
    private readonly List<Card> _dealer = new();
    private bool[] _done = Array.Empty<bool>();
    private bool[] _doubled = Array.Empty<bool>();
    private long[] _betOf = Array.Empty<long>();
    private int _toAct = -1;
    private int _drawNext;                    // next draw-pile position
    private int _applied;                     // seq of applied player actions
    private long[] _net = Array.Empty<long>();
    private BjOutcome[] _outcome = Array.Empty<BjOutcome>();

    public Phase State { get; private set; } = Phase.WaitingForPlayer;
    public int MySeat => _mySeat;
    public string[] SeatPubs => _seats ?? Array.Empty<string>();
    public IReadOnlyList<Card> MyHand => _mySeat >= 0 && _hands.Length > _mySeat ? _hands[_mySeat] : Array.Empty<Card>();
    public IReadOnlyList<Card> DealerCards => _dealer;
    public int ToAct => _toAct;
    public bool Complete => State == Phase.Done;
    public IReadOnlyList<long> Net => _net;
    public IReadOnlyList<BjOutcome> Outcomes => _outcome;
    public string Status { get; private set; } = "Waiting for players to join…";
    public event Action? OnUpdate;
    public int Rejected { get; private set; }
    public bool CheatDetected { get; private set; }

    private int Seats => _seats?.Length ?? 0;
    private int DealerUp => 2 * Seats;
    private int DealerHole => 2 * Seats + 1;

    public NetBlackjack(P2PNode node, string tableId, byte[] myPriv, byte[] myPub)
    {
        _node = node; _table = tableId; _priv = myPriv; _myPubHex = Convert.ToHexString(myPub).ToLowerInvariant();
        _players[_myPubHex] = 1;
        var parts = tableId.Split('~');
        _seatCount = 2; _bet = 10;
        for (int i = 1; i < parts.Length; i++)
        {
            var seg = parts[i];
            if (seg.StartsWith("p", StringComparison.Ordinal) && int.TryParse(seg[1..], out var pc) && pc is >= 2 and <= 6) _seatCount = pc;
            else if (seg.StartsWith("b", StringComparison.Ordinal) && long.TryParse(seg[1..], out var b) && b is >= 1 and <= 1_000_000_000) _bet = b;
        }
        _cardSet = Variants.CardSet(Variant.TexasHoldem);   // a standard 52-card deck
        _n = _cardSet.Count;
    }

    public void Start() { _unsub = _node.Subscribe(_table, OnMessage); _ticker = new System.Threading.Timer(_ => Tick(), null, 0, 120); }
    public void Stop() { _ticker?.Dispose(); _unsub?.Invoke(); }

    private void Raise() { try { OnUpdate?.Invoke(); } catch { } }
    private void Reject(string w) { Rejected++; }

    // ---- signed transport (identical to NetGame) ----
    private void Send(object o, bool ephemeral = false)
    {
        var json = JsonSerializer.Serialize(o);
        using var doc = JsonDocument.Parse(json);
        var digest = Digest(doc.RootElement);
        var sig = Convert.ToHexString(Secp256k1.SignDigest(_priv, digest)).ToLowerInvariant();
        var node = JsonNode.Parse(json)!.AsObject();
        node["pub"] = _myPubHex; node["sig"] = sig;
        var payload = Encoding.UTF8.GetBytes(node.ToJsonString());
        if (ephemeral) { _ = _node.PublishAsync(_table, payload); return; }
        var fid = new byte[33 + digest.Length];
        Convert.FromHexString(_myPubHex).CopyTo(fid, 0); digest.CopyTo(fid, 33);
        _ = _node.PublishAsync(_table, payload, Convert.ToHexString(Hashes.Sha256(fid)).ToLowerInvariant());
    }
    private byte[] Digest(JsonElement msg) { var sb = new StringBuilder(_table); sb.Append('|'); Canon(msg, sb); return Hashes.Sha256d(Encoding.UTF8.GetBytes(sb.ToString())); }
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
                sb.Append('['); bool f = true; foreach (var i in e.EnumerateArray()) { if (!f) sb.Append(','); f = false; Canon(i, sb); } sb.Append(']'); break;
            case JsonValueKind.String: sb.Append('"').Append(e.GetString()).Append('"'); break;
            default: sb.Append(e.GetRawText()); break;
        }
    }
    private bool Verify(JsonElement root, out string pub)
    {
        pub = root.TryGetProperty("pub", out var pe) ? pe.GetString() ?? "" : "";
        if (pub.Length != 66 || !root.TryGetProperty("sig", out var se)) return false;
        try { return Secp256k1.VerifyDigest(Convert.FromHexString(pub), Digest(root), Convert.FromHexString(se.GetString()!)); } catch { return false; }
    }

    private static string[] PtsHex(byte[][] p) => p.Select(x => Convert.ToHexString(x).ToLowerInvariant()).ToArray();
    private static byte[][] PtsFrom(JsonElement a) => a.EnumerateArray().Select(e => Convert.FromHexString(e.GetString()!)).ToArray();

    private void Tick()
    {
        try
        {
            lock (_gate)
            {
                Send(new { t = "hello", pub = _myPubHex }, ephemeral: true);
                if (_seats == null) { TryAssignSeats(); return; }
                if (State == Phase.Done) return;
                DriveDeal();
                DriveReveals();
                DriveGame();
            }
        }
        catch { }
    }

    // ---- anti-grind seating (same construction as NetGame) ----
    private void SendSeatCommit() { if (_seatCommits.TryGetValue(_myPubHex, out var c)) Send(new { t = "seatcommit", c = Convert.ToHexString(c).ToLowerInvariant() }); }
    private void SendSeatReveal() { if (_seatReveals.TryGetValue(_myPubHex, out var n)) Send(new { t = "seatreveal", n = Convert.ToHexString(n).ToLowerInvariant() }); }
    private void TryAssignSeats()
    {
        if (_seats != null) return;
        if (_players.Count < _seatCount) { Status = $"Waiting for players… ({_players.Count}/{_seatCount})"; return; }
        _seatCandidates ??= _players.Keys.OrderBy(x => x, StringComparer.Ordinal).Take(_seatCount).ToArray();
        var cands = _seatCandidates;
        if (Array.IndexOf(cands, _myPubHex) < 0) { Status = $"Table is full ({_seatCount}/{_seatCount})."; return; }
        if (!_sentSeatCommit) { _seatNonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32); _seatCommits[_myPubHex] = SeatOrder.Commit(_seatNonce); _sentSeatCommit = true; }
        SendSeatCommit();
        bool allC = cands.All(p => _seatCommits.ContainsKey(p));
        if (allC) { if (!_sentSeatReveal) { _seatReveals[_myPubHex] = _seatNonce; _sentSeatReveal = true; } SendSeatReveal(); }
        if (allC && cands.All(p => _seatReveals.ContainsKey(p)))
        {
            var reveals = cands.Select(p => (Convert.FromHexString(p), _seatReveals[p])).ToList();
            var order = SeatOrder.Assign(cands.Select(Convert.FromHexString).ToList(), SeatOrder.JointSeed(reveals));
            _seats = order.Select(i => cands[i]).ToArray();
            _mySeat = Array.IndexOf(_seats, _myPubHex);
            int s = Seats;
            _hands = Enumerable.Range(0, s).Select(_ => new List<Card>()).ToArray();
            _done = new bool[s]; _doubled = new bool[s]; _net = new long[s]; _outcome = new BjOutcome[s];
            _betOf = Enumerable.Repeat(_bet, s).ToArray();
            _drawNext = 2 * s + 2;
            _ecGlobal = MentalPokerEC.NewScalar(); _ecPerCard = MentalPokerEC.NewPerCardScalars(_n); _ecPerm = RandomPerm(_n);
            State = Phase.Dealing; Status = $"Hand starting — you are seat {_mySeat}. Shuffling…"; Raise();
        }
        else { int c = cands.Count(p => _seatCommits.ContainsKey(p)), r = cands.Count(p => _seatReveals.ContainsKey(p)); Status = $"Agreeing a fair seat order… commits {c}/{_seatCount}, reveals {r}/{_seatCount}"; }
    }
    private static int[] RandomPerm(int n) { var p = Enumerable.Range(0, n).ToArray(); for (int i = n - 1; i > 0; i--) { int j = (int)System.Security.Cryptography.RandomNumberGenerator.GetInt32(i + 1); (p[i], p[j]) = (p[j], p[i]); } return p; }

    // ---- the dealerless deal: shuffle + remask + hiding commitments (same as NetGame) ----
    private void SendShuf() { if (_shuf.TryGetValue(_mySeat, out var p)) Send(new { t = "shuf", step = _mySeat, pts = PtsHex(p) }); }
    private void SendRem() { if (_rem.TryGetValue(_mySeat, out var p)) Send(new { t = "rem", step = _mySeat, pts = PtsHex(p) }); }
    private void SendComm() { if (_comm.TryGetValue(_mySeat, out var c)) Send(new { t = "comm", seat = _mySeat, c = PtsHex(c) }); }
    private void DriveDeal()
    {
        if (_seats == null || State != Phase.Dealing) return;
        if (!_shuf.ContainsKey(_mySeat))
        {
            byte[][]? input = _mySeat == 0 ? MentalPokerEC.BaseDeck(_n) : (_shuf.TryGetValue(_mySeat - 1, out var prev) ? prev : null);
            if (input != null) _shuf[_mySeat] = MentalPokerEC.ShuffleMask(input, _ecGlobal, _ecPerm);
        }
        if (_shuf.ContainsKey(_mySeat) && !_sentShuf) { _sentShuf = true; SendShuf(); }
        if (!_rem.ContainsKey(_mySeat) && _shuf.ContainsKey(Seats - 1))
        {
            byte[][]? input = _mySeat == 0 ? _shuf[Seats - 1] : (_rem.TryGetValue(_mySeat - 1, out var prev) ? prev : null);
            if (input != null)
            {
                _rem[_mySeat] = MentalPokerEC.Remask(input, _ecGlobal, _ecPerCard);
                var comms = new byte[_n][]; _ecNonce = new byte[_n][];
                for (int k = 0; k < _n; k++) { var (cc, r) = RevealProof.Commit(_ecPerCard[k]); comms[k] = cc; _ecNonce[k] = r; }
                _comm[_mySeat] = comms; SendComm();
            }
        }
        if (_rem.ContainsKey(_mySeat) && !_sentRem) { _sentRem = true; SendRem(); }
        if (_final == null && _rem.TryGetValue(Seats - 1, out var fin)) { _final = fin; State = Phase.Playing; Status = "Dealing the cards…"; }
        SendComm();
    }

    private readonly HashSet<int> _everNeeded = new();

    // Accumulate every position that has ever been needed: at deal, all player cards + dealer up; every draw a
    // player or the dealer takes; and the dealer HOLE only once the dealer plays — so no one can read it early.
    // Positions stay in the set so reveals keep flowing until every node has opened them (no dropped-frame stall).
    private void UpdateNeeded()
    {
        if (_seats == null) return;
        for (int k = 0; k <= DealerUp; k++) if (k < _n) _everNeeded.Add(k);   // 2N player cards + dealer up
        foreach (var pos in _pendingDraw.Keys) if (pos < _n) _everNeeded.Add(pos);
        if ((State == Phase.DealerPlay || State == Phase.Done) && DealerHole < _n) _everNeeded.Add(DealerHole);
    }

    private void DriveReveals()
    {
        if (_final == null || _mySeat < 0) return;
        UpdateNeeded();
        if (_everNeeded.Count == 0) return;
        var map = new Dictionary<string, string[]>();
        foreach (var p in _everNeeded)
        {
            map[p.ToString()] = new[] { Convert.ToHexString(_ecPerCard[p]).ToLowerInvariant(), Convert.ToHexString(_ecNonce[p]).ToLowerInvariant() };
            if (!_maskShares.TryGetValue(p, out var mm)) { mm = new(); _maskShares[p] = mm; } mm[_mySeat] = _ecPerCard[p];   // record my own share
        }
        Send(new { t = "reveal", seat = _mySeat, d = map });   // re-sent each tick (stable id ⇒ deduped; grows as draws happen)
        TryOpen();
    }

    private Card? Unmask(int pos)
    {
        if (_final == null) return null;
        var masks = new List<byte[]>(Seats);
        for (int s = 0; s < Seats; s++) { if (_maskShares.TryGetValue(pos, out var m) && m.TryGetValue(s, out var d)) masks.Add(d); else return null; }
        int idx = MentalPokerEC.Identify(MentalPokerEC.Unmask(_final[pos], masks), _n);
        return idx < 0 ? null : _cardSet[idx];
    }
    private void TryOpen()
    {
        foreach (var p in _everNeeded.Where(p => p < _n && !_opened.ContainsKey(p)).ToList())
        { var c = Unmask(p); if (c != null) _opened[p] = c.Value; }
    }

    private void DriveGame()
    {
        if (_final == null) return;
        UpdateNeeded(); TryOpen(); ResolvePendingDraws();
        if (State == Phase.Playing)
        {
            // need all player holes + dealer up opened before play begins
            for (int k = 0; k <= DealerUp; k++) if (!_opened.ContainsKey(k)) return;
            if (_hands[0].Count == 0)   // first time everything is open: build hands + dealer up
            {
                for (int s = 0; s < Seats; s++) { _hands[s].Add(_opened[2 * s]); _hands[s].Add(_opened[2 * s + 1]); }
                _dealer.Add(_opened[DealerUp]);
                for (int s = 0; s < Seats; s++) if (Blackjack.IsBlackjack(_hands[s])) _done[s] = true;
                _toAct = NextToAct(0);
                Status = _toAct < 0 ? "All naturals — dealer plays." : $"Hand dealt. Seat {_toAct} to act.";
                Raise();
                if (_toAct < 0) State = Phase.DealerPlay;
            }
        }
        if (State == Phase.DealerPlay) DriveDealer();
    }

    private int NextToAct(int from) { for (int i = from; i < Seats; i++) if (!_done[i]) return i; return -1; }

    /// <summary>The local player acts (called by the UI / bot driver on their turn).</summary>
    public void Act(BjAction a)
    {
        lock (_gate)
        {
            if (State != Phase.Playing || _toAct != _mySeat) return;
            int seq = _applied;
            ApplyAction(_mySeat, a);
            Send(new { t = "act", seq, seat = _mySeat, a = (int)a });
        }
    }

    private void ApplyAction(int seat, BjAction a)
    {
        switch (a)
        {
            case BjAction.Hit:
                RequestDraw(seat);
                break;
            case BjAction.Double:
                _betOf[seat] *= 2; _doubled[seat] = true; RequestDraw(seat); _done[seat] = true;
                break;
            case BjAction.Stand:
                _done[seat] = true;
                break;
        }
        _applied++;
        AdvanceTurn();
    }

    // Hitting consumes the next draw position; it opens via the normal reveal path. We add the card once opened.
    private readonly Dictionary<int, int> _pendingDraw = new();   // position -> seat awaiting it
    private void RequestDraw(int seat) { int pos = _drawNext++; _pendingDraw[pos] = seat; }

    private void AdvanceTurn()
    {
        // resolve any opened pending draws into hands first
        ResolvePendingDraws();
        if (State != Phase.Playing) return;
        if (_toAct >= 0 && !_done[_toAct]) { Raise(); return; }   // same seat keeps acting (after a hit)
        _toAct = NextToAct(_toAct < 0 ? 0 : _toAct + 1);
        if (_toAct < 0) { State = Phase.DealerPlay; Status = "All players done — dealer plays."; }
        Raise();
    }

    private void ResolvePendingDraws()
    {
        TryOpen();
        // Only PLAYER draws (seat >= 0) are resolved into player hands here; the DEALER's draws (seat == -1) are
        // consumed by DriveDealer — adding them here would index _hands[-1] and throw, freezing the dealer.
        foreach (var kv in _pendingDraw.Where(kv => kv.Value >= 0 && _opened.ContainsKey(kv.Key)).ToList())
        {
            int pos = kv.Key, seat = kv.Value; _hands[seat].Add(_opened[pos]); _pendingDraw.Remove(pos);
            if (Blackjack.Value(_hands[seat]).Total > 21) { _outcome[seat] = BjOutcome.PlayerBust; _done[seat] = true; }
        }
    }

    private void DriveDealer()
    {
        ResolvePendingDraws();
        UpdateNeeded(); TryOpen();
        if (!_opened.ContainsKey(DealerHole)) return;          // dealer hole must be revealed first
        if (_dealer.Count < 2) _dealer.Add(_opened[DealerHole]);
        // draw to 17, opening one position at a time (each is revealed by all over the next ticks)
        while (Blackjack.Value(_dealer).Total < 17)
        {
            int pos = _drawNext;
            if (!_opened.ContainsKey(pos)) { _pendingDraw[pos] = -1; return; }   // mark needed, wait for the reveal
            _dealer.Add(_opened[pos]); _pendingDraw.Remove(pos); _drawNext++;
        }
        Settle();
    }

    private void Settle()
    {
        int d = Blackjack.Value(_dealer).Total; bool dbj = Blackjack.IsBlackjack(_dealer);
        for (int s = 0; s < Seats; s++)
        {
            long bet = _betOf[s];
            if (_outcome[s] == BjOutcome.PlayerBust) { _net[s] = -bet; continue; }
            int p = Blackjack.Value(_hands[s]).Total; bool pbj = Blackjack.IsBlackjack(_hands[s]);
            if (pbj && dbj) { _outcome[s] = BjOutcome.Push; _net[s] = 0; }
            else if (pbj) { _outcome[s] = BjOutcome.PlayerBlackjack; _net[s] = bet * 3 / 2; }
            else if (dbj) { _outcome[s] = BjOutcome.DealerWin; _net[s] = -bet; }
            else if (d > 21) { _outcome[s] = BjOutcome.DealerBust; _net[s] = bet; }
            else if (p > d) { _outcome[s] = BjOutcome.PlayerWin; _net[s] = bet; }
            else if (d > p) { _outcome[s] = BjOutcome.DealerWin; _net[s] = -bet; }
            else { _outcome[s] = BjOutcome.Push; _net[s] = 0; }
        }
        State = Phase.Done; _toAct = -1;
        Status = "Hand complete. " + string.Join("  ", Enumerable.Range(0, Seats).Select(s => $"P{s}:{_outcome[s]}({(_net[s] >= 0 ? "+" : "")}{_net[s]})"));
        Raise();
    }

    private void OnMessage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var t = root.GetProperty("t").GetString();
            if (!Verify(root, out var pub)) { Reject($"sig ({t})"); return; }
            if (t == "hello") { if (_players.TryAdd(pub, 1)) { lock (_gate) TryAssignSeats(); } return; }
            if (t == "seatcommit") { try { var c = Convert.FromHexString(root.GetProperty("c").GetString()!); if (c.Length == 32 && _seatCommits.TryAdd(pub, c)) { lock (_gate) TryAssignSeats(); } } catch { } return; }
            if (t == "seatreveal") { try { var n = Convert.FromHexString(root.GetProperty("n").GetString()!); if (n.Length == 32 && _seatCommits.TryGetValue(pub, out var c) && SeatOrder.VerifyReveal(c, n) && _seatReveals.TryAdd(pub, n)) { lock (_gate) TryAssignSeats(); } } catch { } return; }
            lock (_gate)
            {
                int SeatOf(string p) => _seats == null ? -1 : Array.IndexOf(_seats, p);
                switch (t)
                {
                    case "shuf": { int s = root.GetProperty("step").GetInt32(); if (s == SeatOf(pub)) { _shuf.TryAdd(s, PtsFrom(root.GetProperty("pts"))); DriveDeal(); } break; }
                    case "rem": { int s = root.GetProperty("step").GetInt32(); if (s == SeatOf(pub)) { _rem.TryAdd(s, PtsFrom(root.GetProperty("pts"))); DriveDeal(); } break; }
                    case "comm": { int s = root.GetProperty("seat").GetInt32(); if (s == SeatOf(pub) && !_comm.ContainsKey(s)) { var c = PtsFrom(root.GetProperty("c")); if (c.Length == _n && c.All(x => x.Length == 32)) { _comm.TryAdd(s, c); DriveDeal(); DriveGame(); } } break; }
                    case "reveal": { int s = root.GetProperty("seat").GetInt32(); if (s == SeatOf(pub)) { MergeShares(s, root.GetProperty("d")); DriveGame(); } break; }
                    case "act": { int s = root.GetProperty("seat").GetInt32(); if (s == SeatOf(pub) && s == _toAct && State == Phase.Playing && root.GetProperty("seq").GetInt32() == _applied) ApplyAction(s, (BjAction)root.GetProperty("a").GetInt32()); break; }
                }
            }
        }
        catch { }
    }

    private void MergeShares(int seat, JsonElement dMap)
    {
        if (seat < 0 || seat >= Seats || seat == _mySeat) return;
        if (!_comm.TryGetValue(seat, out var comm)) return;
        foreach (var kv in dMap.EnumerateObject())
        {
            if (!int.TryParse(kv.Name, out int pos) || pos < 0 || pos >= comm.Length) continue;
            if (_maskShares.TryGetValue(pos, out var have) && have.ContainsKey(seat)) continue;
            byte[] scalar, nonce;
            try { if (kv.Value.ValueKind != JsonValueKind.Array || kv.Value.GetArrayLength() != 2) continue; scalar = Convert.FromHexString(kv.Value[0].GetString()!); nonce = Convert.FromHexString(kv.Value[1].GetString()!); } catch { continue; }
            if (!RevealProof.Verify(scalar, nonce, comm[pos])) { CheatDetected = true; Reject("reveal !open commitment"); continue; }
            if (!_maskShares.TryGetValue(pos, out var m)) { m = new(); _maskShares[pos] = m; }
            m[seat] = scalar;
        }
    }
}
