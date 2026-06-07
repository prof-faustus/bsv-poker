using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace BsvPoker.App.Views;

/// <summary>
/// Chat where every message is a Bitcoin transaction AND peers are discovered automatically — there is no
/// manual key or IP exchange. Your client announces itself with an on-chain Announce transaction; other
/// players' clients do the same, so the peer list fills in by itself (public key + IP learned from their
/// Announce transactions). To message someone you pick them from the list and type; the app builds an
/// encrypted ChatDirect transaction, pushes it IP-to-IP to them, and broadcasts the same tx to the miners.
/// </summary>
public sealed class ChatView : UserControl
{
    private readonly Func<IReadOnlyList<(string PubHex, string Endpoint)>> _peers;
    private readonly Func<string, string, string, string> _send;   // (recipientPubHex, endpoint, text) -> status
    private readonly ListBox _peerList = new() { Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x12)), Foreground = Brushes.White, BorderThickness = new Thickness(0), Height = 150 };
    private readonly ListBox _log = new() { Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x0F, 0x0F)), Foreground = Brushes.White, BorderThickness = new Thickness(0), Height = 240 };
    private readonly TextBox _text = new() { Width = 460 };
    private readonly TextBlock _status = new() { Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private List<(string PubHex, string Endpoint)> _current = new();

    public ChatView(byte[] myPub, Func<IReadOnlyList<(string, string)>> peers, Func<string, string, string, string> send)
    {
        _peers = peers; _send = send;
        Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0D)); Foreground = Brushes.White;
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = "Chat — every message is a Bitcoin transaction; peers auto-discovered (no manual key exchange)", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap });

        root.Children.Add(new TextBlock { Text = "Players discovered automatically (from their on-chain Announce transactions):", Foreground = Brushes.Gray, Margin = new Thickness(0, 12, 0, 2) });
        root.Children.Add(_peerList);

        root.Children.Add(new TextBlock { Text = "Messages received (transactions peers pushed to you):", Foreground = Brushes.Gray, Margin = new Thickness(0, 12, 0, 2) });
        root.Children.Add(_log);

        var line = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        line.Children.Add(new TextBlock { Text = "Message ", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(_text);
        var sendBtn = new Button { Content = "Send (as a Bitcoin tx)", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        sendBtn.Click += (_, _) =>
        {
            int i = _peerList.SelectedIndex;
            if (i < 0 || i >= _current.Count) { _status.Text = "Select a discovered player above first."; return; }
            _status.Text = _send(_current[i].PubHex, _current[i].Endpoint, _text.Text);
            _text.Clear();
        };
        line.Children.Add(sendBtn);
        root.Children.Add(line);
        root.Children.Add(_status);

        Content = new ScrollViewer { Content = root };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => RefreshPeers();
        timer.Start();
        RefreshPeers();
    }

    private void RefreshPeers()
    {
        var sel = _peerList.SelectedIndex >= 0 && _peerList.SelectedIndex < _current.Count ? _current[_peerList.SelectedIndex].PubHex : null;
        _current = _peers().ToList();
        _peerList.Items.Clear();
        foreach (var (pub, ep) in _current) _peerList.Items.Add($"{pub[..Math.Min(16, pub.Length)]}…  @ {ep}");
        if (_current.Count == 0) _peerList.Items.Add("(no players discovered yet — they appear automatically once they announce on-chain)");
        if (sel != null) { var idx = _current.FindIndex(p => p.PubHex == sel); if (idx >= 0) _peerList.SelectedIndex = idx; }
    }

    /// <summary>Display a chat message that arrived as a transaction.</summary>
    public void AddIncoming(string senderPubHex, string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => AddIncoming(senderPubHex, text))); return; }
        var who = senderPubHex.Length >= 12 ? senderPubHex[..12] + "…" : senderPubHex;
        _log.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {who}:  {text}");
    }
}
