using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BsvPoker.Core;
using BsvPoker.Net;

namespace BsvPoker.App.Views;

/// <summary>
/// Lobby tab — REAL peer-to-peer. Your own node hosts tables that gossip across the mesh; you connect to
/// another player by their node address (host:port) and see their tables appear; Join opens the table in
/// the Game tab and plays them directly. No server.
/// </summary>
public sealed class LobbyView : UserControl
{
    public sealed class TableRow { public string Id = ""; public string Name { get; set; } = ""; public int Players { get; set; } }

    private readonly P2PNode _node;
    private readonly Action<string, string> _onJoin;
    private readonly ListView _list = new() { Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x0F, 0x0F)), Foreground = Brushes.White, BorderThickness = new Thickness(0), Height = 320 };
    private readonly TextBox _name = new() { Text = "Friday night", Width = 180 };
    private readonly ComboBox _variant = new();
    private readonly ComboBox _seats = new();
    private readonly TextBox _stack = new() { Text = "100", Width = 64, ToolTip = "starting chips per player" };
    private readonly TextBox _bb = new() { Text = "2", Width = 48, ToolTip = "big blind (small blind = half)" };
    private readonly TextBox _peer = new() { Width = 200, ToolTip = "e.g. 192.168.1.50:9700" };
    private readonly TextBlock _nodeInfo = new() { Foreground = Brushes.LightGreen };
    private readonly TextBlock _status = new() { Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private readonly Action<Variant> _onPlayBot;
    private readonly Action _onPlayMyBot;
    private readonly Action<int> _onHostBlackjack;

    /// <summary>The variant currently chosen in the lobby's selector — so "Play MY bot" deals that same game.</summary>
    public Variant SelectedVariant => Variants.All[Math.Max(0, _variant.SelectedIndex)];

    /// <summary>Set by the host: publish THIS node's seed (IP:port + expiry) to the on-chain registry so players
    /// anywhere can discover + TCP-connect to us. Returns a status string. Wired to the "Publish my node" button.</summary>
    public Func<string>? PublishNode { get; set; }

    public LobbyView(P2PNode node, byte[] myPub, Action<string, string> onJoin, Action<Variant> onPlayBot, Action onPlayMyBot, Action<int> onHostBlackjack)
    {
        _node = node; _onJoin = onJoin; _onPlayBot = onPlayBot; _onPlayMyBot = onPlayMyBot; _onHostBlackjack = onHostBlackjack;
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = "Lobby — peer-to-peer (no server)", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
        root.Children.Add(_nodeInfo);

        // PLAY NOW — the bot buttons up front so they are never buried. One click starts a game.
        var quick = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 6) };
        quick.Children.Add(new TextBlock { Text = "Play now:", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var botBtn = Btn("Play a bot", "#6A3FA0"); botBtn.Click += (_, _) => _onPlayBot(Variants.All[Math.Max(0, _variant.SelectedIndex)]);
        quick.Children.Add(botBtn);
        var myBotBtn = Btn("Play MY bot (on-chain)", "#B8860B"); myBotBtn.ToolTip = "Open your own bot — a separate player derived from your identity — and start an on-chain hand.";
        myBotBtn.Click += (_, _) => _onPlayMyBot();
        quick.Children.Add(myBotBtn);
        var bjBtn = Btn("Host Blackjack (group)", "#1F6F43"); bjBtn.ToolTip = "Host a multiplayer Blackjack table (uses the 'players' count) — a shared dealer computed between all players, an n-of-n pot, every card a mental-poker deal. Others join it from the table list. Never one-on-one.";
        bjBtn.Click += (_, _) => _onHostBlackjack(_seats.SelectedIndex >= 0 ? (int)_seats.Items[_seats.SelectedIndex]! : 2);
        quick.Children.Add(bjBtn);
        root.Children.Add(quick);

        var create = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        create.Children.Add(new TextBlock { Text = "Host table ", Foreground = Brushes.Gainsboro, VerticalAlignment = VerticalAlignment.Center });
        create.Children.Add(_name);
        create.Children.Add(new TextBlock { Text = "  game ", Foreground = Brushes.Gainsboro, VerticalAlignment = VerticalAlignment.Center });
        foreach (var v in Variants.All) _variant.Items.Add(Variants.Name(v));
        _variant.SelectedIndex = 0; _variant.Width = 170; _variant.Margin = new Thickness(0, 0, 4, 0);
        create.Children.Add(_variant);
        create.Children.Add(new TextBlock { Text = "  players ", Foreground = Brushes.Gainsboro, VerticalAlignment = VerticalAlignment.Center });
        for (int p = 2; p <= 6; p++) _seats.Items.Add(p);
        _seats.SelectedIndex = 0; _seats.Width = 56; _seats.Margin = new Thickness(0, 0, 4, 0);
        create.Children.Add(_seats);
        create.Children.Add(new TextBlock { Text = "  buy-in ", Foreground = Brushes.Gainsboro, VerticalAlignment = VerticalAlignment.Center });
        create.Children.Add(_stack);
        create.Children.Add(new TextBlock { Text = "  blind ", Foreground = Brushes.Gainsboro, VerticalAlignment = VerticalAlignment.Center });
        create.Children.Add(_bb);
        var createBtn = Btn("Create", "#2E7D32"); createBtn.Click += (_, _) => Create();
        create.Children.Add(createBtn);
        var pubBtn = Btn("Publish my node on-chain", "#8A5A00");
        pubBtn.ToolTip = "Publish your node's address to the on-chain directory (1 sat) so players across the internet discover and connect to you automatically. Local and same-network players already connect with no setup.";
        pubBtn.Click += (_, _) => { _status.Text = PublishNode?.Invoke() ?? "Node publishing is not available."; };
        create.Children.Add(pubBtn);
        root.Children.Add(create);

        // AUTOMATIC: all players connect, all tables broadcast — there is NO "connect to a player". You find
        // players and connect automatically. (An OPTIONAL manual connect is kept for an internet peer behind a
        // firewall, tucked away so it is never the main path.)
        var advanced = new Expander { Header = "Advanced: manually add an internet peer (optional)", Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 8, 0, 0) };
        var advRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        advRow.Children.Add(new TextBlock { Text = "host:port ", Foreground = Brushes.Gainsboro, VerticalAlignment = VerticalAlignment.Center });
        advRow.Children.Add(_peer);
        var dialBtn = Btn("Add peer", "#333333"); dialBtn.Click += (_, _) => Dial();
        advRow.Children.Add(dialBtn);
        advanced.Content = advRow;
        root.Children.Add(advanced);

        root.Children.Add(new TextBlock { Text = "Open tables — every player's table appears here automatically (no setup). Double-click to join.", Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 16, 0, 4), TextWrapping = TextWrapping.Wrap });
        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = "Table", Width = 280, DisplayMemberBinding = new System.Windows.Data.Binding("Name") });
        gv.Columns.Add(new GridViewColumn { Header = "Players", Width = 80, DisplayMemberBinding = new System.Windows.Data.Binding("Players") });
        _list.View = gv;
        _list.MouseDoubleClick += (_, _) => JoinSelected();
        root.Children.Add(_list);

        var joinBtn = Btn("Join selected", "#2E5A7A"); joinBtn.HorizontalAlignment = HorizontalAlignment.Left; joinBtn.Margin = new Thickness(0, 8, 0, 0);
        joinBtn.Click += (_, _) => JoinSelected();
        root.Children.Add(joinBtn);
        root.Children.Add(_status);

        Content = root;
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    public void OnNodeReady(int port) { _nodeInfo.Text = "Your node is LIVE. Players are found AUTOMATICALLY — same network in a few seconds, across the internet via the on-chain directory. You never enter an IP or port. Open tables appear below; double-click to join."; Refresh(); }

    private static Button Btn(string t, string hex) { var c = (Color)ColorConverter.ConvertFromString(hex); return new Button { Content = t, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6), Foreground = Brushes.White, BorderThickness = new Thickness(0), Background = new SolidColorBrush(c) }; }

    private void Create()
    {
        var v = Variants.All[Math.Max(0, _variant.SelectedIndex)];
        int seats = _seats.SelectedIndex >= 0 ? (int)_seats.Items[_seats.SelectedIndex]! : 2;
        long stack = long.TryParse(_stack.Text.Trim(), out var s) && s >= 2 ? s : 100;
        long bb = long.TryParse(_bb.Text.Trim(), out var b) && b >= 2 ? b : 2;
        if (bb > stack) bb = stack;
        // encode the variant, seat count and stakes in the id so peers agree without extra messages
        var id = "t-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant() + "~" + v + "~p" + seats + "~s" + stack + "~b" + bb;
        _ = _node.CreateTableAsync(id, _name.Text.Trim());
        _status.Text = $"Hosting '{_name.Text.Trim()}' ({Variants.Name(v)}, {seats} players, {stack} chips, blind {bb}). You are seated — waiting for players to join.";
        Refresh();
        // A HOST IS A PLAYER. Creating a table SEATS you in it immediately (opens the Game tab and joins the
        // networked hand), so you appear at the table and the game starts the moment the other seats fill — you
        // never have to "join your own table". Without this the creator only advertised the table and was never
        // actually in the game (so a joiner like Charlie sat waiting for a host who never appeared).
        _onJoin(id, _name.Text.Trim());
    }

    private void Dial()
    {
        var parts = _peer.Text.Trim().Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port)) { _status.Text = "Enter peer as host:port (e.g. 192.168.1.50:9700)."; return; }
        _node.Dial(new P2PNode.PeerAddr(parts[0], port));
        _status.Text = $"Connecting to {_peer.Text.Trim()}… their tables will appear below.";
    }

    private void JoinSelected()
    {
        if (_list.SelectedItem is TableRow r) { _status.Text = $"Joining '{r.Name}'…"; _onJoin(r.Id, r.Name); }
    }

    private void Refresh()
    {
        var sel = (_list.SelectedItem as TableRow)?.Id;
        var rows = _node.ListTables().Select(t => new TableRow { Id = t.id, Name = $"{t.name}  ·  {(IsBlackjack(t.id) ? "Blackjack (group)" : Variants.Name(VariantOf(t.id)))}  ·  {SeatsOf(t.id)}-max", Players = t.members }).ToList();
        _list.ItemsSource = rows;
        if (sel != null) _list.SelectedItem = rows.FirstOrDefault(x => x.Id == sel);
        // SHOW the live connection so "I can't see any nodes" is answered with a real number: how many nodes we are
        // connected to right now, and how many open tables we can see. Updates every second (the lobby timer).
        int peers = _node.PeerCount;
        _nodeInfo.Text = peers > 0
            ? $"● LIVE — connected to {peers} node(s); {rows.Count} open table(s) visible. Double-click a table to join. (Players are found automatically — no IP needed.)"
            : "● LIVE — looking for players… same machine connects instantly, same network within a few seconds. If players on OTHER machines don't appear, allow “poker” through Windows Firewall (private networks).";
    }

    private static bool IsBlackjack(string id) => id.Contains("~bj~", StringComparison.Ordinal) || id.EndsWith("~bj", StringComparison.Ordinal);
    private static Variant VariantOf(string id) { try { var p = id.Split('~'); return p.Length > 1 ? Variants.Parse(p[1]) : Variant.TexasHoldem; } catch { return Variant.TexasHoldem; } }
    private static int SeatsOf(string id) { foreach (var p in id.Split('~')) if (p.StartsWith("p") && int.TryParse(p[1..], out var n)) return n; return 2; }
}
