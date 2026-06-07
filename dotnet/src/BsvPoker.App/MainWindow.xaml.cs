using System.Windows;
using BsvPoker.App.Views;
using BsvPoker.Core;
using BsvPoker.Net;
using BsvPoker.Net.Bsv;

namespace BsvPoker.App;

public partial class MainWindow : Window
{
    private readonly Profile _profile = new();
    private GameView? _game;
    private ChatView? _chatView;
    private TxLink? _link;   // the ONLY player-to-player transport: it carries nothing but Bitcoin transactions
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _peers = new(); // pubHex -> endpoint, auto-discovered

    public MainWindow()
    {
        InitializeComponent();
        // PER-INSTANCE: each running copy gets its OWN profile (wallet + identity). A 2nd copy is a different player.
        Title = $"BSV Poker — {_profile.Name}";

        var vault = new CardVault(_profile.Dir, _profile.IdentityPriv, _profile.IdentityPub);
        var wallet = new WalletView(_profile.Dir, vault, () => _bsvNode, () => _headerStore, () => _currentNet);
        WalletHost.Content = wallet;
        _wallet = wallet;

        _game = new GameView(_profile.IdentityPriv, _profile.IdentityPub, vault, wallet.RefreshCards,
            playHand: RunLiveHand);   // a hand is a genuine two-party on-chain mental-poker deal vs a real peer
        GameHost.Content = _game;

        // Chat: every message is a Bitcoin transaction; peers are auto-discovered from on-chain Announce txs
        // (no manual key/IP exchange). Send = fund an encrypted ChatDirect tx, push it IP-to-IP to the peer
        // AND broadcast it to miners.
        _chatView = new ChatView(_profile.IdentityPub,
            () => _peers.Select(kv => (kv.Key, kv.Value)).ToList(),
            SendChatTx);
        ChatHost.Content = _chatView;

        LobbyHost.Content = new LobbyView(() => { _game!.StartBot(default); Tabs.SelectedIndex = 2; });
        InitNetworkSelector();

        Loaded += (_, _) =>
        {
            StartBsvNetwork();   // the only Bitcoin-network connection (tx/headers/block) — nothing off-chain
            StartTxLink();       // listen for transactions pushed to us IP-to-IP by other players
            var netRefresh = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            netRefresh.Tick += (_, _) => UpdateNetInfo();
            netRefresh.Start();
            // announce ourselves on-chain so peers auto-discover our key + address (a funded Announce tx)
            var ann = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
            ann.Tick += (_, _) => Announce();
            ann.Start();
            Announce();
        };
        Closed += (_, _) => { try { _bsvNode?.Dispose(); } catch { } try { _link?.Dispose(); } catch { } };
    }

    private WalletView _wallet = null!;

    private void StartTxLink()
    {
        try
        {
            _link = new TxLink(_currentNet, 0); // loopback by default; expose to LAN/Internet is an explicit opt-in
            _link.OnTransaction += Ingest;      // transactions peers push to us directly, IP-to-IP
            _link.Start();
        }
        catch { }
    }

    private string MyEndpoint() => $"{LocalIp()}:{(_link?.Port ?? 0)}";

    /// <summary>Every inbound transaction (pushed IP-to-IP or relayed by the network) is inspected here.</summary>
    private void Ingest(Chain.Tx tx)
    {
        var ann = OnChainAnnounce.TryReadTx(tx);
        if (ann != null)
        {
            var hex = Convert.ToHexString(ann.Pub).ToLowerInvariant();
            if (!hex.Equals(Convert.ToHexString(_profile.IdentityPub), StringComparison.OrdinalIgnoreCase))
                _peers[hex] = ann.Endpoint;     // auto-discovered a peer: key + address, no manual exchange
            return;
        }
        var msg = OnChainChat.TryReadTx(tx, _profile.IdentityPriv, _profile.IdentityPub);
        if (msg == null) return;
        // a live mental-poker deal message from our current opponent is routed to the deal protocol; else it's chat
        if (msg.Text.StartsWith("DEAL:", StringComparison.Ordinal) && _activeDeal != null
            && _activeDeal.PeerPub.AsSpan().SequenceEqual(msg.SenderPub))
            _activeDeal.Deliver(msg.Text["DEAL:".Length..]);
        else
            _chatView?.AddIncoming(Convert.ToHexString(msg.SenderPub).ToLowerInvariant(), msg.Text);
    }

    private TxDealChannel? _activeDeal;

    /// <summary>
    /// Play a hand as a GENUINE two-party on-chain mental-poker deal against a discovered peer — no local or
    /// bot deck, no shared RNG. Both players run <see cref="LiveDeal"/>; every protocol message is an encrypted
    /// Bitcoin transaction pushed IP-to-IP to the peer and to the miners. Returns an immediate status; the
    /// dealt result is shown on the table when the exchange completes.
    /// </summary>
    private const long LiveStake = 20_000;

    private string RunLiveHand()
    {
        if (_activeDeal != null) return "A hand is already in progress.";
        if (_bsvNode == null || _bsvNode.PeerCount == 0) return "Not connected to the BSV network yet.";
        var peer = _peers.FirstOrDefault();
        if (peer.Key == null) return "No opponent discovered yet. A fair deal needs a real peer's entropy (peers appear automatically once they announce). There is NO local or bot deck.";
        byte[] peerPub; try { peerPub = Convert.FromHexString(peer.Key); } catch { return "discovered peer has a bad key."; }
        var seat = _wallet.ReserveSeat(LiveStake + 5000);
        if (seat == null) return $"No spendable coin ≥ {LiveStake + 5000:N0} sat to seat the hand — fund your wallet first (poker is real-money only).";
        var endpoint = peer.Value;
        var ch = new TxDealChannel(peerPub, plaintext => SendEncrypted(peer.Key, endpoint, "DEAL:" + plaintext));
        _activeDeal = ch;
        bool initiator = string.CompareOrdinal(Convert.ToHexString(_profile.IdentityPub).ToLowerInvariant(), peer.Key) < 0;
        var s = seat.Value;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var r = initiator
                    ? LiveHand.RunInitiator(ch, s.Utxo, s.ChangePub, (s.Priv, s.Pub), LiveStake)
                    : LiveHand.RunResponder(ch, s.Utxo, s.ChangePub, (s.Priv, s.Pub), LiveStake);
                _bsvNode?.Broadcast(Chain.Serialize(r.EscrowTx));     // both to miners → on-chain
                _bsvNode?.Broadcast(Chain.Serialize(r.Settlement));
                _wallet.MarkSpent(s.Utxo.Txid, s.Utxo.Vout);
                string holes = string.Join(" ", r.MyHoles.Select(CardStr));
                string board = string.Join(" ", r.Board.Select(CardStr));
                bool iWon = (r.WinnerSeat == 0) == initiator;
                _game?.ShowStatus($"On-chain hand complete — every message was a Bitcoin transaction.\n" +
                                  $"Your hole cards: {holes}\nBoard: {board}\nPot: {r.Pot:N0} sat → {(iWon ? "YOU win" : "opponent wins")}\n" +
                                  $"Shuffle/remask proofs verified: {r.ProofsVerified}. Escrow + settlement broadcast to miners.");
            }
            catch (Exception ex) { _game?.ShowStatus("Hand did not complete: " + ex.Message); }
            finally { _activeDeal = null; }
        });
        return $"Opponent {peer.Key[..12]}… found — playing a real two-party on-chain hand for {LiveStake:N0} sat ({(initiator ? "you deal first" : "opponent deals first")}). Every message is a Bitcoin transaction.";
    }

    private static string CardStr(Card c)
    {
        string r = c.Rank switch { 14 => "A", 13 => "K", 12 => "Q", 11 => "J", 10 => "T", _ => c.Rank.ToString() };
        string s = c.Suit switch { Suit.Spades => "♠", Suit.Hearts => "♥", Suit.Diamonds => "♦", _ => "♣" };
        return r + s;
    }

    /// <summary>Encrypt <paramref name="text"/> to the recipient, fund a ChatDirect tx, push it IP-to-IP + to miners. "" on success.</summary>
    private string SendEncrypted(string recipientPubHex, string endpoint, string text)
    {
        byte[] rpub;
        try { rpub = Convert.FromHexString(recipientPubHex); } catch { return "Recipient public key must be 66 hex chars."; }
        if (rpub.Length != 33) return "Recipient public key must be a 33-byte compressed key.";
        var script = OnChainChat.BuildScript(rpub, _profile.IdentityPub, text);
        var (raw, status) = _wallet.FundTx(script, 1000, 500);
        if (raw == null) return status;
        var (host, port) = ParseHostPort(endpoint);
        if (host != null) _ = TxLink.SendTxAsync(_currentNet, host, port, raw);
        _bsvNode?.Broadcast(raw);
        return "";
    }

    /// <summary>Broadcast our Announce transaction (key + endpoint) so peers discover us automatically.</summary>
    private void Announce()
    {
        try
        {
            var script = OnChainAnnounce.BuildScript(_profile.IdentityPub, MyEndpoint());
            var (raw, _) = _wallet.FundTx(script, 1000, 500);   // an announcement is a real funded tx (needs sats)
            if (raw != null) { _bsvNode?.Broadcast(raw); foreach (var ep in _peers.Values) Push(ep, raw); }
        }
        catch { }
    }

    private void Push(string endpoint, byte[] raw)
    {
        var (host, port) = ParseHostPort(endpoint);
        if (host != null) _ = TxLink.SendTxAsync(_currentNet, host, port, raw);
    }

    private static string LocalIp()
    {
        try
        {
            foreach (var ip in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(ip))
                    return ip.ToString();
        }
        catch { }
        return "127.0.0.1";
    }

    /// <summary>Send a chat message AS a Bitcoin transaction: fund it, push IP-to-IP to the peer, broadcast to miners.</summary>
    private string SendChatTx(string recipientPubHex, string peerHostPort, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Type a message.";
        var err = SendEncrypted(recipientPubHex, peerHostPort, text);
        return err == "" ? $"Sent as a Bitcoin tx — pushed to {peerHostPort} and to miners." : err;
    }

    private static (string? Host, int Port) ParseHostPort(string s)
    {
        var i = s.LastIndexOf(':');
        if (i <= 0 || !int.TryParse(s[(i + 1)..], out var p) || p <= 0) return (null, 0);
        return (s[..i], p);
    }

    private void InitNetworkSelector()
    {
        var file = System.IO.Path.Combine(_profile.Dir, "network.txt");
        int sel = 0; // default MAINNET (Mainnet primary, Testnet backup, Regtest a distant 3rd, never default)
        try { if (System.IO.File.Exists(file) && int.TryParse(System.IO.File.ReadAllText(file).Trim(), out var v) && v is >= 0 and <= 2) sel = v; } catch { }
        NetworkBox.SelectedIndex = sel;
        UpdateNetInfo();
        NetworkBox.SelectionChanged += (_, _) =>
        {
            try { System.IO.File.WriteAllText(file, NetworkBox.SelectedIndex.ToString()); } catch { }
            StartBsvNetwork();
            UpdateNetInfo();
        };
    }

    private BsvNode? _bsvNode;
    private HeaderStore? _headerStore;
    private NetworkParams _currentNet = NetworkParams.For(BsvNetwork.Mainnet);
    private int _storedHeight;

    private void StartBsvNetwork()
    {
        var net = NetworkBox.SelectedIndex switch
        {
            1 => BsvNetwork.Testnet,
            2 => BsvNetwork.Regtest,
            _ => BsvNetwork.Mainnet,
        };
        try { _bsvNode?.Dispose(); } catch { }
        _currentNet = NetworkParams.For(net);
        _bsvNode = new BsvNode(_currentNet);
        _bsvNode.OnRelayedTransaction += Ingest;   // auto-discover peers + receive messages from network-relayed txs
        var storePath = System.IO.Path.Combine(_profile.Dir, $"headers-{net}.dat");
        _headerStore = new HeaderStore(storePath);
        _storedHeight = _headerStore.Count;
        var store = _headerStore;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            var node = _bsvNode;
            await node.StartAsync();
            while (ReferenceEquals(_headerStore, store))
            {
                try
                {
                    if (node.PeerCount > 0)
                    {
                        var (_, height) = await node.SyncHeadersToStoreAsync(store, maxBatches: 4);
                        await Dispatcher.InvokeAsync(() => { if (ReferenceEquals(_headerStore, store)) { _storedHeight = height; UpdateNetInfo(); } });
                    }
                }
                catch { }
                await System.Threading.Tasks.Task.Delay(2000);
            }
        });
        UpdateNetInfo();
    }

    private void UpdateNetInfo()
    {
        var name = (NetworkBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Mainnet";
        // These are BITCOIN NETWORK NODES (blockchain infrastructure) — NOT other players.
        var chain = _bsvNode != null
            ? $"blockchain: {_bsvNode.PeerCount} BSV node(s) · tip {_bsvNode.BestHeight} · validated {_storedHeight}"
            : "blockchain: connecting…";
        NetInfo.Text = $"   {name} · {chain} · transactions-only (no off-chain channel)";
        Title = $"BSV Poker — {_profile.Name} — {name}";
    }
}
