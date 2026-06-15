using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BsvPoker.App.Controls;
using BsvPoker.Core.Games;
using BsvPoker.Net;

namespace BsvPoker.App.Views;

/// <summary>
/// A LIVE multiplayer (group) Blackjack table driven by a <see cref="NetBlackjack"/> over the mesh: shows the
/// communal dealer on top, YOUR hand below, the live status across all seats, and Hit / Stand / Double buttons
/// that act only on your turn. Never one-on-one — you join a table with other players; the dealer is computed
/// jointly. Updates whenever the networked game raises a change.
/// </summary>
public sealed class BlackjackTableWindow : Window
{
    private readonly NetBlackjack _bj;
    private readonly StackPanel _dealerCards = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel _playerCards = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _dealerInfo = new() { Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _playerInfo = new() { Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _status = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xCF, 0xD8, 0xDC)), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _hit = Mk("Hit"), _stand = Mk("Stand"), _double = Mk("Double");
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private static Button Mk(string t) => new() { Content = t, Width = 110, Margin = new Thickness(6), Padding = new Thickness(0, 8, 0, 8), Foreground = Brushes.White, BorderThickness = new Thickness(0), Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x5A, 0x7A)) };

    public BlackjackTableWindow(Window owner, NetBlackjack bj)
    {
        _bj = bj; Owner = owner; Title = "Group Blackjack (multiplayer)";
        Width = 600; Height = 560; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x4A, 0x28));

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = "Group Blackjack", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) });
        root.Children.Add(new TextBlock { Text = "Dealer (shared)", Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x82)), HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeights.Bold });
        root.Children.Add(_dealerCards);
        root.Children.Add(_dealerInfo);
        root.Children.Add(new TextBlock { Text = "Your hand", Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x82)), HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 16, 0, 0) });
        root.Children.Add(_playerCards);
        root.Children.Add(_playerInfo);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        _hit.Click += (_, _) => { _bj.Act(BjAction.Hit); Refresh(); };
        _stand.Click += (_, _) => { _bj.Act(BjAction.Stand); Refresh(); };
        _double.Click += (_, _) => { _bj.Act(BjAction.Double); Refresh(); };
        btns.Children.Add(_hit); btns.Children.Add(_stand); btns.Children.Add(_double);
        root.Children.Add(btns);
        root.Children.Add(_status);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _bj.OnUpdate += OnUpdate;
        _timer.Tick += (_, _) => Refresh();   // also poll, so the deal/dealer progress always shows
        _timer.Start();
        Closed += (_, _) => { _timer.Stop(); try { _bj.OnUpdate -= OnUpdate; } catch { } try { _bj.Stop(); } catch { } };
        Refresh();
    }

    private void OnUpdate() { if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Refresh)); return; } Refresh(); }

    private void Refresh()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Refresh)); return; }
        _dealerCards.Children.Clear(); _playerCards.Children.Clear();
        var dealer = _bj.DealerCards;
        foreach (var c in dealer) { var cv = new CardView(); cv.ShowCard(c); _dealerCards.Children.Add(cv); }
        // while players are still acting, the dealer's hole card is sealed — show a face-down placeholder
        if (_bj.State == NetBlackjack.Phase.Playing && dealer.Count == 1) { var back = new CardView(); back.ShowBack(); _dealerCards.Children.Add(back); }
        _dealerInfo.Text = dealer.Count > 0 ? (_bj.State is NetBlackjack.Phase.DealerPlay or NetBlackjack.Phase.Done ? $"Dealer total: {Blackjack.Value(dealer).Total}" : "Dealer shows…") : "";

        var me = _bj.MyHand;
        foreach (var c in me) { var cv = new CardView(); cv.ShowCard(c); _playerCards.Children.Add(cv); }
        _playerInfo.Text = me.Count > 0 ? $"Your total: {Blackjack.Value(me).Total}" + (_bj.MySeat >= 0 ? $"   (seat {_bj.MySeat})" : "") : "";

        bool myTurn = _bj.State == NetBlackjack.Phase.Playing && _bj.ToAct == _bj.MySeat;
        _hit.IsEnabled = _stand.IsEnabled = myTurn;
        _double.IsEnabled = myTurn && me.Count == 2;
        _status.Text = _bj.Status;
    }
}
