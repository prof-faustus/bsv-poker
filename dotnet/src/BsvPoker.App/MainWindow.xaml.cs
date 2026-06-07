using System.Windows;
using BsvPoker.App.Views;
using BsvPoker.Core;
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
            onChainSettle: (deck, pot) => wallet.PlayOnChainHand(deck, pot), // a hand is real BSV transactions only
            canFund: stake => wallet.CanPlayOnChain(stake));                 // refuse to start without real sats
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
        if (msg != null) _chatView?.AddIncoming(Convert.ToHexString(msg.SenderPub).ToLowerInvariant(), msg.Text);
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
        byte[] rpub;
        try { rpub = Convert.FromHexString(recipientPubHex); } catch { return "Recipient public key must be 66 hex chars."; }
        if (rpub.Length != 33) return "Recipient public key must be a 33-byte compressed key (66 hex).";
        var script = OnChainChat.BuildScript(rpub, _profile.IdentityPub, text);
        var (raw, status) = _wallet.FundTx(script, 1000, 500);
        if (raw == null) return status;
        var (host, port) = ParseHostPort(peerHostPort);
        if (host != null) _ = TxLink.SendTxAsync(_currentNet, host, port, raw);   // IP-to-IP straight to the player
        _bsvNode?.Broadcast(raw);                                                 // and to the mining nodes → on-chain
        return $"Sent as a Bitcoin tx — pushed to {peerHostPort} and broadcast to miners.";
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
