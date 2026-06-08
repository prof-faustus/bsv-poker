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
    private Func<string, string?>? _handleFor;                       // resolve a peer pubkey to a saved contact handle
    private Action<string, string>? _saveContact;                    // (handle, pubHex) -> add to the wallet address book
    private readonly ListBox _peerList = new() { Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x12)), Foreground = Brushes.White, BorderThickness = new Thickness(0), Height = 150 };
    private readonly ListBox _log = new() { Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x0F, 0x0F)), Foreground = Brushes.White, BorderThickness = new Thickness(0), Height = 240 };
    private readonly TextBox _text = new() { Width = 460 };
    private readonly TextBlock _status = new() { Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private List<(string PubHex, string Endpoint)> _current = new();

    /// <summary>Set by MainWindow to the wallet's contact resolver so the peer list can show @handles.</summary>
    public void SetHandleResolver(Func<string, string?> handleFor) => _handleFor = handleFor;
    public void SetSaveContact(Action<string, string> save) => _saveContact = save;

    public ChatView(byte[] myPub, Func<IReadOnlyList<(string, string)>> peers, Func<string, string, string, string> send)
    {
        _peers = peers; _send = send;
        Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0D)); Foreground = Brushes.White;
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = "Chat — every message is a Bitcoin transaction; peers auto-discovered (no manual key exchange)", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap });

        var myHex = Convert.ToHexString(myPub).ToLowerInvariant();
        var idLine = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        idLine.Children.Add(new TextBlock { Text = "Your identity: ", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        idLine.Children.Add(new TextBox { Text = myHex, IsReadOnly = true, Width = 460, FontFamily = new FontFamily("Consolas"), Foreground = Brushes.LightGreen, VerticalAlignment = VerticalAlignment.Center });
        var copyKey = new Button { Content = "Copy", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
        copyKey.Click += (_, _) => { for (int i = 0; i < 5; i++) { try { Clipboard.SetText(myHex); _status.Text = "Identity key copied."; break; } catch { System.Threading.Thread.Sleep(40); } } };
        idLine.Children.Add(copyKey);
        root.Children.Add(idLine);

        root.Children.Add(new TextBlock { Text = "Players discovered automatically (poker gossip overlay):", Foreground = Brushes.Gray, Margin = new Thickness(0, 12, 0, 2) });
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
        var saveC = new Button { Content = "Save selected as contact…", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        saveC.Click += (_, _) =>
        {
            int i = _peerList.SelectedIndex;
            if (i < 0 || i >= _current.Count) { _status.Text = "Select a discovered player first."; return; }
            var pub = _current[i].PubHex;
            var box = new TextBox { Width = 240 };
            var ok = new Button { Content = "Save", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
            var win = new Window { Title = "Save contact", Width = 300, Height = 150, Owner = Window.GetWindow(this), Content = new StackPanel { Margin = new Thickness(12), Children = { new TextBlock { Text = "Handle for this peer:" }, box, ok } } };
            ok.Click += (_, _) => { if (box.Text.Trim().Length > 0) { _saveContact?.Invoke(box.Text.Trim(), pub); _status.Text = $"Saved @{box.Text.Trim()}."; } win.Close(); };
            win.ShowDialog();
        };
        line.Children.Add(saveC);
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
        foreach (var (pub, ep) in _current) { var h = _handleFor?.Invoke(pub); _peerList.Items.Add((h != null ? $"@{h}  " : "") + $"{pub[..Math.Min(16, pub.Length)]}…  @ {ep}"); }
        if (_current.Count == 0) _peerList.Items.Add("(no players discovered yet — they appear automatically once they announce on-chain)");
        if (sel != null) { var idx = _current.FindIndex(p => p.PubHex == sel); if (idx >= 0) _peerList.SelectedIndex = idx; }
    }

    /// <summary>Display a chat message that arrived as a transaction.</summary>
    public void AddIncoming(string senderPubHex, string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => AddIncoming(senderPubHex, text))); return; }
        var h = _handleFor?.Invoke(senderPubHex);
        var who = h != null ? "@" + h : (senderPubHex.Length >= 12 ? senderPubHex[..12] + "…" : senderPubHex);
        _log.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {who}:  {text}");
    }
}
