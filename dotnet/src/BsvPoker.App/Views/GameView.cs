using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BsvPoker.App.Controls;
using BsvPoker.Core;

namespace BsvPoker.App.Views;

/// <summary>
/// The poker table. Play is ONLY a real on-chain hand. With no funds there is NOTHING here — no cards, no
/// felt, no table — because there is no game until you fund a real pot. Pressing "Play on-chain hand" funds
/// a real 2-of-2 pot escrow from your wallet and emits the whole hand as Bitcoin transactions; the dealt
/// board (recorded on-chain by the hand's transactions) is then shown. No play money, no off-chain game,
/// no free cards.
/// </summary>
public sealed class GameView : UserControl
{
    private readonly Func<IReadOnlyList<Card>, long, string>? _onChainSettle;
    private readonly Func<long, bool>? _canFund;
    private const long OnChainStake = 20_000; // real-sat stake for one on-chain hand

    private readonly StackPanel _content = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24) };
    private readonly Button _play = Mk("Play on-chain hand", "#3A6E2E");
    private readonly Button _leave = Mk("Leave table", "#555555");

    public event Action? OnLeaveTable;

    public GameView(byte[] priv, byte[] pub, CardVault vault, Action onCardsChanged,
        Func<IReadOnlyList<Card>, long, string>? onChainSettle = null, Func<long, bool>? canFund = null)
    {
        _onChainSettle = onChainSettle; _canFund = canFund;
        Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0D)); Foreground = Brushes.White;

        var bar = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(10) };
        _play.Click += (_, _) => PlayOnChain();
        _leave.Click += (_, _) => { OnLeaveTable?.Invoke(); ShowMessage("You left the table. Your funds are yours."); };
        bar.Children.Add(_play); bar.Children.Add(_leave);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var host = new ScrollViewer { Content = _content };
        Grid.SetRow(host, 0); root.Children.Add(host);
        var barHost = new Border { Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x16)), Padding = new Thickness(8) };
        barHost.Child = bar; Grid.SetRow(barHost, 1); root.Children.Add(barHost);
        Content = root;

        ShowIdle();
    }

    /// <summary>Lobby entry point: a hand is ONLY ever a real on-chain hand.</summary>
    public void StartBot(Variant variant) => PlayOnChain();

    private void PlayOnChain()
    {
        if (_onChainSettle == null) { ShowMessage("On-chain play is unavailable (wallet/node not wired)."); return; }
        if (_canFund != null && !_canFund(OnChainStake))
        {
            ShowMessage($"You have no funds. There is no game until you fund a real pot.\n\n" +
                        $"Fund your wallet with at least {OnChainStake:N0} sat of real BSV (Wallet tab) and connect to the network. " +
                        "Poker is ONLY played as on-chain Bitcoin transactions — there is no play-money mode and no cards until you fund.");
            return;
        }
        var deck = ShuffledDeck(9);
        var result = _onChainSettle(deck, OnChainStake);     // funds escrow + emits the whole on-chain hand tape
        ShowHand(result, deck.Skip(4).Take(5).ToList());     // the board is recorded on-chain by the hand's txs
    }

    private void ShowIdle()
    {
        bool funded = _canFund == null || _canFund(OnChainStake);
        _content.Children.Clear();
        _content.Children.Add(new TextBlock
        {
            Text = funded
                ? "Press “Play on-chain hand” to fund a real pot escrow and play a hand entirely as Bitcoin transactions."
                : "No funds — no game. There are no cards and no table until you fund a real pot.\nFund your wallet with real BSV (Wallet tab), then play. Play that is not on-chain does not exist here.",
            Foreground = funded ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)),
            FontSize = 16, TextWrapping = TextWrapping.Wrap, MaxWidth = 640, TextAlignment = TextAlignment.Center
        });
    }

    private void ShowMessage(string msg)
    {
        _content.Children.Clear();
        _content.Children.Add(new TextBlock { Text = msg, Foreground = Brushes.White, FontSize = 16, TextWrapping = TextWrapping.Wrap, MaxWidth = 640, TextAlignment = TextAlignment.Center });
    }

    private void ShowHand(string status, IReadOnlyList<Card> board)
    {
        _content.Children.Clear();
        _content.Children.Add(new TextBlock { Text = "On-chain hand — every card and bet is a Bitcoin transaction", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var c in board) { var cv = new CardView(); cv.ShowCard(c); row.Children.Add(cv); }
        _content.Children.Add(row);
        _content.Children.Add(new TextBlock { Text = status, Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xE9)), FontSize = 14, TextWrapping = TextWrapping.Wrap, MaxWidth = 700, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 12, 0, 0) });
    }

    private static IReadOnlyList<Card> ShuffledDeck(int n)
    {
        var a = Enumerable.Range(0, 52).ToArray();
        for (int i = 51; i > 0; i--) { int j = RandomNumberGenerator.GetInt32(i + 1); (a[i], a[j]) = (a[j], a[i]); }
        return a.Take(n).Select(Card.FromIndex).ToList();
    }

    private static Button Mk(string text, string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        return new Button { Content = text, Width = 150, Margin = new Thickness(4), Padding = new Thickness(0, 8, 0, 8), Foreground = Brushes.White, BorderThickness = new Thickness(0), Background = new SolidColorBrush(c) };
    }
}
