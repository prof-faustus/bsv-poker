using System.Windows;
using BsvPoker.App.Views;
using BsvPoker.Net;

namespace BsvPoker.App;

public partial class MainWindow : Window
{
    private readonly Profile _profile = new();
    private readonly P2PNode _node = new(0, "127.0.0.1"); // loopback by default; LAN is an explicit opt-in
    private GameView? _game;

    public MainWindow()
    {
        InitializeComponent();
        // PER-INSTANCE: each running copy gets its OWN profile (wallet + identity). A 2nd copy is a
        // DIFFERENT player, not a clone.
        Title = $"BSV Poker — {_profile.Name}";

        var vault = new CardVault(_profile.Dir, _profile.IdentityPriv, _profile.IdentityPub);
        var wallet = new WalletView(_profile.Dir, vault);
        WalletHost.Content = wallet;
        var chat = new ChatService(_node, _profile.IdentityPriv, _profile.IdentityPub, _profile.Dir);
        ChatHost.Content = new ChatView(chat);
        _game = new GameView(_node, _profile.IdentityPriv, _profile.IdentityPub, vault, wallet.RefreshCards);
        _game.OnLeaveTable += () => { try { _botWindow?.Close(); _bot?.Dispose(); _bot = null; _botWindow = null; } catch { } };
        GameHost.Content = _game;

        var lobby = new LobbyView(_node, _profile.IdentityPub, JoinTable, PlayBot);
        LobbyHost.Content = lobby;
        InitNetworkSelector();

        Loaded += async (_, _) =>
        {
            _node.SetIdentity(_profile.IdentityPriv, _profile.IdentityPub); // sign presence/table announcements
            await _node.StartAsync();
            // announce the FULL pubkey as presence so peers can DM us (and find us in the lobby).
            await _node.HeartbeatAsync(Convert.ToHexString(_profile.IdentityPub).ToLowerInvariant(), $"127.0.0.1:{_node.BoundPort}");
            lobby.OnNodeReady(_node.BoundPort);

            // LOCAL auto-discovery: two copies on one machine find and connect to each other (no manual IP).
            var disc = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            disc.Tick += (_, _) =>
            {
                LocalDiscovery.Register(_node.BoundPort);
                foreach (var port in LocalDiscovery.Peers(_node.BoundPort))
                    if (_dialed.Add(port)) _node.Dial(new BsvPoker.Net.P2PNode.PeerAddr("127.0.0.1", port));
            };
            disc.Start();
            LocalDiscovery.Register(_node.BoundPort);

            StartBsvNetwork(); // connect to the live BSV network for the selected network
            var netRefresh = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            netRefresh.Tick += (_, _) => UpdateNetInfo();
            netRefresh.Start();
        };
        Closed += (_, _) => { try { _bsvNode?.Dispose(); } catch { } try { _node.Dispose(); } catch { } };
    }

    private readonly HashSet<int> _dialed = new();

    private void InitNetworkSelector()
    {
        var file = System.IO.Path.Combine(_profile.Dir, "network.txt");
        int sel = 0; // default MAINNET (Mainnet primary, Testnet backup, Regtest a distant 3rd never default)
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

    private BsvPoker.Net.Bsv.BsvNode? _bsvNode;

    private void StartBsvNetwork()
    {
        var net = NetworkBox.SelectedIndex switch
        {
            1 => BsvPoker.Net.Bsv.BsvNetwork.Testnet,
            2 => BsvPoker.Net.Bsv.BsvNetwork.Regtest,
            _ => BsvPoker.Net.Bsv.BsvNetwork.Mainnet,
        };
        try { _bsvNode?.Dispose(); } catch { }
        _bsvNode = new BsvPoker.Net.Bsv.BsvNode(BsvPoker.Net.Bsv.NetworkParams.For(net));
        _ = _bsvNode.StartAsync();   // resolve DNS seeds and connect to live BSV peers
        UpdateNetInfo();
    }

    private void UpdateNetInfo()
    {
        var name = (NetworkBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Mainnet";
        var live = _bsvNode != null ? $" · {_bsvNode.PeerCount} BSV peers · height {_bsvNode.BestHeight}" : "";
        NetInfo.Text = $"   {name}{live} · peer-to-peer";
        Title = $"BSV Poker — {_profile.Name} — {name}";
    }

    private void JoinTable(string tableId, string tableName)
    {
        _game?.StartNetworked(tableId, tableName);
        Tabs.SelectedIndex = 2; // switch to the Game tab
    }

    private BotPlayer? _bot;
    private BotWindow? _botWindow;

    private void PlayBot(BsvPoker.Core.Variant variant)
    {
        // A bot is a SEPARATE automated player on its OWN node + identity, NOT a hot-seat clone and NOT a
        // second copy of the app. The human joins the table on the main node; the bot joins the same table
        // on its own loopback node and plays itself in a small, distinct, offset window.
        _botWindow?.Close(); _bot?.Dispose();
        var tableId = "t-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant() + "~" + variant + "~p2";
        _game?.StartNetworked(tableId, "vs Bot");
        Tabs.SelectedIndex = 2; // Game tab (the human)
        _bot = new BotPlayer(_node.BoundPort, tableId);
        _botWindow = new BotWindow(_bot) { Owner = this };
        _botWindow.Show();
    }

    protected override void OnClosed(EventArgs e) { try { _botWindow?.Close(); _bot?.Dispose(); } catch { } base.OnClosed(e); }
}
