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
    // WaitingForPlayer → (seat) → Funding → Dealing → Playing → DealerPlay → HandOver → (re-deal) → Dealing …
    //   → (session ends) → Settling → Done.
    // Funding: every player escrows their real-token stake into the shared n-of-n pot before any card is dealt.
    // HandOver is the brief pause after a hand settles before the NEXT hand is dealt — the table runs hand after
    // hand until someone leaves. Settling: all players co-sign the on-chain n-of-n payout to final standings.
    // Done is terminal: the session is over and the pot has been settled on-chain.
    public enum Phase { WaitingForPlayer, Funding, Dealing, Playing, DealerPlay, HandOver, Settling, Done }

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

    private string[]? _roster;                // the table roster — every player who took a seat (fixed for the session)
    private string[]? _seats;                 // the ACTIVE seats for the current hand (roster minus those who left/busted)
    private int _mySeat = -1;
    private readonly IReadOnlyList<Card> _cardSet;
    private readonly int _n;

    // CONTINUOUS PLAY (a real table deals hand after hand until you leave). Each hand is numbered and every
    // per-hand message carries that number, so a stale frame from a previous hand can never be applied to a new
    // one (and an "act seq 0" from hand 2 cannot be deduped against the identical-looking one from hand 1).
    private int _handNo = -1;
    private long _handOverAt;                  // Environment.TickCount64 when the current hand settled
    /// <summary>Pause AFTER a hand settles before the next is dealt, so everyone can read who won/lost. Default 10s.</summary>
    public long HandPauseMs { get; set; } = 10_000;

    // Bankroll per player (keyed by identity pubkey) carried across hands; the cash you walk away with on Leave.
    private long _buyIn;
    private long _dealerBankroll;              // the HOUSE bankroll (real tokens) — wins what players lose, pays what they win
    private readonly ConcurrentDictionary<string, long> _bankroll = new();
    // A signed leave takes effect at a hand boundary: a player who announces "after hand k" plays hands ≤ k and is
    // excluded from k+1 on. The explicit number makes the active set identical on every node regardless of timing.
    private readonly ConcurrentDictionary<string, int> _leaveAfter = new();
    private bool _leaving;

    // ===== ON-CHAIN n-of-n POT (real tokens at risk) =====
    // Before any card is dealt, every player ESCROWS their stake (personal buy-in + an equal share of the house
    // bankroll) into the SAME n-of-n pot script — one real on-chain coin each. When the session ends, all players
    // co-sign ONE settlement tx spending every pot coin to the final standings (conserved to the satoshi). The app
    // injects the wallet hooks; in headless mode (no hook) the table runs on tracked bankroll only (no real coins).
    public sealed record PotCoin(string Txid, uint Vout, long Value, string RawHex = "");
    /// <summary>Escrow <c>value</c> sat into the given n-of-n script from the wallet and return the created (already
    /// broadcast) pot coin. Null ⇒ no real pot (bankroll-only). Set by the app.</summary>
    public Func<byte[], long, PotCoin?>? FundPot;
    /// <summary>Broadcast the fully co-signed settlement tx on-chain. Set by the app.</summary>
    public Action<byte[]>? OnSettlementTx;
    /// <summary>Broadcast the pre-signed nLockTime REFUND if the cooperative settlement stalls (a griefer). Set by the app.</summary>
    public Action<byte[]>? OnRecoveryTx;
    /// <summary>ASK THE MINER: given a funding tx (hex), return true iff a miner ACCEPTS it (first-seen) — i.e. those
    /// inputs really back THIS pot coin. A double-spend (a conflicting tx already at the miner) returns false. Set by
    /// the app; when null (headless tests) only the structural check applies.</summary>
    public Func<string, bool>? VerifyFundedOnChain;
    private readonly ConcurrentDictionary<string, byte> _potVerified = new();   // funder pubhex -> miner confirmed first-seen
    private readonly ConcurrentDictionary<string, byte> _potChecking = new();   // a miner check is in flight
    private readonly ConcurrentDictionary<string, byte> _potDoubleSpent = new();// funder pubhex -> miner REJECTED (double-spend)
    /// <summary>The block height the refund unlocks at — set by the app to (current tip + ~30 days). 0 = no lock (tests).</summary>
    public uint RecoveryLockHeight { get; set; }
    public long PotFee { get; set; } = 1;
    private byte[][] _potPubs = Array.Empty<byte[]>();        // roster identity pubs (seat order) = the n-of-n keys
    private byte[] _potScript = Array.Empty<byte>();
    private readonly ConcurrentDictionary<string, PotCoin> _potIns = new();   // funder pubhex -> their escrowed coin (INITIAL per-player funding)
    private List<BlackjackPot.PotIn> _pot = new();                             // the CURRENT pot coins (after funding; replaced by one coin after a split)
    private int _potGen;                                                       // pot generation — bumped on each leaver-split so stale co-sigs can't cross
    private bool _postSplit;                                                   // the current pot came from a split (already funded; only re-co-sign the recovery)
    private bool _isSplit;                                                     // the in-flight co-signed tx is a leaver-split (pay leaver + re-escrow), not a final settlement
    private string[]? _splitRemaining;                                         // who continues after the in-flight split
    private bool _funded;
    private bool _potReady;   // escrow confirmed + refund co-signed — the end-of-session payout can now be on-chain
    private bool _settleStarted, _settleDone;
    private Chain.Tx? _settleTx;
    private List<BlackjackPot.PotIn> _settleInputs = new();
    private readonly ConcurrentDictionary<string, string[]> _settleSigs = new();   // signer pubhex -> per-input sig hex
    private long _settlingAt;                                // when settlement began (grace before broadcasting the refund)
    private const long SettleGraceMs = 30_000;              // if not all co-sign within this, broadcast the pre-signed refund
    // pre-signed nLockTime REFUND safety net (co-signed at funding, before any card): unwinds the pot to each
    // player's stake if the cooperative settlement ever stalls. No stake can be stranded by a griefer.
    private Chain.Tx? _recoveryTx;
    private readonly ConcurrentDictionary<string, string[]> _recoverySigs = new();
    private byte[]? _recoveryRaw;
    private bool _recoveryBroadcast;
    private bool PotActive => FundPot != null;               // a real on-chain pot is in play
    private long MyStake => _buyIn * 2;                      // personal buy-in + an equal share of the house bankroll
    public long PotValue => _pot.Count > 0 ? _pot.Sum(c => c.Value) : _potIns.Values.Sum(c => c.Value);
    public bool PotSettled => _settleDone;
    /// <summary>True once the on-chain pot is fully secured in the background (all stakes escrowed + miner-confirmed +
    /// refund co-signed) — only then can the end-of-session payout be on-chain. The game plays regardless.</summary>
    public bool PotReady => _potReady;
    /// <summary>The fully co-signed nLockTime refund (the safety net) once assembled at funding; null until then.</summary>
    public byte[]? RecoveryRaw => _recoveryRaw;
    /// <summary>The n-of-n pot keys in their canonical (roster/seat) order — the order the settlement is signed in.</summary>
    public IReadOnlyList<byte[]> PotPubs => _potPubs;
    /// <summary>Diagnostic snapshot for tests.</summary>
    public string DebugState => $"{State}/gen{_potGen}/pot{PotValue}/seats{SeatPubs.Length}/over{SessionOver}";

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
    public string[] SeatPubs => _seats ?? _roster ?? Array.Empty<string>();
    /// <summary>The current hand number (1-based for display); -1 before the first hand.</summary>
    public int HandNumber => _handNo + 1;
    /// <summary>The cash you currently hold at the table (buy-in ± every hand's result) — what you cash out on Leave.</summary>
    public long MyBankroll => _bankroll.TryGetValue(_myPubHex, out var b) ? b : 0;
    /// <summary>The HOUSE/dealer bankroll in real tokens — it wins what players lose and pays what they win. The
    /// table closes if it cannot cover the players (a real money game can't run a house that can't pay).</summary>
    public long DealerBankroll => _dealerBankroll;
    /// <summary>True while waiting for a card you drew (Hit/Double) to be revealed by all players — you cannot act
    /// again until you SEE it, so you can never blow past a bust by clicking fast.</summary>
    public bool AwaitingMyCard { get { lock (_gate) return _mySeat >= 0 && _pendingDraw.ContainsValue(_mySeat); } }
    /// <summary>True once the whole session is over (you left, or fewer than two players remain).</summary>
    public bool SessionOver => State == Phase.Done;
    /// <summary>True after you have asked to leave; you finish the current hand, then cash out.</summary>
    public bool Leaving => _leaving;
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
            else if (seg.StartsWith("c", StringComparison.Ordinal) && long.TryParse(seg[1..], out var c) && c > 0) _buyIn = c;
        }
        if (_buyIn <= 0) _buyIn = _bet * 50;   // default buy-in: 50 bets, so the table plays many hands
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
                if (_leaving && _leaveAfter.TryGetValue(_myPubHex, out var la)) Send(new { t = "leave", after = la });   // keep telling peers I'm leaving until the hand turns over
                if (_roster == null) { TryAssignSeats(); return; }
                if (State == Phase.Done) return;
                // The on-chain pot (escrow + miner-verify + refund co-sign) runs ENTIRELY IN THE BACKGROUND and NEVER
                // gates the cards — the game deals instantly; the money is secured in parallel while you play.
                if (PotActive && !_potReady && State != Phase.Settling) DrivePot();
                if (State == Phase.Settling) { DriveSettlement(); return; }
                if (State == Phase.HandOver)
                {
                    if (Environment.TickCount64 - _handOverAt >= HandPauseMs) StartHand();   // deal the next hand — a real table never stops on its own
                    return;
                }
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
        if (_roster != null) return;
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
            _roster = order.Select(i => cands[i]).ToArray();
            foreach (var p in _roster) _bankroll[p] = _buyIn;   // everyone buys in for the same amount
            _dealerBankroll = _buyIn * _roster.Length;          // the house is staked to cover every seat's buy-in (real tokens)
            _potPubs = _roster.Select(Convert.FromHexString).ToArray();
            _potScript = BlackjackPot.PotScript(_potPubs);      // the n-of-n pot every player escrows into (funded in the background)
            StartHand();                                        // DEAL IMMEDIATELY — the pot funds in parallel, it never blocks the cards
        }
        else { int c = cands.Count(p => _seatCommits.ContainsKey(p)), r = cands.Count(p => _seatReveals.ContainsKey(p)); Status = $"Agreeing a fair seat order… commits {c}/{_seatCount}, reveals {r}/{_seatCount}"; }
    }

    // True if this roster player is still in for the given hand: present (bankroll covers a bet) and has not left.
    private bool InFor(string pub, int hand) => _bankroll.GetValueOrDefault(pub) >= _bet && (!_leaveAfter.TryGetValue(pub, out var last) || hand <= last);

    /// <summary>Deal the next hand: re-seat the players still in (bankroll ≥ bet, not left), reset all deal state.
    /// If fewer than two remain the SESSION ends and everyone cashes out their bankroll.</summary>
    private void StartHand()
    {
        if (_roster == null) return;
        _handNo++;
        if (_dealerBankroll <= 0) { EndSession(); return; }   // the house is busted — a real-token table cannot keep paying
        // On a real-token table whose pot has finished securing, a leave with ≥2 remaining SPLITS the pot on-chain
        // (leaver cashed out, the rest play on). If the pot isn't secured yet, fall through to plain bankroll play.
        if (PotActive && _potReady)
        {
            var leavers = _roster.Where(p => !InFor(p, _handNo)).ToArray();
            if (leavers.Length > 0)
            {
                var remain = _roster.Where(p => InFor(p, _handNo)).ToArray();
                if (remain.Length < 2) { EndSession(); return; }
                BeginSplit(leavers, remain); return;
            }
        }
        var active = _roster.Where(p => InFor(p, _handNo)).ToArray();
        if (active.Length < 2) { EndSession(); return; }
        _seats = active;
        _mySeat = Array.IndexOf(_seats, _myPubHex);
        int s = Seats;
        _hands = Enumerable.Range(0, s).Select(_ => new List<Card>()).ToArray();
        _done = new bool[s]; _doubled = new bool[s]; _net = new long[s]; _outcome = new BjOutcome[s];
        _betOf = Enumerable.Repeat(_bet, s).ToArray();
        _drawNext = 2 * s + 2;
        _toAct = -1; _applied = 0;
        _dealer.Clear(); _opened.Clear(); _everNeeded.Clear(); _pendingDraw.Clear();
        _shuf.Clear(); _rem.Clear(); _comm.Clear(); _maskShares.Clear(); _final = null;
        _sentShuf = _sentRem = false;
        _ecGlobal = MentalPokerEC.NewScalar(); _ecPerCard = MentalPokerEC.NewPerCardScalars(_n); _ecPerm = RandomPerm(_n); _ecNonce = Array.Empty<byte[]>();
        if (_mySeat < 0) { State = Phase.Done; Status = $"You left the table with {MyBankroll} sat. The remaining players continue."; Raise(); return; }
        State = Phase.Dealing; Status = $"Hand #{HandNumber} — you are seat {_mySeat}. Shuffling the deck (encrypted)…"; Raise();
    }

    // If a player we are still dealing this hand has actually left as of an earlier hand (we learned of their leave
    // late), the active set for the CURRENT hand is wrong and the deal would wait on them forever. Re-derive and
    // re-deal the same hand number with the corrected set. A no-op when membership already matches.
    private void RecheckMembership()
    {
        if (_roster == null || _seats == null) return;
        if (State is not (Phase.Dealing or Phase.Playing or Phase.DealerPlay)) return;
        var active = _roster.Where(p => InFor(p, _handNo)).ToArray();
        if (active.SequenceEqual(_seats)) return;     // membership unchanged — nothing to do
        _handNo--;                                    // StartHand re-increments to the SAME hand number
        StartHand();
    }

    private void EndSession()
    {
        _toAct = -1;
        // A real-token table does not just stop — it SETTLES the pot on-chain to the final standings, co-signed by
        // everyone, then closes. A bankroll-only (headless) table just reports the cash-out.
        if (PotActive && _potReady && _pot.Count > 0 && !_settleDone) { BeginSettlement(); return; }   // on-chain only if the pot finished securing; else cash out on bankroll
        State = Phase.Done;
        Status = _leaving
            ? $"You left the table. Cashed out {MyBankroll} sat."
            : $"Table closed — not enough players to continue. You cash out {MyBankroll} sat.";
        Raise();
    }

    // ===== ON-CHAIN POT: funding (each player escrows their stake) and settlement (everyone co-signs the payout) =====

    private List<BlackjackPot.PotIn> SortedPot() => _potIns.Values
        .Select(c => new BlackjackPot.PotIn(c.Txid, c.Vout, c.Value))
        .OrderBy(p => p.Txid, StringComparer.Ordinal).ThenBy(p => p.Vout).ToList();

    // BACKGROUND pot work — runs each tick WHILE the game is already being played; it NEVER deals, blocks, or
    // changes the game phase. It escrows the stake, asks miners to confirm it (first-seen), and co-signs the
    // nLockTime refund. When all of that is in hand the pot is "ready" and the end-of-session payout can be on-chain.
    // If it isn't ready yet when the session ends, the table simply settles on tracked bankroll (best effort).
    private void DrivePot()
    {
        if (_roster == null) return;
        if (!_postSplit)
        {
            if (!_funded && FundPot != null)
            {
                var coin = FundPot(_potScript, MyStake);   // builds the tx locally + broadcasts async (does not block)
                if (coin != null) { _potIns[_myPubHex] = coin; _funded = true; }
            }
            if (_potIns.TryGetValue(_myPubHex, out var mine))
                Send(new { t = "potin", txid = mine.Txid, vout = mine.Vout, value = mine.Value, raw = mine.RawHex });
            if (!_roster.All(p => _potIns.ContainsKey(p))) return;   // still gathering escrows — keep playing meanwhile

            // ASK THE MINER (BSV first-seen) about every escrow, in the BACKGROUND. A double-spent stake is flagged
            // (CheatDetected) and simply blocks the on-chain payout; it never freezes the game.
            if (VerifyFundedOnChain != null)
            {
                foreach (var kv in _potIns)
                    if (!_potVerified.ContainsKey(kv.Key) && !_potDoubleSpent.ContainsKey(kv.Key) && _potChecking.TryAdd(kv.Key, 1))
                    {
                        string who = kv.Key, raw = kv.Value.RawHex;
                        _ = System.Threading.Tasks.Task.Run(() =>
                        {
                            bool ok; try { ok = VerifyFundedOnChain(raw); } catch { ok = false; }
                            if (ok) _potVerified[who] = 1; else _potDoubleSpent[who] = 1;
                            _potChecking.TryRemove(who, out _);
                        });
                    }
                if (!_potDoubleSpent.IsEmpty) { CheatDetected = true; return; }
                if (!_roster.All(p => _potVerified.ContainsKey(p))) return;   // miner confirmations still pending — keep playing
            }
            if (_pot.Count == 0) _pot = SortedPot();
        }

        // co-sign the nLockTime REFUND for the current pot generation (background; the safety net for settlement)
        if (_recoveryTx == null)
        {
            try { _recoveryTx = BlackjackPot.BuildSessionRecovery(_pot, _potPubs, StakesInRosterOrder(), PotFee, RecoveryLockHeight); }
            catch { _potReady = true; return; }   // couldn't build the refund — settlement still needs all sigs
        }
        if (!_recoverySigs.ContainsKey(_myPubHex))
        {
            var sigs = new string[_pot.Count];
            for (int j = 0; j < _pot.Count; j++) sigs[j] = Convert.ToHexString(BlackjackPot.SignSessionInput(_recoveryTx, j, _potPubs, _pot[j].Value, _priv)).ToLowerInvariant();
            _recoverySigs[_myPubHex] = sigs;
        }
        Send(new { t = "recosig", g = _potGen, sigs = _recoverySigs[_myPubHex] });
        if (_recoveryRaw == null && _roster.All(p => _recoverySigs.ContainsKey(p)))
        {
            var perInput = new List<IReadOnlyList<byte[]>>();
            for (int j = 0; j < _pot.Count; j++) { var col = new List<byte[]>(); foreach (var pub in _roster) col.Add(Convert.FromHexString(_recoverySigs[pub][j])); perInput.Add(col); }
            var rec = BlackjackPot.ApplySessionSigs(_recoveryTx, perInput);
            if (VerifyRecovery(rec)) { _recoveryRaw = Chain.Serialize(rec); Send(new { t = "recovered", g = _potGen, raw = Convert.ToHexString(_recoveryRaw).ToLowerInvariant() }); }
        }
        if (_recoveryRaw != null) _potReady = true;   // pot fully secured — end-of-session payout can be on-chain
    }

    // The stakes the refund returns, in roster order. Initial pot: each player's own coin. Post-split single-coin
    // pot: split the pot across the remaining players (the refund only needs to conserve the pot).
    private long[] StakesInRosterOrder()
    {
        if (!_postSplit) return _roster!.Select(p => _potIns[p].Value).ToArray();
        long potV = _pot.Sum(p => p.Value); int n = _roster!.Length;
        long share = potV / n, extra = potV - share * n; var s = new long[n];
        for (int i = 0; i < n; i++) s[i] = share + (i == n - 1 ? extra : 0);
        return s;
    }


    private void BeginSettlement()
    {
        if (_roster == null) { State = Phase.Done; return; }
        State = Phase.Settling; _settleStarted = true; _isSplit = false;
        _settleInputs = new List<BlackjackPot.PotIn>(_pot);   // the current pot coins (deterministic order)
        long pot = _settleInputs.Sum(p => p.Value);
        int n = _roster.Length;
        long residual = _dealerBankroll, share = residual / n, extra = residual - share * n;
        var final = new long[n];
        for (int i = 0; i < n; i++) final[i] = _bankroll.GetValueOrDefault(_roster[i]) + share + (i == 0 ? extra : 0);
        // the on-chain fee comes off the largest payout (keeps every payout positive)
        int big = 0; for (int i = 1; i < n; i++) if (final[i] > final[big]) big = i;
        final[big] -= PotFee;
        try { _settleTx = BlackjackPot.BuildSessionSettlement(_settleInputs, _potPubs, final, PotFee); }
        catch (Exception) { State = Phase.Done; Status = "Settlement could not be built (pot mismatch)."; Raise(); return; }
        _settlingAt = Environment.TickCount64;
        Status = "Settling the pot on-chain — all players co-sign the payout…";
        Raise();
    }

    // A LEAVER SPLIT: pay each leaving player their standing AND re-escrow the remainder into a NEW n-of-n pot of
    // the players who continue — all co-signed by the CURRENT players. Built deterministically (same on every node).
    private void BeginSplit(string[] leavers, string[] remaining)
    {
        if (_roster == null) return;
        State = Phase.Settling; _settleStarted = true; _isSplit = true; _splitRemaining = remaining;
        _settleInputs = new List<BlackjackPot.PotIn>(_pot);
        long pot = _settleInputs.Sum(p => p.Value);
        int n = _roster.Length; long share = _dealerBankroll / n;   // each player's share of the house residual at the leave
        var leaverPubs = new List<byte[]>(); var leaverPayouts = new List<long>(); long leaversTotal = 0;
        foreach (var p in _roster) if (Array.IndexOf(leavers, p) >= 0)
        { long pay = _bankroll.GetValueOrDefault(p) + share; leaverPubs.Add(Convert.FromHexString(p)); leaverPayouts.Add(pay); leaversTotal += pay; }
        long newPot = pot - leaversTotal - PotFee;
        var remainingPubs = _roster.Where(p => Array.IndexOf(remaining, p) >= 0).Select(Convert.FromHexString).ToList();
        try { _settleTx = BlackjackPot.BuildLeaverSplit(_settleInputs, leaverPubs, leaverPayouts, remainingPubs, newPot, PotFee); }
        catch { State = Phase.Done; Status = "The leaver split could not be built."; Raise(); return; }
        _settlingAt = Environment.TickCount64;
        Status = "A player is leaving — co-signing the on-chain split (cash them out; the rest play on)…";
        Raise();
    }

    private void DriveSettlement()
    {
        if (_settleTx == null || _roster == null) return;
        if (!_settleSigs.ContainsKey(_myPubHex))
        {
            var sigs = new string[_settleInputs.Count];
            for (int j = 0; j < _settleInputs.Count; j++)
                sigs[j] = Convert.ToHexString(BlackjackPot.SignSessionInput(_settleTx, j, _potPubs, _settleInputs[j].Value, _priv)).ToLowerInvariant();
            _settleSigs[_myPubHex] = sigs;
        }
        Send(new { t = "settlesig", g = _potGen, sigs = _settleSigs[_myPubHex] });   // re-sent each tick (deduped) until everyone signs
        if (_settleDone) return;

        // Assemble ONLY a fully-VALID n-of-n tx: every player present AND the result verifies. A single missing or
        // GARBAGE signature must NOT be treated as "done" — that would broadcast an invalid tx and strand the pot.
        Chain.Tx? signed = null;
        if (_roster.All(p => _settleSigs.ContainsKey(p)))
        {
            var perInput = new List<IReadOnlyList<byte[]>>();
            for (int j = 0; j < _settleInputs.Count; j++)
            {
                var col = new List<byte[]>();
                foreach (var pub in _roster) col.Add(Convert.FromHexString(_settleSigs[pub][j]));
                perInput.Add(col);
            }
            try { var cand = BlackjackPot.ApplySessionSigs(_settleTx, perInput); if (VerifyAssembled(cand)) signed = cand; } catch { }
        }
        if (signed == null)
        {
            // GRIEFING SAFETY: if a valid payout can't be assembled within the grace window (a player won't sign, or
            // signs garbage, or vanished mid-split), broadcast the pre-signed nLockTime REFUND — no stake is stranded.
            if (_recoveryRaw != null && !_recoveryBroadcast && Environment.TickCount64 - _settlingAt > SettleGraceMs)
            {
                _recoveryBroadcast = true; _settleDone = true; State = Phase.Done;
                try { OnRecoveryTx?.Invoke(_recoveryRaw); } catch { }
                Status = "The payout could not be co-signed — the pre-signed refund was broadcast; every stake is returned after the timeout.";
                Raise(); return;
            }
            Status = $"{(_isSplit ? "Splitting" : "Settling")} the pot on-chain… {_settleSigs.Count}/{_roster.Length} players co-signed.";
            return;
        }
        int gen = _potGen; bool wasSplit = _isSplit;
        var rawBytes = Chain.Serialize(signed);
        try { OnSettlementTx?.Invoke(rawBytes); } catch { }
        // Transition FIRST, THEN gossip. Send() echoes locally on THIS thread (reentrant lock), so if we gossiped
        // before transitioning, our own "settled" would re-enter AdoptSettled→ContinueAfterSplit, clear _isSplit,
        // and the outer flow would then wrongly take the full-settlement Done path. Transitioning first makes the
        // reentrant AdoptSettled a no-op (State is no longer Settling).
        if (wasSplit) ContinueAfterSplit(signed);
        else { _settleDone = true; State = Phase.Done; Status = $"Pot settled on-chain — you cashed out {MyBankroll} sat (tx {Chain.Txid(signed)[..12]}…)."; Raise(); }
        Send(new { t = "settled", g = gen, raw = Convert.ToHexString(rawBytes).ToLowerInvariant() });   // straggler adopts it (no stall → recovery)
    }

    // Adopt a settlement/split that ANOTHER player already assembled (so a node whose peer finished first doesn't
    // stall waiting for a co-sig that will never be re-sent). Verified before adopting — never trust a raw blindly.
    private void AdoptSettled(byte[] raw)
    {
        if (_settleDone || _settleTx == null || State != Phase.Settling) return;
        Chain.Tx tx; try { tx = Chain.Deserialize(raw); } catch { return; }
        if (tx.Ins.Count != _settleInputs.Count || !VerifyAssembled(tx)) return;   // must be a valid n-of-n spend of OUR pot inputs
        try { OnSettlementTx?.Invoke(raw); } catch { }
        if (_isSplit) { ContinueAfterSplit(tx); return; }
        _settleDone = true; State = Phase.Done;
        Status = $"Pot settled on-chain (tx {Chain.Txid(tx)[..12]}…).";
        Raise();
    }

    // A fully co-signed settlement/split is accepted only if EVERY n-of-n input verifies — so a forged or missing
    // signature can never be mistaken for a completed payout (it falls through to the pre-signed refund instead).
    private bool VerifyAssembled(Chain.Tx tx)
    {
        try { for (int j = 0; j < _settleInputs.Count; j++) if (!Chain.VerifyMultisigNofN(tx, j, _potPubs, _settleInputs[j].Value)) return false; return true; }
        catch { return false; }
    }

    private bool VerifyRecovery(Chain.Tx tx)
    {
        try { if (tx.Ins.Count != _pot.Count) return false; for (int j = 0; j < _pot.Count; j++) if (!Chain.VerifyMultisigNofN(tx, j, _potPubs, _pot[j].Value)) return false; return true; }
        catch { return false; }
    }

    // Adopt a fully co-signed REFUND another player assembled, so a node whose peer finished the refund handshake
    // first doesn't stall in Funding waiting for a co-sig that will never be re-sent.
    private void AdoptRecovered(byte[] raw)
    {
        if (_recoveryRaw != null || _recoveryTx == null) return;   // background: adopt a peer's assembled refund
        Chain.Tx tx; try { tx = Chain.Deserialize(raw); } catch { return; }
        if (!VerifyRecovery(tx)) return;
        _recoveryRaw = raw; _potReady = true;
    }

    // After the split tx is co-signed (identically on every node), reconfigure to the new pot/roster: the leavers
    // are cashed out and done; the remaining players adopt the re-escrowed coin as a NEW generation pot, re-secure
    // it with a fresh refund, and play on — no re-funding, no re-confirmation.
    private void ContinueAfterSplit(Chain.Tx signed)
    {
        var newCoin = new BlackjackPot.PotIn(Chain.Txid(signed), (uint)(signed.Outs.Count - 1), signed.Outs[^1].Value);
        bool iRemain = _splitRemaining != null && Array.IndexOf(_splitRemaining, _myPubHex) >= 0;
        _potGen++;
        _pot = new List<BlackjackPot.PotIn> { newCoin };
        _roster = _splitRemaining;
        _potPubs = _roster!.Select(Convert.FromHexString).ToArray();
        _potScript = BlackjackPot.PotScript(_potPubs);
        _dealerBankroll = newCoin.Value - _roster.Sum(p => _bankroll.GetValueOrDefault(p));   // residual backing the new pot
        _potIns.Clear(); _settleSigs.Clear(); _settleTx = null; _settleInputs = new(); _isSplit = false; _splitRemaining = null;
        _recoverySigs.Clear(); _recoveryTx = null; _recoveryRaw = null; _recoveryBroadcast = false;
        _leaveAfter.Clear(); _settleDone = false; _settleStarted = false; _postSplit = true; _funded = true; _potReady = false;
        if (iRemain) { StartHand(); }   // DEAL ON IMMEDIATELY — DrivePot re-secures the new (smaller) pot in the background
        else { _leaving = true; State = Phase.Done; Status = $"You left — cashed out on-chain. The others play on."; Raise(); }
    }

    private static int[] RandomPerm(int n) { var p = Enumerable.Range(0, n).ToArray(); for (int i = n - 1; i > 0; i--) { int j = (int)System.Security.Cryptography.RandomNumberGenerator.GetInt32(i + 1); (p[i], p[j]) = (p[j], p[i]); } return p; }

    // ---- the dealerless deal: shuffle + remask + hiding commitments (same as NetGame) ----
    private void SendShuf() { if (_shuf.TryGetValue(_mySeat, out var p)) Send(new { t = "shuf", h = _handNo, step = _mySeat, pts = PtsHex(p) }); }
    private void SendRem() { if (_rem.TryGetValue(_mySeat, out var p)) Send(new { t = "rem", h = _handNo, step = _mySeat, pts = PtsHex(p) }); }
    private void SendComm() { if (_comm.TryGetValue(_mySeat, out var c)) Send(new { t = "comm", h = _handNo, seat = _mySeat, c = PtsHex(c) }); }
    private void DriveDeal()
    {
        if (_seats == null || State != Phase.Dealing) return;
        if (!_shuf.ContainsKey(_mySeat))
        {
            byte[][]? input = _mySeat == 0 ? MentalPokerEC.BaseDeck(_n) : (_shuf.TryGetValue(_mySeat - 1, out var prev) ? prev : null);
            if (input != null) _shuf[_mySeat] = MentalPokerEC.ShuffleMask(input, _ecGlobal, _ecPerm);
        }
        if (_shuf.ContainsKey(_mySeat)) { _sentShuf = true; SendShuf(); }   // re-send each tick (deduped) so a peer that joins this hand late, or settled later, still catches up
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
        if (_rem.ContainsKey(_mySeat)) { _sentRem = true; SendRem(); }   // re-send each tick (deduped) for late/lagging peers
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
        if ((State is Phase.DealerPlay or Phase.HandOver or Phase.Done) && DealerHole < _n) _everNeeded.Add(DealerHole);
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
        Send(new { t = "reveal", h = _handNo, seat = _mySeat, d = map });   // re-sent each tick (stable id ⇒ deduped; grows as draws happen)
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
            // A hit's card arrives a few ticks AFTER the hit (it must be revealed by everyone). If it busted the
            // actor, ResolvePendingDraws set _done but did not move the turn — so advance here, or the busted
            // player would keep "acting" (the 27-and-still-playing bug).
            if (State == Phase.Playing && _toAct >= 0 && _done[_toAct]) AdvanceTurn();
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
            if (_done[_mySeat]) return;                       // already finished this hand (stood/doubled/busted)
            if (_pendingDraw.ContainsValue(_mySeat)) return;  // a card you drew hasn't arrived yet — wait and SEE it first
            int seq = _applied;
            ApplyAction(_mySeat, a);
            Send(new { t = "act", h = _handNo, seq, seat = _mySeat, a = (int)a });
        }
    }

    /// <summary>Leave the table. You finish the hand in progress (your bet stands), then you are dealt out and
    /// cash out your bankroll — the remaining players keep playing. If no hand is in progress you leave at once.
    /// Real-table rule: you cannot pull a bet out of a hand that is already dealt.</summary>
    public void Leave()
    {
        lock (_gate)
        {
            if (_leaving || State == Phase.Done) return;
            _leaving = true;
            int after = _handNo;                                   // I play up to and including the current hand
            _leaveAfter[_myPubHex] = after;
            Send(new { t = "leave", after });                      // tell every node so they deal me out next hand
            if (State == Phase.Playing && _toAct == _mySeat) { int seq = _applied; ApplyAction(_mySeat, BjAction.Stand); Send(new { t = "act", h = _handNo, seq, seat = _mySeat, a = (int)BjAction.Stand }); }
            // If I'm already not seated, end now. Otherwise the next StartHand routes my leave to a SPLIT (≥2 remain)
            // or a full settlement (a real-token table never force-closes when others could keep playing).
            if (_roster != null && _seats != null && _mySeat < 0) { EndSession(); return; }
            Status = $"Leaving after this hand (#{HandNumber}). You will cash out {MyBankroll} sat.";
            Raise();
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
        long playersNet = 0;
        for (int s = 0; s < Seats; s++) { _bankroll.AddOrUpdate(_seats![s], _net[s], (_, b) => b + _net[s]); playersNet += _net[s]; }
        _dealerBankroll -= playersNet;   // the house wins what players lose and pays what they win (real tokens, conserved)
        State = Phase.HandOver; _toAct = -1; _handOverAt = Environment.TickCount64;
        string results = string.Join("  ", Enumerable.Range(0, Seats).Select(s => $"P{s}:{_outcome[s]}({(_net[s] >= 0 ? "+" : "")}{_net[s]})"));
        bool willEnd = _dealerBankroll <= 0 || _roster!.Count(p => InFor(p, _handNo + 1)) < 2;
        Status = $"Hand #{HandNumber} complete. {results}.  Your bankroll: {MyBankroll} sat · house {_dealerBankroll} sat. " +
                 (_leaving ? "Cashing you out…" : _dealerBankroll <= 0 ? "House is busted — table closing." : willEnd ? "Table closing — not enough players." : "Next hand starting…");
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
            // a signed leave: this player is dealt out after the named hand (the lowest such number wins — no take-backs).
            // If we had already started a LATER hand still including them (their leave hadn't reached us yet), re-derive
            // membership for the current hand so we stop waiting on a player who is gone — otherwise the deal deadlocks.
            if (t == "leave") { try { int after = root.GetProperty("after").GetInt32(); _leaveAfter.AddOrUpdate(pub, after, (_, old) => Math.Min(old, after)); lock (_gate) RecheckMembership(); } catch { } return; }
            // a player announces the pot coin they escrowed. VERIFY it: the raw funding tx must hash to the claimed
            // txid AND its vout must pay EXACTLY the claimed value to OUR n-of-n pot script. Otherwise a player could
            // "fund" nothing (or pay themselves) yet play with a full bankroll — and poison the settlement/refund.
            if (t == "potin")
            {
                try
                {
                    if (_roster == null || Array.IndexOf(_roster, pub) < 0 || _potIns.ContainsKey(pub)) return;
                    string txid = root.GetProperty("txid").GetString()!; uint vout = root.GetProperty("vout").GetUInt32();
                    long value = root.GetProperty("value").GetInt64(); string raw = root.GetProperty("raw").GetString() ?? "";
                    if (value != MyStake) { Reject("potin: wrong stake"); return; }   // every player must escrow the same full stake
                    var tx = Chain.Deserialize(Convert.FromHexString(raw));
                    if (!string.Equals(Chain.Txid(tx), txid, StringComparison.OrdinalIgnoreCase)) { Reject("potin: txid mismatch"); return; }
                    if (vout >= tx.Outs.Count || tx.Outs[(int)vout].Value != value || !tx.Outs[(int)vout].Script.SequenceEqual(_potScript)) { Reject("potin: does not pay the pot"); return; }
                    _potIns.TryAdd(pub, new PotCoin(txid, vout, value, raw));
                }
                catch { Reject("potin: malformed"); }
                return;
            }
            // a player's co-signatures for the settlement/split (one per pot input). Tagged with the pot GENERATION
            // so a stale co-sig from before a leaver-split can never be mixed into the new pot's signing.
            if (t == "settlesig") { try { if (_roster != null && Array.IndexOf(_roster, pub) >= 0 && root.GetProperty("g").GetInt32() == _potGen) { var arr = root.GetProperty("sigs").EnumerateArray().Select(e => e.GetString()!).ToArray(); _settleSigs.TryAdd(pub, arr); } } catch { } return; }
            // a player's co-signatures for the pre-signed nLockTime REFUND (per generation; collected before play)
            if (t == "recosig") { try { if (_roster != null && Array.IndexOf(_roster, pub) >= 0 && root.GetProperty("g").GetInt32() == _potGen) { var arr = root.GetProperty("sigs").EnumerateArray().Select(e => e.GetString()!).ToArray(); _recoverySigs.TryAdd(pub, arr); } } catch { } return; }
            // a finished settlement/split tx another player assembled — adopt it (verified) so we never stall waiting
            if (t == "settled") { try { if (_roster != null && Array.IndexOf(_roster, pub) >= 0 && root.GetProperty("g").GetInt32() == _potGen) { var raw = Convert.FromHexString(root.GetProperty("raw").GetString()!); lock (_gate) AdoptSettled(raw); } } catch { } return; }
            // a finished REFUND another player assembled — adopt it so the refund handshake can't strand a straggler
            if (t == "recovered") { try { if (_roster != null && Array.IndexOf(_roster, pub) >= 0 && root.GetProperty("g").GetInt32() == _potGen) { var raw = Convert.FromHexString(root.GetProperty("raw").GetString()!); lock (_gate) AdoptRecovered(raw); } } catch { } return; }
            lock (_gate)
            {
                // every per-hand message is tagged with its hand number; a stale frame from a previous hand is ignored,
                // so the continuous re-deal never mixes state across hands (and identical "act" frames can't collide).
                if (!root.TryGetProperty("h", out var hEl) || hEl.GetInt32() != _handNo) return;
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
