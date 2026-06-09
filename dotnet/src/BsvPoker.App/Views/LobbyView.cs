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
    private readonly TextBlock _status = new() { Foreground = Brushes.Gray, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private readonly Action<Variant> _onPlayBot;

    public LobbyView(P2PNode node, byte[] myPub, Action<string, string> onJoin, Action<Variant> onPlayBot)
    {
        _node = node; _onJoin = onJoin; _onPlayBot = onPlayBot;
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = "Lobby — peer-to-peer (no server)", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
        root.Children.Add(_nodeInfo);

        var create = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        create.Children.Add(new TextBlock { Text = "Host table ", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        create.Children.Add(_name);
        create.Children.Add(new TextBlock { Text = "  game ", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        foreach (var v in Variants.All) _variant.Items.Add(Variants.Name(v));
        _variant.SelectedIndex = 0; _variant.Width = 170; _variant.Margin = new Thickness(0, 0, 4, 0);
        create.Children.Add(_variant);
        create.Children.Add(new TextBlock { Text = "  players ", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        for (int p = 2; p <= 6; p++) _seats.Items.Add(p);
        _seats.SelectedIndex = 0; _seats.Width = 56; _seats.Margin = new Thickness(0, 0, 4, 0);
        create.Children.Add(_seats);
        create.Children.Add(new TextBlock { Text = "  buy-in ", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        create.Children.Add(_stack);
        create.Children.Add(new TextBlock { Text = "  blind ", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        create.Children.Add(_bb);
        var createBtn = Btn("Create", "#2E7D32"); createBtn.Click += (_, _) => Create();
        create.Children.Add(createBtn);
        create.Children.Add(new TextBlock { Text = "    Connect to player ", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        create.Children.Add(_peer);
        var botBtn = Btn("Play a bot", "#6A3FA0"); botBtn.Click += (_, _) => _onPlayBot(Variants.All[Math.Max(0, _variant.SelectedIndex)]);
        create.Children.Add(botBtn);
        var dialBtn = Btn("Connect", "#333333"); dialBtn.Click += (_, _) => Dial();
        create.Children.Add(dialBtn);
        root.Children.Add(create);

        root.Children.Add(new TextBlock { Text = "Open tables on the mesh", Foreground = Brushes.Gray, Margin = new Thickness(0, 16, 0, 4) });
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

    public void OnNodeReady(int port) { _nodeInfo.Text = $"Your node is live on port {port} — share your IP:{port} so others can Connect to you."; Refresh(); }

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
        _status.Text = $"Hosting '{_name.Text.Trim()}' ({Variants.Name(v)}, {seats} players, {stack} chips, blind {bb}). Join it (or wait for players) to play.";
        Refresh();
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
        var rows = _node.ListTables().Select(t => new TableRow { Id = t.id, Name = $"{t.name}  ·  {Variants.Name(VariantOf(t.id))}  ·  {SeatsOf(t.id)}-max", Players = t.members }).ToList();
        _list.ItemsSource = rows;
        if (sel != null) _list.SelectedItem = rows.FirstOrDefault(x => x.Id == sel);
    }

    private static Variant VariantOf(string id) { var p = id.Split('~'); return p.Length > 1 ? Variants.Parse(p[1]) : Variant.TexasHoldem; }
    private static int SeatsOf(string id) { foreach (var p in id.Split('~')) if (p.StartsWith("p") && int.TryParse(p[1..], out var n)) return n; return 2; }
}
