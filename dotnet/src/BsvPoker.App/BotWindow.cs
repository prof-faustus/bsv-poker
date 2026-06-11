using System.Threading.Tasks;
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
    private readonly Func<long, Task<bool>>? _onFund;   // one-click: send the amount from MY wallet to the bot
    private readonly ListBox _log = new() { Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x0B, 0x0E)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
    private readonly TextBlock _bal = new() { Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.Bold };

    public BotWindow(BotPlayer bot, Func<long, Task<bool>>? onFund = null)
    {
        _bot = bot;
        _onFund = onFund;
        Title = bot.Name + " (your bot)";
        Width = 460; Height = 620;
        // ALWAYS visible: center on the main window (Owner) — never absolute screen coords, which put the window
        // OFF-SCREEN on multi-monitor setups whose monitors sit at negative coordinates. Brought to front on load.
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x0E, 0x12)); // distinct dark-crimson, never the main felt
        Loaded += (_, _) => { try { Activate(); Topmost = true; Topmost = false; Focus(); } catch { } };

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock { Text = $"🤖 {_bot.Name} — your bot (only you can play it)", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
        root.Children.Add(new TextBlock { Text = "Identity:", Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 8, 0, 0) });
        root.Children.Add(new TextBox { Text = _bot.PubHex, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), Foreground = Brushes.Pink, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(new TextBlock { Text = $"Address (fund the bot here): {_bot.ReceiveAddress()}", Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        root.Children.Add(new TextBlock { Text = $"On the overlay at: {_bot.Endpoint}", Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 4, 0, 0) });
        root.Children.Add(new TextBlock { Text = "Balance (confirmed):", Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 8, 0, 0) });
        root.Children.Add(_bal);

        // ONE-CLICK funding (the principal's rule): type the amount, press ONE button. The amount is sent from the
        // owner's wallet to the bot automatically — no SPV-envelope, no copy-paste. The bot refunds it on close.
        var amtRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        amtRow.Children.Add(new TextBlock { Text = "Fund (sat): ", Foreground = Brushes.Gainsboro, VerticalAlignment = VerticalAlignment.Center });
        var amount = new TextBox { Text = "20000", Width = 120, VerticalAlignment = VerticalAlignment.Center };
        amtRow.Children.Add(amount);
        var fundBtn = new Button { Content = "Fund bot & start", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6),
            Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)), Foreground = Brushes.White, FontWeight = FontWeights.Bold };
        fundBtn.Click += async (_, _) =>
        {
            if (_onFund == null) { ImportFunding(); return; }   // fallback to manual if no wallet wired
            if (!long.TryParse(amount.Text.Trim(), out var sat) || sat <= 0) { MessageBox.Show("Enter a positive amount in satoshis.", "Fund bot"); return; }
            fundBtn.IsEnabled = false; fundBtn.Content = "Funding…";
            try { bool ok = await _onFund(sat); AppendLog(ok ? $"funded with {sat:N0} sat — bot is ready to play" : "funding failed"); }
            catch (Exception ex) { MessageBox.Show("Funding failed: " + ex.Message, "Fund bot"); }
            finally { fundBtn.IsEnabled = true; fundBtn.Content = "Fund bot & start"; }
        };
        amtRow.Children.Add(fundBtn);
        root.Children.Add(amtRow);

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
