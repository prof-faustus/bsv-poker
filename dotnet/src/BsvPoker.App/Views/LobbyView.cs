using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace BsvPoker.App.Views;

/// <summary>
/// Lobby tab. There is no off-chain table gossip — players are discovered on the poker gossip overlay (each
/// announcement is a real Bitcoin transaction). This shows your identity, your discovered opponents (with their
/// @handles from your Contacts), and lets you start a hand or open your own bot. A hand and every message is a
/// Bitcoin transaction.
/// </summary>
public sealed class LobbyView : UserControl
{
    private Func<IReadOnlyList<(string PubHex, string Endpoint)>>? _peers;
    private Func<string, string?>? _handleFor;
    private readonly TextBlock _idLine = new() { Foreground = Brushes.LightGreen, FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) };
    private readonly ListBox _peerList = new() { Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x12)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)), BorderThickness = new Thickness(1), Height = 200 };

    public LobbyView(Action onPlay, Action onPlayBot)
    {
        Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0D)); Foreground = Brushes.White;
        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock { Text = "Lobby — on-chain, peer-to-peer", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
        root.Children.Add(new TextBlock
        {
            Text = "Every table, hand, and message is a Bitcoin transaction; players are found on the poker gossip " +
                   "overlay. Fund your wallet with real BSV (Wallet tab), then play a discovered peer — or open a " +
                   "bot (a separate automated player linked to your identity that only you can play).",
            Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 12), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left
        });

        root.Children.Add(new TextBlock { Text = "Your identity", Foreground = Brushes.Gray });
        root.Children.Add(_idLine);

        root.Children.Add(new TextBlock { Text = "Discovered players (live):", Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 2) });
        root.Children.Add(_peerList);

        var play = new Button { Content = "Play an on-chain hand", Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 12, 0, 10), Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x6E, 0x2E)), Foreground = Brushes.White, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
        play.Click += (_, _) => onPlay();
        root.Children.Add(play);
        var bot = new Button { Content = "Open a bot (your own, linked to your identity)", Padding = new Thickness(14, 10, 14, 10), Background = new SolidColorBrush(Color.FromRgb(0x6E, 0x2E, 0x3A)), Foreground = Brushes.White, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
        bot.Click += (_, _) => onPlayBot();
        root.Children.Add(bot);
        Content = new ScrollViewer { Content = root };

        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) => RefreshPeers();
        t.Start();
    }

    /// <summary>Wire the lobby to live discovery + the wallet's contact handle resolver + your identity key.</summary>
    public void SetDiscovery(Func<IReadOnlyList<(string, string)>> peers, Func<string, string?> handleFor, string myIdentityHex)
    {
        _peers = peers; _handleFor = handleFor;
        _idLine.Text = myIdentityHex;
        RefreshPeers();
    }

    private void RefreshPeers()
    {
        if (_peers == null) return;
        var cur = _peers().ToList();
        _peerList.Items.Clear();
        foreach (var (pub, ep) in cur) { var h = _handleFor?.Invoke(pub); _peerList.Items.Add((h != null ? "@" + h + "  " : "") + pub[..Math.Min(16, pub.Length)] + "…  @ " + ep); }
        if (cur.Count == 0) _peerList.Items.Add("(no players discovered yet — they appear automatically as they announce on-chain)");
    }
}
