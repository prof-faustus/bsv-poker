using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using BsvPoker.Core;
using BsvPoker.Crypto;

namespace BsvPoker.Net;

/// <summary>
/// A networked N-player poker hand over the P2P table channel — NO server, NO dealer, and with TRUE
/// per-card privacy. Every peer runs this identically. They cooperatively build a commutative-encryption
/// deck (<see cref="MentalPokerEC"/>): in seat order each player masks+shuffles the deck, then in seat
/// order each re-masks with independent per-card scalars. A card is dealt by every OTHER player revealing
/// only that position's scalar, so a player learns ONLY their own hole cards; the board is revealed per
/// street by all players, and opponents' hole cards only at showdown. The shared <see cref="HoldemState"/>
/// (deferred-showdown mode) adjudicates betting identically on every peer, including multiway side pots.
/// The seat count is carried in the table id ("t-&lt;hex&gt;~&lt;Variant&gt;~p&lt;N&gt;", default 2).
/// Raises OnUpdate on the node thread (the UI marshals to the dispatcher).
/// </summary>
public sealed class NetGame
{
    public enum Phase { WaitingForPlayer, Dealing, Playing, Done }

    private readonly P2PNode _node;
    private readonly string _table;
    private readonly string _myPubHex;
    private readonly int _seatCount;
    private Action? _unsub;
    private System.Threading.Timer? _ticker;
    private readonly object _gate = new();

    private readonly ConcurrentDictionary<string, byte> _players = new();

    // --- commutative-encryption deal state ---
    private readonly IReadOnlyList<Card> _cardSet;
    private readonly int _n;
    private readonly byte[] _ecGlobal;                       // my global shuffle mask c
    private readonly byte[][] _ecPerCard;                    // my per-card masks d[0..n)
    private readonly int[] _ecPerm;                          // my secret shuffle permutation
    private readonly Dictionary<int, byte[][]> _shuf = new();// step -> shuffle-masked deck after that seat
    private readonly Dictionary<int, byte[][]> _rem = new(); // step -> re-masked deck after that seat
    private byte[][]? _final;                                // = _rem[N-1]
    // pos -> (seat -> that seat's per-card mask at pos), gathered from holeD / showD / boardD reveals
    private readonly Dictionary<int, Dictionary<int, byte[]>> _maskShares = new();
    private readonly HashSet<int> _boardStreetsSupplied = new();

    public Phase State { get; private set; } = Phase.WaitingForPlayer;
    public int MySeat { get; private set; } = -1;
    public string[] SeatPubs { get; private set; } = Array.Empty<string>();
    public HoldemState? Hand { get; private set; }
    public string Status { get; private set; } = "Waiting for players to join…";
    public event Action? OnUpdate;
    public Variant Variant { get; }
    public int SeatCount => _seatCount;

    private int HoleCount => Variants.HoleCards(Variant);
    private int BoardStart => HoleCount * _seatCount;
    private IEnumerable<int> HolePositions(int seat) => Enumerable.Range(seat * HoleCount, HoleCount);
    private IEnumerable<int> OtherSeatsHolePositions() => Enumerable.Range(0, _seatCount).Where(s => s != MySeat).SelectMany(HolePositions);

    public NetGame(P2PNode node, string tableId, byte[] myPub)
    {
        _node = node; _table = tableId; _myPubHex = Convert.ToHexString(myPub).ToLowerInvariant();
        _players[_myPubHex] = 1;
        var parts = tableId.Split('~');
        Variant = parts.Length > 1 ? Variants.Parse(parts[1]) : Variant.TexasHoldem;
        _seatCount = 2;
        for (int i = 2; i < parts.Length; i++)
            if (parts[i].StartsWith("p", StringComparison.Ordinal) && int.TryParse(parts[i][1..], out var pc) && pc is >= 2 and <= 10) _seatCount = pc;
        _cardSet = Variants.CardSet(Variant);
        _n = _cardSet.Count;
        _ecGlobal = MentalPokerEC.NewScalar();
        _ecPerCard = MentalPokerEC.NewPerCardScalars(_n);
        _ecPerm = RandomPerm(_n);
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
        _ticker = new System.Threading.Timer(_ => Tick(), null, 0, 500);
    }

    public void Stop() { _ticker?.Dispose(); _unsub?.Invoke(); }

    private void Send(object o) => _ = _node.PublishAsync(_table, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o)));

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
                if (SeatPubs.Length != _seatCount) { TryAssignSeats(); return; }
                DriveDeal();
                DriveStreet();
                Broadcast();   // all periodic sending happens here (once per tick) to avoid a message storm
            }
        }
        catch { }
    }

    private void Broadcast()
    {
        if (Hand == null)
        {
            if (_shuf.TryGetValue(MySeat, out var myShuf)) Send(new { t = "shuf", step = MySeat, pts = PtsHex(myShuf) });
            if (_rem.TryGetValue(MySeat, out var myRem)) Send(new { t = "rem", step = MySeat, pts = PtsHex(myRem) });
        }
        // reveal my masks at every OTHER seat's hole positions so each of them can read their own cards
        if (_final != null) Send(new { t = "holeD", seat = MySeat, d = ScalarMap(OtherSeatsHolePositions()) });
        if (Hand is { AwaitingBoard: true })
        {
            var positions = Enumerable.Range(BoardStart + Hand.Board.Count, Hand.PendingBoardCount);
            Send(new { t = "boardD", seat = MySeat, d = ScalarMap(positions) });
        }
        if (Hand is { AwaitingShowdown: true }) Send(new { t = "showD", seat = MySeat, d = ScalarMap(HolePositions(MySeat)) });
    }

    private void TryAssignSeats()
    {
        if (SeatPubs.Length == _seatCount) return;
        if (_players.Count < _seatCount) { Status = $"Waiting for players… ({_players.Count}/{_seatCount})"; return; }
        SeatPubs = _players.Keys.OrderBy(x => x, StringComparer.Ordinal).Take(_seatCount).ToArray();
        MySeat = Array.IndexOf(SeatPubs, _myPubHex);
        State = Phase.Dealing;
        Status = $"Table full — you are seat {MySeat}. Shuffling the deck (encrypted)…";
        Raise();
    }

    // The deal is a two-pass pipeline in seat order: pass 1 each seat shuffle-masks, pass 2 each seat
    // re-masks. A seat produces its stage as soon as the previous seat's stage is in hand; messages are
    // re-broadcast each tick until the next stage appears, so a dropped frame self-heals.
    private void DriveDeal()
    {
        if (Hand != null || MySeat < 0) return;
        if (!_shuf.ContainsKey(MySeat))
        {
            byte[][]? input = MySeat == 0 ? MentalPokerEC.BaseDeck(_n) : (_shuf.TryGetValue(MySeat - 1, out var prev) ? prev : null);
            if (input != null) _shuf[MySeat] = MentalPokerEC.ShuffleMask(input, _ecGlobal, _ecPerm);
        }
        if (!_rem.ContainsKey(MySeat) && _shuf.ContainsKey(_seatCount - 1)) // re-mask only after the full shuffle exists
        {
            byte[][]? input = MySeat == 0 ? _shuf[_seatCount - 1] : (_rem.TryGetValue(MySeat - 1, out var prev) ? prev : null);
            if (input != null) _rem[MySeat] = MentalPokerEC.Remask(input, _ecGlobal, _ecPerCard);
        }
        if (_final == null && _rem.TryGetValue(_seatCount - 1, out var fin)) _final = fin;
        TryCreateHand();
    }

    /// <summary>Combine all seats' masks at a position (mine + the gathered shares) to recover the card; null if not yet possible.</summary>
    private Card? TryUnmask(int pos)
    {
        if (_final == null) return null;
        var masks = new List<byte[]>(_seatCount);
        for (int s = 0; s < _seatCount; s++)
        {
            if (s == MySeat) masks.Add(_ecPerCard[pos]);
            else if (_maskShares.TryGetValue(pos, out var m) && m.TryGetValue(s, out var d)) masks.Add(d);
            else return null;
        }
        int idx = MentalPokerEC.Identify(MentalPokerEC.Unmask(_final[pos], masks), _n);
        return idx < 0 ? null : _cardSet[idx];
    }

    private void TryCreateHand()
    {
        if (Hand != null || _final == null || MySeat < 0) return;
        var myCards = new Card[HoleCount];
        int k = 0;
        foreach (var p in HolePositions(MySeat))
        {
            var c = TryUnmask(p);
            if (c == null) return; // still need other seats' masks at my hole positions
            myCards[k++] = c.Value;
        }
        var deck = new Card[HoleCount * _seatCount];
        for (int i = 0; i < deck.Length; i++) deck[i] = Card.FaceDown; // every other seat's holes stay face-down
        k = 0; foreach (var p in HolePositions(MySeat)) deck[p] = myCards[k++];
        var stacks = Enumerable.Repeat(100L, _seatCount).ToArray();
        Hand = HoldemState.Create(stacks, button: 0, sb: 1, bb: 2, deck, Variant, deferShowdown: true);
        State = Phase.Playing;
        Status = "Dealt. " + Hand.Message;
        Raise();
    }

    private void DriveStreet()
    {
        if (Hand == null) return;
        if (Hand.AwaitingBoard)
        {
            var positions = Enumerable.Range(BoardStart + Hand.Board.Count, Hand.PendingBoardCount).ToList();
            int key = Hand.Board.Count; // distinct per street by board cards already dealt (0,3,4)
            var cards = positions.Select(TryUnmask).ToList();
            if (cards.All(c => c != null) && _boardStreetsSupplied.Add(key))
            {
                Hand.SupplyBoard(cards.Select(c => c!.Value).ToList());
                Status = Hand.Message; Raise();
                DriveStreet(); // a forced all-in runout may immediately need the next street/showdown
            }
        }
        else if (Hand.AwaitingShowdown)
        {
            // unmask every live opponent's hole cards once all required masks (incl. their own showD) are in
            var live = Hand.Seats.Where(s => !s.Folded && s.Seat != MySeat).ToList();
            var revealed = new Dictionary<int, Card[]>();
            foreach (var t in live)
            {
                var hs = HolePositions(t.Seat).Select(TryUnmask).ToList();
                if (hs.Any(c => c == null)) return; // wait for more reveals
                revealed[t.Seat] = hs.Select(c => c!.Value).ToArray();
            }
            foreach (var (seat, cards) in revealed) Hand.SetRevealedHole(seat, cards);
            Hand.CompleteShowdown();
            State = Phase.Done; Status = Hand.Message; Raise();
        }
    }

    private void OnMessage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            switch (root.GetProperty("t").GetString())
            {
                case "hello":
                    var pub = root.GetProperty("pub").GetString();
                    if (pub != null && _players.TryAdd(pub, 1)) { lock (_gate) TryAssignSeats(); }
                    break;
                case "shuf": lock (_gate) { _shuf.TryAdd(root.GetProperty("step").GetInt32(), PtsFrom(root.GetProperty("pts"))); DriveDeal(); } break;
                case "rem": lock (_gate) { _rem.TryAdd(root.GetProperty("step").GetInt32(), PtsFrom(root.GetProperty("pts"))); DriveDeal(); } break;
                case "holeD": lock (_gate) { MergeShares(root.GetProperty("seat").GetInt32(), root.GetProperty("d")); DriveDeal(); } break;
                case "showD": lock (_gate) { MergeShares(root.GetProperty("seat").GetInt32(), root.GetProperty("d")); DriveStreet(); } break;
                case "boardD": lock (_gate) { MergeShares(root.GetProperty("seat").GetInt32(), root.GetProperty("d")); DriveStreet(); } break;
                case "act": lock (_gate) ApplyRemote(root); break;
            }
        }
        catch { }
    }

    private void MergeShares(int seat, JsonElement dMap)
    {
        if (seat < 0 || seat >= _seatCount || seat == MySeat) return; // I already hold my own masks
        foreach (var kv in dMap.EnumerateObject())
        {
            int pos = int.Parse(kv.Name);
            if (!_maskShares.TryGetValue(pos, out var m)) { m = new(); _maskShares[pos] = m; }
            m[seat] = Convert.FromHexString(kv.Value.GetString()!);
        }
    }

    private int _applied;
    private void ApplyRemote(JsonElement root)
    {
        if (Hand == null || Hand.Complete) return;
        if (root.GetProperty("seq").GetInt32() != _applied) return;
        int seat = root.GetProperty("seat").GetInt32();
        if (seat != Hand.ToAct) return;
        var kind = Enum.Parse<ActionKind>(root.GetProperty("kind").GetString()!);
        long amt = root.GetProperty("amount").GetInt64();
        try { Hand.Apply(new GameAction(kind, seat, amt)); _applied++; Status = Hand.Message; if (Hand.Complete) State = Phase.Done; Raise(); DriveStreet(); }
        catch { }
    }

    /// <summary>Called by the UI when it is MY turn. Applies locally and broadcasts to the peers.</summary>
    public void Act(ActionKind kind, long amount)
    {
        lock (_gate)
        {
            if (Hand == null || Hand.Complete || Hand.ToAct != MySeat) return;
            int seq = _applied;
            try { Hand.Apply(new GameAction(kind, MySeat, amount)); _applied++; }
            catch (Exception ex) { Status = ex.Message; Raise(); return; }
            Send(new { t = "act", seat = MySeat, seq, kind = kind.ToString(), amount });
            Status = Hand.Message;
            if (Hand.Complete) State = Phase.Done;
            Raise();
            DriveStreet();
        }
    }

    private void Raise() { try { OnUpdate?.Invoke(); } catch { } }
}
