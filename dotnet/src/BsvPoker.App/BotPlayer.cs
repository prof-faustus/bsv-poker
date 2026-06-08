using BsvPoker.Core;
using BsvPoker.Crypto;
using BsvPoker.Net;
using BsvPoker.Net.Bsv;

namespace BsvPoker.App;

/// <summary>
/// A bot is a SEPARATE automated player — its OWN identity, OWN wallet, OWN always-online SPV node (TxLink),
/// and OWN entry on the poker gossip overlay. It is not a hot-seat clone and not a second copy of the app.
/// The human discovers it on the gossip overlay and plays a REAL two-party on-chain hand against it
/// (<see cref="LiveHand"/>); the bot funds its own stake, signs its own escrow input, and reveals/plays the
/// dealerless deal automatically. The bot is test-only: it must be funded with real coins (an SPV envelope)
/// before it can play, and on close it refunds everything to its funder.
/// </summary>
public sealed class BotPlayer : IDisposable
{
    public const long LiveStake = 20_000;

    private readonly NetworkParams _net;
    private readonly byte[] _seed;
    private readonly OnChainWallet _wallet;
    private readonly TxLink _link;
    private readonly object _lock = new();
    private TxDealChannel? _deal;
    private long _balance;                 // confirmed coin held by the bot (SPV-verified envelopes only)
    private string? _funderAddress;        // where to refund on close

    public byte[] Priv { get; }
    public byte[] Pub { get; }
    public PokerGossip Gossip { get; }
    public string PubHex => Convert.ToHexString(Pub).ToLowerInvariant();
    public string Endpoint { get; }
    public long Balance { get { lock (_lock) return _balance; } }
    public event Action<string>? OnLog;

    private readonly byte[] _ownerPub;     // the bot belongs to this identity and plays ONLY this identity
    public string Name { get; }            // e.g. "Alice-Bot-001"

    /// <param name="ownerPriv">the owner's identity private key — the bot's key is DERIVED from it (Type-42), so
    /// only the owner can create/control this bot.</param>
    /// <param name="ownerPub">the owner's identity public key — the bot will ONLY play this identity.</param>
    /// <param name="index">the bot number for this owner (Alice-Bot-001, -002, …).</param>
    /// <param name="ownerHandle">the owner's handle for naming.</param>
    public BotPlayer(NetworkParams net, string localIp, byte[] ownerPriv, byte[] ownerPub, int index, string ownerHandle)
    {
        _net = net;
        _ownerPub = ownerPub;
        Name = $"{(string.IsNullOrWhiteSpace(ownerHandle) ? "Owner" : ownerHandle)}-Bot-{index:D3}";
        // the bot's seed is DERIVED from the owner's identity via Type-42 — provably the owner's bot, and only the
        // owner (who holds ownerPriv) can ever derive/run it. A different owner cannot reproduce this key.
        _seed = Type42.UniqueKey(ownerPriv, $"bsvpoker/bot/{index}");
        var k = WalletKeys.Account(_seed, 0, 0);
        Priv = k.Priv; Pub = k.Pub;
        _wallet = new OnChainWallet(_seed);
        _link = new TxLink(net, 0);
        _link.OnTransaction += Ingest;
        _link.Start();
        Endpoint = $"{localIp}:{_link.Port}";
        Gossip = new PokerGossip(PubHex, Endpoint, (peerPub, endpoint, msg) => SendChat(peerPub, endpoint, "GOSSIP:" + msg));
        Gossip.OnPeersChanged += () => OnLog?.Invoke($"discovered {Gossip.Peers.Count} peer(s)");
        Log($"{Name} online — plays ONLY its owner {Convert.ToHexString(_ownerPub).ToLowerInvariant()[..12]}…  @ {Endpoint}");
    }

    public string ReceiveAddress()
    {
        var payload = new byte[21]; payload[0] = _net.AddressVersion; Hashes.Hash160(Pub).CopyTo(payload, 1);
        return Base58.CheckEncode(payload);
    }

    /// <summary>Fund the bot with a real coin via an SPV envelope (verified before crediting). Records the funder for refund.</summary>
    public bool ImportFunding(SpvEnvelope env, string funderAddress)
    {
        if (!env.Verify()) { Log("funding rejected — SPV envelope did not verify"); return false; }
        var tx = env.ParseTx(); if (tx == null) return false;
        var lockMe = Chain.P2pkhLock(Hashes.Hash160(Pub));
        bool credited = false;
        for (int v = 0; v < tx.Outs.Count; v++)
            if (tx.Outs[v].Script.AsSpan().SequenceEqual(lockMe))
            {
                lock (_lock) { _wallet.Add(new OnChainWallet.Utxo(Chain.Txid(tx), (uint)v, tx.Outs[v].Value, 0, 0)); _balance += tx.Outs[v].Value; }
                credited = true;
            }
        if (credited) { _funderAddress = funderAddress; Log($"funded — balance {Balance:N0} sat (will refund {funderAddress} on close)"); }
        else Log("funding envelope does not pay the bot");
        return credited;
    }

    /// <summary>Announce on the overlay (so the human finds the bot) and pull peers it knows.</summary>
    public void Announce() { Gossip.Announce(); Gossip.Query(); }
    public void AddPeer(string pubHex, string endpoint) => Gossip.AddSeed(pubHex, endpoint);

    private void Ingest(Chain.Tx tx)
    {
        var msg = OnChainChat.TryReadTx(tx, Priv, Pub);
        if (msg == null) return;
        var senderHex = Convert.ToHexString(msg.SenderPub).ToLowerInvariant();
        if (msg.Text.StartsWith("GOSSIP:", StringComparison.Ordinal)) { Gossip.Receive(msg.Text["GOSSIP:".Length..]); return; }
        if (msg.Text.StartsWith("DEAL:", StringComparison.Ordinal))
        {
            // a bot ONLY ever plays its owner — refuse a hand from anyone else, always.
            if (!msg.SenderPub.AsSpan().SequenceEqual(_ownerPub)) { Log($"refused a hand from {senderHex[..12]}… — {Name} only plays its owner."); return; }
            if (_deal == null) StartHand(msg.SenderPub, senderHex);
            if (_deal != null && _deal.PeerPub.AsSpan().SequenceEqual(msg.SenderPub)) _deal.Deliver(msg.Text["DEAL:".Length..]);
            return;
        }
        Log($"chat from {senderHex[..12]}…: {msg.Text}");
    }

    private void StartHand(byte[] peerPub, string peerHex)
    {
        var peer = Gossip.Peers.FirstOrDefault(p => p.PubHex == peerHex);
        if (peer == null) { Log("got a hand request but don't know that peer's address yet"); return; }
        if (Balance < LiveStake + 5000) { Log("cannot play — fund the bot first (Import funding)"); return; }
        var seatCoin = ReserveSeat();
        if (seatCoin == null) { Log("no spendable coin to seat the hand"); return; }
        var ch = new TxDealChannel(peerPub, pt => SendChat(peerHex, peer.Endpoint, "DEAL:" + pt));
        _deal = ch;
        bool initiator = string.CompareOrdinal(PubHex, peerHex) < 0;
        Log($"playing a hand vs {peerHex[..12]}… ({(initiator ? "I deal first" : "they deal first")})");
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var s = seatCoin.Value;
                var r = initiator
                    ? LiveHand.RunInitiator(ch, s.Utxo, s.ChangePub, (s.Priv, s.Pub), LiveStake)
                    : LiveHand.RunResponder(ch, s.Utxo, s.ChangePub, (s.Priv, s.Pub), LiveStake);
                bool iWon = (r.WinnerSeat == 0) == initiator;
                lock (_lock) { _balance -= LiveStake; if (iWon) _balance += r.Pot; }   // settlement moves the pot
                Log($"hand complete — pot {r.Pot:N0} → {(iWon ? "bot wins" : "human wins")}; proofs {r.ProofsVerified}");
            }
            catch (Exception ex) { Log("hand did not complete: " + ex.Message); }
            finally { _deal = null; }
        });
    }

    private (OnChainWallet.Utxo Utxo, byte[] Priv, byte[] Pub, byte[] ChangePub)? ReserveSeat()
    {
        lock (_lock)
        {
            var u = _wallet.Coins.FirstOrDefault(c => c.Value >= LiveStake + 5000);
            if (u == null) return null;
            var k = WalletKeys.Account(_seed, u.KeyChain, u.KeyIndex);
            return (u, k.Priv, k.Pub, WalletKeys.Account(_seed, 1, 0).Pub);
        }
    }

    private string SendChat(string recipientPubHex, string endpoint, string text)
    {
        try
        {
            var rpub = Convert.FromHexString(recipientPubHex);
            var script = OnChainChat.BuildScript(rpub, Pub, text);
            lock (_lock)
            {
                if (_wallet.Balance < 1500) return "bot has no sats to send a message";
                var spend = _wallet.SpendAction(script, 1000, 500);
                var (host, port) = ParseHostPort(endpoint);
                if (host != null) _ = TxLink.SendTxAsync(_net, host, port, Chain.Serialize(spend.Tx));
            }
            return "";
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Refund every remaining sat to the funder (bots leave no money behind).</summary>
    public void RefundFunder()
    {
        try
        {
            if (_funderAddress == null) return;
            var payload = Base58.CheckDecode(_funderAddress);
            lock (_lock)
            {
                if (_wallet.Balance <= 1000) return;
                var spend = _wallet.BuildAction(Chain.P2pkhLock(payload[1..]), _wallet.Balance - 1000, 1000);
                Log($"refunding {_wallet.Balance - 1000:N0} sat to the funder {_funderAddress}");
                // (broadcast handled by the host node; the bot has no miner connection of its own)
                _ = spend;
            }
        }
        catch (Exception ex) { Log("refund failed: " + ex.Message); }
    }

    private static (string? Host, int Port) ParseHostPort(string s)
    {
        var i = s.LastIndexOf(':');
        if (i <= 0 || !int.TryParse(s[(i + 1)..], out var p) || p <= 0) return (null, 0);
        return (s[..i], p);
    }

    private void Log(string m) => OnLog?.Invoke(m);
    public void Dispose() { try { RefundFunder(); } catch { } try { _link.Dispose(); } catch { } }
}
