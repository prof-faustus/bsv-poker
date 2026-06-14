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
    private string[]? _sessionSeats;          // the admitted players, in FAIR (joint-randomness) seat order
    private readonly Dictionary<string, long> _stacks = new();
    private int _handNo = -1;

    // ANTI-GRINDING SEAT ASSIGNMENT (audit fix): seats are NOT ordered by sorted public key (which a player
    // could grind by generating many identity keys to land on the button / in late position). Instead the
    // candidate players run a COMMIT-REVEAL: each commits a random nonce, then reveals it; a joint seed derived
    // from EVERY revealed nonce decides the order (SeatOrder). No one can predict or bias their seat. See
    // SeatOrder.cs. The candidate SET is the deterministic sorted-pubkey first-N (so all peers agree who is
    // seating); only the ORDER within it is randomised — that is where the positional edge lives.
    private string[]? _seatCandidates;
    private byte[] _seatNonce = Array.Empty<byte>();
    private readonly ConcurrentDictionary<string, byte[]> _seatCommits = new();
    private readonly ConcurrentDictionary<string, byte[]> _seatReveals = new();
    private bool _sentSeatCommit, _sentSeatReveal;

    // per-hand state (reset at the start of each hand)
    private string[] _inPubs = Array.Empty<string>();   // players still holding chips, this hand
    private int _myHandSeat = -1;
    private int _handButton;
    private readonly IReadOnlyList<Card> _cardSet;
    private readonly int _n;
    private byte[] _ecGlobal = Array.Empty<byte>();
    private byte[][] _ecPerCard = Array.Empty<byte[]>();
    private byte[][] _ecNonce = Array.Empty<byte[]>();   // per-position blinding nonces for the hiding reveal commitments
    private int[] _ecPerm = Array.Empty<int>();
    private readonly Dictionary<int, byte[][]> _shuf = new();
    private readonly Dictionary<int, byte[][]> _rem = new();
    private readonly Dictionary<int, byte[][]> _comm = new();   // seat → per-position commitment C_k = d_k·G (full deck)
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

    /// <summary>One in-game MOVE, surfaced so the app can turn it into a funded on-chain n-of-n move tx and
    /// dual-path broadcast it (IP-to-IP + nodes). DEFAULT null ⇒ no on-chain emission (the simulation/stress
    /// engine is unchanged). The live game wires this to make every move a real transaction.</summary>
    public sealed record MoveRecord(int HandNo, string Kind, int Seat, string Action, long Amount, bool Mine, IReadOnlyList<(string Pub, long Stack)>? Stacks);
    public event Action<MoveRecord>? OnMove;
    private void RaiseMove(MoveRecord m) { try { OnMove?.Invoke(m); } catch { } }
    public Variant Variant { get; }
    public int SeatCount => _seatCount;
    public int HandNumber => _handNo;
    public long TableChips => _stacks.Values.Sum();
    public bool Eliminated { get; private set; }

    // Accountable abort: if the DEAL or a REVEAL makes NO PROGRESS within this window (a peer is genuinely
    // withholding card material), the hand is aborted instead of hanging forever. This timer covers ONLY the
    // automated deal/reveal exchange — it is NEVER running while it is a human's turn to act (betting has no
    // timeout at all; take all the time you want, the whole game can run for hours). Every new piece of deal
    // state RESETS this clock (see _progressTick in OnMessage), so a slow-but-advancing deal never aborts.
    // The window is deliberately generous (this is an ONLINE game, not a speed round): a full minute and a
    // half of total silence from a peer before the deal is abandoned.
    public int AbortMs { get; set; } = 90_000;
    public bool Aborted { get; private set; }
    /// <summary>Set true the moment a peer reveals a card-scalar that does NOT open its published commitment
    /// (a provable card-substitution attempt). The offending reveal is rejected and the hand stalls to abort.</summary>
    public bool CheatDetected { get; private set; }
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
    // The only deck positions ever revealed: every seat's hole cards + up to 5 community board cards. We
    // commit to exactly these (both sides derive the count identically from the seat count), so the
    // commitment payload stays small on the wire.
    private int RevealCount => Math.Min(_n, BoardStart + 5);
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
    private void Send(object o, bool ephemeral = false)
    {
        var json = JsonSerializer.Serialize(o);
        using var doc = JsonDocument.Parse(json);
        var digest = Digest(doc.RootElement);
        var sig = Convert.ToHexString(Secp256k1.SignDigest(_priv, digest)).ToLowerInvariant();
        var node = JsonNode.Parse(json)!.AsObject();
        node["pub"] = _myPubHex; node["sig"] = sig;
        var payload = Encoding.UTF8.GetBytes(node.ToJsonString());
        // EPHEMERAL (presence/"hello"): a FRESH id every time so it is ALWAYS delivered — never deduped. This is
        // essential for the join handshake: the HOST is already flooding hello before the JOINER subscribes to the
        // table topic, so the host's hello reaches the joiner's node and gets marked "seen" while there is still
        // no local subscriber (it is dropped). With a stable id the re-flood is then discarded at the seen-check
        // BEFORE delivery, so the joiner — now subscribed — would NEVER learn the host exists and the game would
        // never start ("Waiting for players 1/2" forever). A random id makes each beacon a new frame, so the very
        // next hello after the joiner subscribes is delivered. Presence is idempotent (TryAdd), so re-delivery is
        // free; only hello uses this path.
        if (ephemeral) { _ = _node.PublishAsync(_table, payload); return; }
        // A STABLE per-sender id for this logical message: hash(sender pubkey ‖ signing digest). The digest
        // covers the table + canonical content but EXCLUDES pub/sig, so two different players sending
        // structurally identical content (e.g. "hello") would otherwise collide and the second be wrongly
        // deduped — folding in the pubkey makes the id sender-unique. Re-broadcasts of the same message share
        // the id, so peers dedup them at the seen-check instead of re-verifying the signature every 120 ms —
        // the headroom that lets 5–6 player deals finish on the single-consumer transport.
        var fidBytes = new byte[33 + digest.Length];
        Convert.FromHexString(_myPubHex).CopyTo(fidBytes, 0); digest.CopyTo(fidBytes, 33);
        var fid = Convert.ToHexString(Hashes.Sha256(fidBytes)).ToLowerInvariant();
        _ = _node.PublishAsync(_table, payload, fid);
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
    // A reveal carries BOTH the scalar and its commitment nonce, so the verifier can open the hiding commitment
    // (SHA-256(d ‖ r)). pos -> [scalarHex, nonceHex].
    private Dictionary<string, string[]> ScalarMap(IEnumerable<int> positions) =>
        positions.ToDictionary(p => p.ToString(), p => new[] { Convert.ToHexString(_ecPerCard[p]).ToLowerInvariant(), Convert.ToHexString(_ecNonce[p]).ToLowerInvariant() });

    private void Tick()
    {
        try
        {
            lock (_gate)
            {
                Send(new { t = "hello", pub = _myPubHex }, ephemeral: true);   // presence beacon: fresh id, never deduped
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
        SendComm();   // persistent all hand, so every reveal can always be verified (idempotent, first-wins)
        if (Hand == null) { SendShuf(); SendRem(); }
        SendHoleD(); SendBoardD(); SendShowD();
    }

    private void SendShuf() { if (_shuf.TryGetValue(_myHandSeat, out var p)) Send(new { t = "shuf", h = _handNo, step = _myHandSeat, pts = PtsHex(p) }); }
    private void SendRem() { if (_rem.TryGetValue(_myHandSeat, out var p)) Send(new { t = "rem", h = _handNo, step = _myHandSeat, pts = PtsHex(p) }); }
    // Commitments (C_k = d_k·G) are broadcast on their OWN channel, persistently for the whole hand — NOT
    // gated on the dealing phase like the remask deck. A reveal can only be verified once the seat's
    // commitments are in hand, and reveals (hole/board/showdown) keep flowing all hand; so the commitments
    // must too, or a peer that dropped the early frame could never verify a later reveal (it would stall).
    // Commitments are on the DEAL'S CRITICAL PATH: a holeD reveal is only accepted once the sender's comm has
    // arrived, so the deal cannot finish until commitments propagate. They are small (only revealable
    // positions) — smaller than the shuf/rem frames already sent every tick during dealing — so we send them
    // every tick (idempotent, first-wins). Throttling them only delayed the deal and stalled it at 5–6 seats.
    private void SendComm() { if (_comm.TryGetValue(_myHandSeat, out var c)) Send(new { t = "comm", h = _handNo, seat = _myHandSeat, c = PtsHex(c) }); }
    private void SendHoleD() { if (_final != null) Send(new { t = "holeD", h = _handNo, seat = _myHandSeat, d = ScalarMap(OtherSeatsHolePositions()) }); }
    private void SendBoardD() { if (Hand is { AwaitingBoard: true }) Send(new { t = "boardD", h = _handNo, seat = _myHandSeat, d = ScalarMap(Enumerable.Range(BoardStart + Hand.Board.Count, Hand.PendingBoardCount)) }); }
    private void SendShowD() { if (Hand is { AwaitingShowdown: true }) Send(new { t = "showD", h = _handNo, seat = _myHandSeat, d = ScalarMap(HolePositions(_myHandSeat)) }); }

    private void SendSeatCommit() { if (_seatCommits.TryGetValue(_myPubHex, out var c)) Send(new { t = "seatcommit", c = Convert.ToHexString(c).ToLowerInvariant() }); }
    private void SendSeatReveal() { if (_seatReveals.TryGetValue(_myPubHex, out var n)) Send(new { t = "seatreveal", n = Convert.ToHexString(n).ToLowerInvariant() }); }

    // Drive the anti-grinding seating handshake, then start the session once a FAIR order is agreed. Called every
    // tick (and on each seatcommit/seatreveal) until _sessionSeats is set. Three steps, all idempotent:
    //   1. once enough players are present, fix the candidate set (deterministic sorted-pubkey first-N) and
    //      COMMIT my random nonce;
    //   2. once EVERY candidate's commit is in, REVEAL my nonce;
    //   3. once EVERY candidate's reveal is in (each verified against its commitment), derive the joint seed and
    //      assign the seat order from it — no player can have biased their position.
    private void TryAssignSeats()
    {
        if (_sessionSeats != null) return;
        if (_players.Count < _seatCount) { Status = $"Waiting for players… ({_players.Count}/{_seatCount})"; return; }

        // 1) fix the candidate set (all peers agree: the sorted-pubkey first-N present), then commit my nonce
        _seatCandidates ??= _players.Keys.OrderBy(x => x, StringComparer.Ordinal).Take(_seatCount).ToArray();
        var cands = _seatCandidates;
        if (Array.IndexOf(cands, _myPubHex) < 0) { Status = $"Table is full ({_seatCount}/{_seatCount})."; return; }
        if (!_sentSeatCommit)
        {
            _seatNonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            _seatCommits[_myPubHex] = SeatOrder.Commit(_seatNonce);
            _sentSeatCommit = true;
        }
        SendSeatCommit();

        // 2) when every candidate has committed, reveal my nonce
        bool allCommitted = cands.All(p => _seatCommits.ContainsKey(p));
        if (allCommitted)
        {
            if (!_sentSeatReveal) { _seatReveals[_myPubHex] = _seatNonce; _sentSeatReveal = true; }
            SendSeatReveal();
        }

        // 3) when every candidate has revealed, the joint seed decides the order — fair, ungrindable
        if (allCommitted && cands.All(p => _seatReveals.ContainsKey(p)))
        {
            var reveals = cands.Select(p => (Convert.FromHexString(p), _seatReveals[p])).ToList();
            var seed = SeatOrder.JointSeed(reveals);
            var order = SeatOrder.Assign(cands.Select(Convert.FromHexString).ToList(), seed); // candidate indices, in seat order
            _sessionSeats = order.Select(i => cands[i]).ToArray();
            foreach (var p in _sessionSeats) _stacks[p] = _startStack;
            _handNo = -1;
            StartHand();
        }
        else
        {
            int c = cands.Count(p => _seatCommits.ContainsKey(p)), r = cands.Count(p => _seatReveals.ContainsKey(p));
            Status = $"Agreeing a fair seat order (no grinding)… commits {c}/{_seatCount}, reveals {r}/{_seatCount}";
        }
    }

    // Begin the next hand: re-seat among players who still have chips, rotate the button, reset the deal.
    private void StartHand()
    {
        _handNo++;
        _inPubs = _sessionSeats!.Where(p => _stacks[p] > 0).ToArray();
        if (_inPubs.Length < 2) { SessionOver(); return; }
        _myHandSeat = Array.IndexOf(_inPubs, _myPubHex);
        if (_myHandSeat < 0)
        {
            // Not seated. Two different cases — say which, so a joiner never just sees "eliminated":
            //  • the table was already FULL when I arrived (I am not in the admitted set at all), or
            //  • I busted out mid-session (I was admitted, now have 0 chips).
            Eliminated = true; State = Phase.Done;
            bool everAdmitted = _sessionSeats != null && Array.IndexOf(_sessionSeats, _myPubHex) >= 0;
            Status = everAdmitted
                ? "You were eliminated. The remaining players continue."
                : $"This table is already full ({_seatCount}/{_seatCount}). Host your own table or open another to play.";
            Raise(); return;
        }
        _handButton = _handNo % _inPubs.Length;
        _ecGlobal = MentalPokerEC.NewScalar();
        _ecPerCard = MentalPokerEC.NewPerCardScalars(_n);
        _ecNonce = Array.Empty<byte[]>();   // built with the commitments at remask (DriveDeal)
        _ecPerm = RandomPerm(_n);
        _shuf.Clear(); _rem.Clear(); _comm.Clear(); _final = null; _maskShares.Clear(); _boardStreetsSupplied.Clear();
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
            if (input != null)
            {
                _rem[_myHandSeat] = MentalPokerEC.Remask(input, _ecGlobal, _ecPerCard);
                // PROVE-WHAT-IT-IS: publish a HIDING commitment for every scalar I will ever reveal (all hole
                // positions + the up-to-5 board positions). Hiding (SHA-256(d ‖ r), random nonce r) is essential:
                // a plain d·G commitment would let an observer read a hidden card via (cardIndex+1)·C — see
                // RevealProof. The nonce is kept per position and disclosed with the scalar at reveal time. Only
                // these positions are ever opened, so committing to the rest of the deck is wasted bandwidth.
                var comms = new byte[RevealCount][];
                _ecNonce = new byte[_n][];
                for (int k = 0; k < RevealCount; k++) { var (c, r) = RevealProof.Commit(_ecPerCard[k]); comms[k] = c; _ecNonce[k] = r; }
                _comm[_myHandSeat] = comms;
                SendComm();   // emit commitments the moment they exist — they gate every reveal that follows
            }
        }
        if (_rem.ContainsKey(_myHandSeat) && !_sentRem) { _sentRem = true; SendRem(); }
        if (_final == null && _rem.TryGetValue(HandSeats - 1, out var fin)) _final = fin;
        if (_final != null && !_sentHoleD) { _sentHoleD = true; SendHoleD(); }
        TryCreateHand();
    }

    /// <summary>A monotonic count of how much deal/hand state we hold — shuffles, remasks, the final deck, mask
    /// shares, board streets, and applied actions. It only grows as the hand advances, so a growth between two
    /// observations is genuine progress (used to distinguish a slow-but-advancing deal from a withholding peer).</summary>
    private long DealProgress()
        => _shuf.Count + _rem.Count + (_final != null ? 1 : 0)
         + _maskShares.Values.Sum(d => (long)d.Count) + _boardStreetsSupplied.Count + _applied
         + (Hand != null ? 1 : 0);

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
        RaiseMove(new MoveRecord(_handNo, "settle", -1, "settle", 0, false, _inPubs.Select(p => (p, _stacks[p])).ToList()));
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
            // ANTI-GRINDING SEATING (pre-hand, no hand number). The signer pub is the committer/revealer, so a
            // player can only commit/reveal for ITSELF — it cannot inject another player's nonce.
            if (t == "seatcommit")
            {
                try { var c = Convert.FromHexString(root.GetProperty("c").GetString()!); if (c.Length == 32 && _seatCommits.TryAdd(pub, c)) { lock (_gate) TryAssignSeats(); } }
                catch { Reject("seatcommit parse"); }
                return;
            }
            if (t == "seatreveal")
            {
                try
                {
                    var n = Convert.FromHexString(root.GetProperty("n").GetString()!);
                    // accept a reveal ONLY if it opens this player's prior commitment (no equivocation, no late nonce)
                    if (n.Length == 32 && _seatCommits.TryGetValue(pub, out var c) && SeatOrder.VerifyReveal(c, n) && _seatReveals.TryAdd(pub, n)) { lock (_gate) TryAssignSeats(); }
                    else if (!_seatCommits.ContainsKey(pub) || !SeatOrder.VerifyReveal(_seatCommits.GetValueOrDefault(pub) ?? Array.Empty<byte>(), n)) Reject("seatreveal does not open commitment");
                }
                catch { Reject("seatreveal parse"); }
                return;
            }
            // all other messages are tagged with the hand number; ignore stale frames from a finished hand
            if (!root.TryGetProperty("h", out var hEl) || hEl.GetInt32() != _handNo) return;
            lock (_gate)
            {
                if (hEl.GetInt32() != _handNo) return;
                var before = DealProgress();   // genuine new deal state (not a re-broadcast) counts as progress
                switch (t)
                {
                    case "shuf":
                        if (!SeatBound(root, "step", pub, out var s1)) return;
                        _shuf.TryAdd(s1, PtsFrom(root.GetProperty("pts"))); DriveDeal(); break;
                    case "rem":
                        if (!SeatBound(root, "step", pub, out var s2)) return;
                        _rem.TryAdd(s2, PtsFrom(root.GetProperty("pts"))); DriveDeal(); break;
                    case "comm":
                        // a seat's per-position HIDING reveal commitments (32-byte SHA-256, one per revealable
                        // position). First-wins (TryAdd): once a seat has committed it cannot change them, so it
                        // cannot retro-fit a substitution.
                        if (!SeatBound(root, "seat", pub, out var sc)) return;
                        if (_comm.ContainsKey(sc)) break;
                        var comm = PtsFrom(root.GetProperty("c"));
                        if (comm.Length != RevealCount || comm.Any(c => c.Length != 32)) { Reject("bad commitments"); break; }
                        _comm.TryAdd(sc, comm); DriveDeal(); DriveStreet(); break;
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
                // a multiway deal/reveal is CPU-heavy (EC crypto for many seats) and can take a while; as long as
                // NEW state keeps arriving the deal is making progress and must NOT be aborted. Abort only fires
                // when a peer genuinely withholds (no new state) for AbortMs.
                if (DealProgress() > before) _progressTick = Environment.TickCount64;
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
        // We can only accept a revealed scalar once the seat's commitments are in hand. If the rem (with its
        // commitments) has not yet arrived, drop the share now — it is re-broadcast every tick, so it will be
        // verified and accepted as soon as the commitments land. Never accept an UNVERIFIED scalar.
        if (!_comm.TryGetValue(seat, out var comm)) return;
        foreach (var kv in dMap.EnumerateObject())
        {
            if (!int.TryParse(kv.Name, out int pos) || pos < 0 || pos >= comm.Length) continue;
            // VERIFY ONCE: reveals are re-broadcast every tick (each with a fresh frame id, so the transport
            // does not dedup them). An already-accepted (seat,pos) share is final — skip it BEFORE the EC
            // check, or at high seat counts the consumer thread drowns re-verifying the same scalars and the
            // hand stalls. This is purely a no-op fast-path; it changes no accepted value.
            if (_maskShares.TryGetValue(pos, out var have) && have.ContainsKey(seat)) continue;
            // each reveal is [scalarHex, nonceHex] — both are needed to open the hiding commitment
            byte[] scalar, nonce;
            try
            {
                if (kv.Value.ValueKind != JsonValueKind.Array || kv.Value.GetArrayLength() != 2) continue;
                scalar = Convert.FromHexString(kv.Value[0].GetString()!);
                nonce = Convert.FromHexString(kv.Value[1].GetString()!);
            }
            catch { continue; }
            // PROVE-WHAT-IT-IS: the revealed (scalar, nonce) must open the hiding commitment this seat published
            // at remask. A forged scalar (a card substitution) cannot open it, so it is rejected here, provably.
            if (!RevealProof.Verify(scalar, nonce, comm[pos]))
            {
                CheatDetected = true;
                Reject($"reveal does not open commitment (seat {seat}, pos {pos}) — card substitution rejected");
                continue;
            }
            if (!_maskShares.TryGetValue(pos, out var m)) { m = new(); _maskShares[pos] = m; }
            m[seat] = scalar;
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
            RaiseMove(new MoveRecord(_handNo, "bet", seat, kind.ToString(), amt, false, null));
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
            RaiseMove(new MoveRecord(_handNo, "bet", _myHandSeat, kind.ToString(), amount, true, null));
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
