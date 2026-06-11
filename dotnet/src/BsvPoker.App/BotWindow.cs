using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BsvPoker.Net.Bsv;

namespace BsvPoker.App;

/// <summary>
/// The bot's OWN small, distinct window — deliberately different from the main table (smaller, dark-crimson,
/// docked bottom-right): it shows ONLY the bot (its identity, address, balance, and a live log of what it is
/// doing), never the game board, and never sits over the main window. The bot is a separate automated player;
/// this window is its console. Closing it refunds the bot's funder.
/// </summary>
public sealed class BotWindow : Window
{
    private readonly BotPlayer _bot;
    private readonly ListBox _log = new() { Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x0B, 0x0E)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
    private readonly TextBlock _bal = new() { Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.Bold };

    public BotWindow(BotPlayer bot)
    {
        _bot = bot;
        Title = bot.Name + " (your bot)";
        Width = 460; Height = 620;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x0E, 0x12)); // distinct dark-crimson, never the main felt
        // dock bottom-right, offset from the main window
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12; Top = area.Bottom - Height - 12;

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock { Text = $"🤖 {_bot.Name} — your bot (only you can play it)", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
        root.Children.Add(new TextBlock { Text = "Identity:", Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 8, 0, 0) });
        root.Children.Add(new TextBox { Text = _bot.PubHex, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), Foreground = Brushes.Pink, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(new TextBlock { Text = $"Address (fund the bot here): {_bot.ReceiveAddress()}", Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        root.Children.Add(new TextBlock { Text = $"On the overlay at: {_bot.Endpoint}", Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 4, 0, 0) });
        root.Children.Add(new TextBlock { Text = "Balance (confirmed):", Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 8, 0, 0) });
        root.Children.Add(_bal);

        var importBtn = new Button { Content = "Fund the bot (SPV envelope)…", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        importBtn.Click += (_, _) => ImportFunding();
        root.Children.Add(importBtn);

        root.Children.Add(new TextBlock { Text = "Bot log:", Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 10, 0, 2) });
        _log.Height = 300; root.Children.Add(_log);

        Content = new ScrollViewer { Content = root };

        _bot.OnLog += AppendLog;
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) => { _bal.Text = $"{_bot.Balance:N0} sat"; _bot.Announce(); };
        t.Start();
        _bal.Text = $"{_bot.Balance:N0} sat";
        Closed += (_, _) => { try { _bot.Dispose(); } catch { } };
    }

    private void AppendLog(string m)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => AppendLog(m))); return; }
        _log.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {m}");
    }

    private void ImportFunding()
    {
        var box = new TextBox { AcceptsReturn = true, Height = 90, Width = 400, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas") };
        var funder = new TextBox { Width = 400 };
        var ok = new Button { Content = "Verify & fund", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = "SPV envelope (wire form: rawTxHex|header80Hex|branchHex,…|index):", Foreground = Brushes.Gainsboro });
        sp.Children.Add(box);
        sp.Children.Add(new TextBlock { Text = "Funder address (refunded on close):", Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 6, 0, 0) });
        sp.Children.Add(funder);
        sp.Children.Add(ok);
        var win = new Window { Title = "Fund the bot", Width = 460, Height = 280, Owner = this, Content = new ScrollViewer { Content = sp } };
        ok.Click += (_, _) =>
        {
            try { if (_bot.ImportFunding(SpvEnvelope.FromWire(box.Text.Trim()), funder.Text.Trim())) win.Close(); }
            catch (Exception ex) { MessageBox.Show("Bad envelope: " + ex.Message, "Fund the bot"); }
        };
        win.ShowDialog();
    }
}
