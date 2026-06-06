using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BsvPoker.Core;

namespace BsvPoker.App;

/// <summary>
/// The bot's OWN window — deliberately and completely distinct from the main window: much smaller, a
/// dark-crimson "robot" theme, docked to the bottom-right (never centered, never on top of the main, never
/// the same size). It shows the bot is an automated player and lets the human take the wheel.
/// </summary>
public sealed class BotWindow : Window
{
    private readonly BotPlayer _bot;
    private readonly TextBlock _status = new() { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _stack = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x6E)), FontSize = 20, FontWeight = FontWeights.Bold };
    private readonly TextBlock _cards = new() { Foreground = Brushes.White, FontFamily = new FontFamily("Consolas"), FontSize = 16, Margin = new Thickness(0, 4, 0, 0) };
    private readonly CheckBox _take = new() { Content = "Take control of this bot", Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 6) };
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public BotWindow(BotPlayer bot)
    {
        _bot = bot;
        Title = "🤖 AUTOMATED BOT — you may take control";
        Width = 460; Height = 620; ResizeMode = ResizeMode.CanMinimize; ShowInTaskbar = true; Topmost = false;
        // dock bottom-right, offset — NEVER over the main window, NEVER the same size
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.WorkArea.Right - Width - 12;
        Top = SystemParameters.WorkArea.Bottom - Height - 12;
        Background = new LinearGradientBrush(Color.FromRgb(0x3A, 0x0E, 0x12), Color.FromRgb(0x16, 0x06, 0x08), 90);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = "🤖  BOT PLAYER", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)) });
        root.Children.Add(new TextBlock { Text = "An automated opponent with its own wallet. It plays itself.", Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0xB0, 0xB0)), TextWrapping = TextWrapping.Wrap });
        root.Children.Add(new TextBlock { Text = "Bot stack", Foreground = Brushes.Gray, Margin = new Thickness(0, 12, 0, 0) });
        root.Children.Add(_stack);
        root.Children.Add(_cards);
        root.Children.Add(_status);
        root.Children.Add(_take);
        _take.Checked += (_, _) => { _bot.HumanControl = true; };
        _take.Unchecked += (_, _) => { _bot.HumanControl = false; };

        var bar = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        bar.Children.Add(Btn("Fold", () => Act(ActionKind.Fold, 0)));
        bar.Children.Add(Btn("Check", () => Act(ActionKind.Check, 0)));
        bar.Children.Add(Btn("Call", () => Act(ActionKind.Call, 0)));
        bar.Children.Add(Btn("Bet/Raise +", () => { var h = _bot.Net.Hand; if (h != null) Act(ActionKind.Raise, h.Legal().MinRaiseTo); }));
        root.Children.Add(bar);
        root.Children.Add(new TextBlock { Text = "(Manual buttons act for the bot only while “Take control” is ticked.)", Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });

        Content = new Border { Child = root };
        _refresh.Tick += (_, _) => Refresh();
        _refresh.Start();
        Refresh();
    }

    private void Act(ActionKind kind, long amount)
    {
        if (!_bot.HumanControl) { _take.IsChecked = true; }
        try { _bot.Net.Act(kind, amount); } catch { }
    }

    private Button Btn(string text, Action onClick)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 6, 10, 6), Foreground = Brushes.White, BorderThickness = new Thickness(0), Background = new SolidColorBrush(Color.FromRgb(0x7A, 0x2E, 0x2E)) };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void Refresh()
    {
        var h = _bot.Net.Hand;
        _stack.Text = _bot.Stack.ToString("N0");
        _status.Text = _bot.Status;
        if (h != null && _bot.Net.MySeat >= 0)
        {
            var hole = h.Seats[_bot.Net.MySeat].Hole;
            _cards.Text = "Bot cards: " + string.Join(" ", hole.Select(c => c.IsFaceDown ? "🂠" : c.ToString()));
        }
        else _cards.Text = "";
    }

    protected override void OnClosed(EventArgs e) { _refresh.Stop(); _bot.Dispose(); base.OnClosed(e); }
}
