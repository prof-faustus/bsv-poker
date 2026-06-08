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
    private PokerGossip? _gossip;   // the poker discovery overlay (announce/forward/query) on top of Bitcoin

    public MainWindow()
    {
        InitializeComponent();
        // PER-INSTANCE: each running copy gets its OWN profile (wallet + identity). A 2nd copy is a different player.
        Title = $"BSV Poker — {_profile.Name}";

        var vault = new CardVault(_profile.Dir, _profile.IdentityPriv, _profile.IdentityPub);
        // ONE identity across wallet + chat + game + NFTs: the wallet pays/encrypts/claims with the SAME Base ID
        // key the chat and game use, and NFTs are sealed to it. Fully integrated, not separate identities.
        var wallet = new WalletView(_profile.Dir, vault, () => _bsvNode, () => _headerStore, () => _currentNet, _profile.IdentityPriv, _profile.IdentityPub);
        WalletHost.Content = wallet;
        _wallet = wallet;
        _wallet.RescanRequested = SpvRescan;   // the wallet's "Rescan for my payments now" button

        _game = new GameView(_profile.IdentityPriv, _profile.IdentityPub, vault, wallet.RefreshCards,
            playHand: RunLiveHand);   // a hand is a genuine two-party on-chain mental-poker deal vs a real peer
        GameHost.Content = _game;

        // Chat: every message is a Bitcoin transaction; peers are auto-discovered from on-chain Announce txs
        // (no manual key/IP exchange). Send = fund an encrypted ChatDirect tx, push it IP-to-IP to the peer
        // AND broadcast it to miners.
        _chatView = new ChatView(_profile.IdentityPub,
            () => (_gossip?.Peers ?? new List<PokerGossip.Peer>()).Select(p => (p.PubHex, p.Endpoint)).ToList(),
            SendChatTx);
        _chatView.SetHandleResolver(wallet.IdentityLabelFor);   // chat shows @handles from the wallet's Contacts
        _chatView.SetSaveContact(wallet.ImportContact);  // save a discovered peer into the wallet address book
        ChatHost.Content = _chatView;

        var lobby = new LobbyView(
            () => { _game!.StartBot(default); Tabs.SelectedIndex = 2; },
            PlayBot);
        lobby.SetDiscovery(
            () => (_gossip?.Peers ?? new List<PokerGossip.Peer>()).Select(p => (p.PubHex, p.Endpoint)).ToList(),
            wallet.IdentityLabelFor,
            Convert.ToHexString(_profile.IdentityPub).ToLowerInvariant());
        LobbyHost.Content = lobby;
        InitNetworkSelector();

        Loaded += (_, _) =>
        {
            StartBsvNetwork();   // the only Bitcoin-network connection (tx/headers/block) — nothing off-chain
            _wallet.Refresh();   // the receive address is network-aware; show it for the loaded network
            StartTxLink();       // listen for transactions pushed to us IP-to-IP by other players
            var netRefresh = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            netRefresh.Tick += (_, _) => UpdateNetInfo();
            netRefresh.Start();
            // SPV discovery: periodically pull the mempool (instant detect of a just-sent payment) and rescan
            // recent blocks with our filter (catches a payment that confirmed before we were connected).
            var spvScan = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            spvScan.Tick += (_, _) => SpvRescan();
            spvScan.Start();
            // re-arm the SPV filter the instant the wallet is unlocked (it needs the seed to derive our addresses)
            _wallet.OnUnlocked += () => { ApplySpvFilter(); SpvRescan(); };
            // announce ourselves on-chain so peers auto-discover our key + address (a funded Announce tx)
            var ann = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
            ann.Tick += (_, _) => Announce();
            ann.Start();
            Announce();
        };
        Closed += (_, _) => { foreach (var w in _botWindows.ToList()) { try { w.Close(); } catch { } } foreach (var b in _bots.ToList()) { try { b.Dispose(); } catch { } } try { _bsvNode?.Dispose(); } catch { } try { _link?.Dispose(); } catch { } };
    }

    private WalletView _wallet = null!;

    private void StartTxLink()
    {
        try
        {
            _link = new TxLink(_currentNet, 0); // loopback by default; expose to LAN/Internet is an explicit opt-in
            _link.OnTransaction += Ingest;      // transactions peers push to us directly, IP-to-IP
            _link.Start();
            var myHex = Convert.ToHexString(_profile.IdentityPub).ToLowerInvariant();
            _gossip = new PokerGossip(myHex, MyEndpoint(),
                (peerPub, endpoint, msg) => SendEncrypted(peerPub, endpoint, "GOSSIP:" + msg)); // gossip rides on Bitcoin txs
            _gossip.OnPeersChanged += () => Dispatcher.BeginInvoke(new Action(UpdateNetInfo));
        }
        catch { }
    }

    private string MyEndpoint() => $"{LocalIp()}:{(_link?.Port ?? 0)}";

    /// <summary>Every inbound transaction (pushed IP-to-IP or relayed by the network) is inspected here.</summary>
    private void Ingest(Chain.Tx tx)
    {
        // an Announce tx (relayed) bootstraps the gossip overlay with a first peer
        var ann = OnChainAnnounce.TryReadTx(tx);
        if (ann != null)
        {
            var hex = Convert.ToHexString(ann.Pub).ToLowerInvariant();
            if (!hex.Equals(Convert.ToHexString(_profile.IdentityPub), StringComparison.OrdinalIgnoreCase))
                _gossip?.AddSeed(hex, ann.Endpoint);
            return;
        }
        // detect an incoming PAYMENT to one of our addresses (shows as pending until SPV-confirmed)
        _wallet.ConsiderIncoming(tx);
        var msg = OnChainChat.TryReadTx(tx, _profile.IdentityPriv, _profile.IdentityPub);
        if (msg == null) return;
        if (msg.Text.StartsWith("GOSSIP:", StringComparison.Ordinal))      // poker discovery overlay
            _gossip?.Receive(msg.Text["GOSSIP:".Length..]);
        else if (msg.Text.StartsWith("DEAL:", StringComparison.Ordinal) && _activeDeal != null
                 && _activeDeal.PeerPub.AsSpan().SequenceEqual(msg.SenderPub))
            _activeDeal.Deliver(msg.Text["DEAL:".Length..]);             // live hand protocol
        else
            _chatView?.AddIncoming(Convert.ToHexString(msg.SenderPub).ToLowerInvariant(), msg.Text);
    }

    private TxDealChannel? _activeDeal;
    private readonly List<BotPlayer> _bots = new();        // Alice can run as MANY of her own bots as she likes
    private readonly List<BotWindow> _botWindows = new();

    /// <summary>
    /// Open one of MY bots: a SEPARATE automated player (its own identity DERIVED from mine, wallet, TxLink, and
    /// gossip node) in its own small window. Alice can open as many as she wants and play them; each bot only ever
    /// plays its owner (Alice). Cross-seeds gossip so I discover the bot; "Play on-chain hand" → choose the bot →
    /// real two-party mental-poker hand. Bots are for testing and soft play and are always available.
    /// </summary>
    private int _botCount;

    private void PlayBot()
    {
        // the bot is DERIVED from MY identity (Type-42) and named <my-handle>-Bot-NNN; it will only ever play me.
        var ownerHandle = string.IsNullOrWhiteSpace(_wallet.MyHandle) ? _profile.Name.Replace(" ", "") : _wallet.MyHandle;
        var bot = new BotPlayer(_currentNet, LocalIp(), _profile.IdentityPriv, _profile.IdentityPub, ++_botCount, ownerHandle);
        var myHex = Convert.ToHexString(_profile.IdentityPub).ToLowerInvariant();
        bot.AddPeer(myHex, MyEndpoint());                  // the bot knows how to reach us
        _gossip?.AddSeed(bot.PubHex, bot.Endpoint);        // we know how to reach the bot
        var win = new BotWindow(bot) { Owner = this };
        _bots.Add(bot); _botWindows.Add(win);
        win.Closed += (_, _) => { _bots.Remove(bot); _botWindows.Remove(win); UpdateNetInfo(); };
        win.Show();
        bot.Announce();
        UpdateNetInfo();
    }

    /// <summary>
    /// Play a hand as a GENUINE two-party on-chain mental-poker deal against a discovered peer — no local or
    /// bot deck, no shared RNG. Both players run <see cref="LiveDeal"/>; every protocol message is an encrypted
    /// Bitcoin transaction pushed IP-to-IP to the peer and to the miners. Returns an immediate status; the
    /// dealt result is shown on the table when the exchange completes.
    /// </summary>
    private const long LiveStake = 20_000;

    /// <summary>Let the HUMAN choose which discovered peer to play (never auto-selected). Returns null if cancelled.</summary>
    private PokerGossip.Peer? ChooseOpponent(List<PokerGossip.Peer> peers)
    {
        if (!Dispatcher.CheckAccess()) return Dispatcher.Invoke(() => ChooseOpponent(peers));
        var list = new System.Windows.Controls.ListBox { Height = 200, Width = 460 };
        foreach (var p in peers) { var h = _wallet.IdentityLabelFor(p.PubHex); list.Items.Add((h != null ? h + "  " : "") + p.PubHex[..Math.Min(16, p.PubHex.Length)] + "…  @ " + p.Endpoint); }
        list.SelectedIndex = 0;
        var ok = new System.Windows.Controls.Button { Content = "Play this opponent", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        var sp = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new System.Windows.Controls.TextBlock { Text = "Choose your opponent (you select every action):" });
        sp.Children.Add(list); sp.Children.Add(ok);
        var win = new Window { Title = "Choose opponent", Width = 500, Height = 300, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = sp };
        PokerGossip.Peer? chosen = null;
        ok.Click += (_, _) => { if (list.SelectedIndex >= 0) chosen = peers[list.SelectedIndex]; win.Close(); };
        win.ShowDialog();
        return chosen;
    }

    private string RunLiveHand()
    {
        // STANDALONE SPV: there is NO "connect to the BSV network" requirement. A hand is peer-to-peer
        // (transactions handed IP-to-IP to the opponent) and the same txs are sent to a miner to land on-chain.
        if (_activeDeal != null) return "A hand is already in progress.";
        var peers = _gossip?.Peers.ToList() ?? new();
        if (peers.Count == 0) return "No opponent discovered yet — the gossip overlay is still finding poker peers. A fair deal needs a real peer's entropy; there is NO local or bot deck.";
        var peer = ChooseOpponent(peers);   // the HUMAN picks the opponent — never auto-selected
        if (peer == null) return "No opponent chosen.";
        byte[] peerPub; try { peerPub = Convert.FromHexString(peer.PubHex); } catch { return "discovered peer has a bad key."; }
        var seat = _wallet.ReserveSeat(LiveStake + 5000);
        if (seat == null) return $"No spendable coin ≥ {LiveStake + 5000:N0} sat to seat the hand — fund your wallet first (poker is real-money only).";
        var endpoint = peer.Endpoint;
        var peerHex = peer.PubHex;
        var ch = new TxDealChannel(peerPub, plaintext => SendEncrypted(peerHex, endpoint, "DEAL:" + plaintext));
        _activeDeal = ch;
        bool initiator = string.CompareOrdinal(Convert.ToHexString(_profile.IdentityPub).ToLowerInvariant(), peerHex) < 0;
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
                // every card dealt to me becomes a REAL on-chain encrypted NFT sealed to my identity (in my wallet)
                var mintStatus = _wallet.MintCardNftsOnChain(r.MyHoles.Select(c => c.Index).ToList());
                bool iWon = (r.WinnerSeat == 0) == initiator;
                var oppName = _wallet.IdentityLabelFor(peerHex) ?? peerHex[..12] + "…";
                _game?.ShowHand(r.MyHoles, r.Board, oppName,
                    $"Pot {r.Pot:N0} sat → {(iWon ? "YOU win" : "opponent wins")} · proofs verified: {r.ProofsVerified} · NFTs: {mintStatus}");
            }
            catch (Exception ex) { _game?.ShowStatus("Hand did not complete: " + ex.Message); }
            finally { _activeDeal = null; }
        });
        return $"Opponent {peerHex[..12]}… found — playing a real two-party on-chain hand for {LiveStake:N0} sat ({(initiator ? "you deal first" : "opponent deals first")}). Every message is a Bitcoin transaction.";
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

    /// <summary>
    /// Announce ourselves so peers discover us: (1) a bootstrap Announce transaction relayed on the Bitcoin
    /// network (seeds nodes that don't know us yet), and (2) the poker gossip overlay's announce/query, which
    /// floods our presence across the overlay and pulls peers others know.
    /// </summary>
    private void Announce()
    {
        try
        {
            var script = OnChainAnnounce.BuildScript(_profile.IdentityPub, MyEndpoint());
            var (raw, _) = _wallet.FundTx(script, 1000, 500);   // a bootstrap announcement is a real funded tx
            if (raw != null) _bsvNode?.Broadcast(raw);
        }
        catch { }
        _gossip?.Announce();   // poker overlay: announce to known peers (they forward it onward)
        _gossip?.Query();      // and pull the peers they know
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
        // ALWAYS start on Mainnet, every launch — Mainnet is primary, Testnet the backup, Regtest a distant 3rd
        // and NEVER the default. The selected network is deliberately NOT persisted: the app always opens on
        // Mainnet so it can never silently come up on a test network.
        var file = System.IO.Path.Combine(_profile.Dir, "network.txt");
        try { if (System.IO.File.Exists(file)) System.IO.File.Delete(file); } catch { } // purge any stale saved choice
        NetworkBox.SelectedIndex = 0; // Mainnet
        UpdateNetInfo();
        NetworkBox.SelectionChanged += (_, _) =>
        {
            StartBsvNetwork();   // sets _currentNet from the selection (session-only; not persisted)
            _wallet.Refresh();   // re-render the network-aware receive address for the newly selected network
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
        _bsvNode.OnConfirmedTransaction += (tx, mb) => _wallet.ConfirmIncoming(tx, mb); // SPV-proven payment → credit it
        ApplySpvFilter();                          // load our addresses into the node so peers relay our own txs back
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

    /// <summary>
    /// Build the SPV bloom filter from the wallet's own addresses and load it into the node, so peers relay our
    /// own transactions (incoming payments and our spends) back to us — the no-server way the wallet sees the
    /// chain. Does nothing until the wallet is unlocked (the seed is needed to derive the addresses).
    /// </summary>
    private void ApplySpvFilter()
    {
        if (_bsvNode == null || !_wallet.IsUnlocked) return;
        try
        {
            var elems = _wallet.FilterElements();
            var tweak = System.BitConverter.ToUInt32(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
            var filter = new BloomFilter(elems.Count, 0.00001, tweak);
            foreach (var e in elems) filter.Insert(e);
            _bsvNode.SetSpvFilter(filter);   // pushes filterload to peers + pulls their mempools
        }
        catch { }
    }

    /// <summary>
    /// Pull peer mempools and rescan recent blocks with our filter so a payment is discovered whether it is
    /// still unconfirmed OR confirmed before we connected. Matching txs come back with a merkleblock proof we
    /// verify against our own headers in <see cref="WalletView.ConfirmIncoming"/>.
    /// </summary>
    private void SpvRescan()
    {
        if (_bsvNode == null || _bsvNode.PeerCount == 0 || !_wallet.IsUnlocked) return;
        try
        {
            _bsvNode.RequestMempool();
            var store = _headerStore;
            if (store != null && store.Count > 0)
            {
                var (chain, _) = store.BuildChain();
                var recent = chain.Recent(2000).Select(e => e.HashHex).ToList();   // last ~2 weeks of blocks
                if (recent.Count > 0) _bsvNode.RequestFilteredBlocks(recent);
            }
        }
        catch { }
    }

    private void UpdateNetInfo()
    {
        var name = (NetworkBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Mainnet";
        // STANDALONE SPV: the client connects to no node network. Payments arrive as SPV envelopes (merkle
        // proof + header) IP-to-IP; players are discovered peer-to-peer. There is no "connected" state.
        int players = _gossip?.Peers.Count ?? 0;
        NetInfo.Text = $"   {name} · SPV (online) · poker gossip overlay · players discovered: {players}";
        Title = $"BSV Poker — {_profile.Name} — {name}";
    }
}
