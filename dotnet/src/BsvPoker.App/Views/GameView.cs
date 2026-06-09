using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BsvPoker.App.Controls;
using BsvPoker.Core;
using BsvPoker.Net;

namespace BsvPoker.App.Views;

/// <summary>
/// The poker table. Two modes:
///  - PRACTICE (hot-seat): both hands on one screen, real engine, for solo testing.
///  - NETWORKED: real 2-player over the P2P mesh (dealerless mental-poker deal; you see only YOUR hole
///    cards until showdown; your controls are live only on your turn). Driven by <see cref="NetGame"/>.
/// </summary>
public sealed class GameView : UserControl
{
    private readonly P2PNode _node;
    private readonly byte[] _priv;
    private readonly byte[] _pub;
    private readonly CardVault _vault;
    private readonly Action _onCardsChanged;
    private readonly Func<IReadOnlyList<Card>, long, string>? _onChainSettle; // settle a completed hand on-chain via the wallet/node
    private const long OnChainPot = 20_000;                                    // demo stake in satoshis for an on-chain hand

    private long[] _stacks = { 100, 100 };
    private int _button;
    private HoldemState? _practice;
    private NetGame? _net;
    private bool _botMode;
    private Variant _botVariant = Variant.TexasHoldem;
    private int _lastMintedHand = -1; // mint my hole-card NFTs once per dealt hand (by hand number)

    private readonly StackPanel _topCards = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel _board = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 8) };
    private readonly StackPanel _botCards = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _topInfo = new() { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _botInfo = new() { Foreground = Brushes.White, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _pot = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x82)), FontSize = 18, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _msg = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xE9)), FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _standings = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xCF, 0xD8, 0xDC)), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _bet = new() { Width = 70, Text = "6", VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
    private readonly Button _deal = Mk("Practice deal", "#444444");
    private readonly Button _fold = Mk("Fold", "#7A2E2E");
    private readonly Button _check = Mk("Check", "#333333");
    private readonly Button _call = Mk("Call", "#333333");
    private readonly Button _betBtn = Mk("Bet / Raise", "#2E5A7A");
    private readonly Button _leave = Mk("Leave table", "#555555");
    private readonly Button _onchain = Mk("On-chain settle", "#3A6E2E");

    /// <summary>Raised when the player leaves the table (so the host can stop any bot, etc.).</summary>
    public event Action? OnLeaveTable;

    public GameView(P2PNode node, byte[] priv, byte[] pub, CardVault vault, Action onCardsChanged,
        Func<IReadOnlyList<Card>, long, string>? onChainSettle = null)
    {
        _node = node; _priv = priv; _pub = pub; _vault = vault; _onCardsChanged = onCardsChanged; _onChainSettle = onChainSettle;
        Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0D)); Foreground = Brushes.White;

        var felt = new Border { Margin = new Thickness(16), CornerRadius = new CornerRadius(160) };
        felt.Background = new RadialGradientBrush(Color.FromRgb(0x1F, 0x7A, 0x43), Color.FromRgb(0x0B, 0x4A, 0x28));
        var inner = new Border { CornerRadius = new CornerRadius(150), BorderBrush = new SolidColorBrush(Color.FromRgb(0xA9, 0x81, 0x2B)), BorderThickness = new Thickness(6), Margin = new Thickness(10) };
        var g = new Grid { Margin = new Thickness(24) };
        g.RowDefinitions.Add(new RowDefinition());
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition());
        var top = new StackPanel { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center };
        top.Children.Add(_topInfo); top.Children.Add(_topCards); Grid.SetRow(top, 0); g.Children.Add(top);
        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        mid.Children.Add(_pot); mid.Children.Add(_board); mid.Children.Add(_msg); mid.Children.Add(_standings); Grid.SetRow(mid, 1); g.Children.Add(mid);
        var bot = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center };
        bot.Children.Add(_botCards); bot.Children.Add(_botInfo); Grid.SetRow(bot, 2); g.Children.Add(bot);
        inner.Child = g; felt.Child = inner;

        var bar = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(10) };
        _deal.Click += (_, _) => PracticeDeal();
        _fold.Click += (_, _) => Do(ActionKind.Fold, 0);
        _check.Click += (_, _) => Do(ActionKind.Check, 0);
        _call.Click += (_, _) => Do(ActionKind.Call, 0);
        _betBtn.Click += (_, _) => { if (long.TryParse(_bet.Text.Trim(), out var to)) Do(ActionKind.Raise, to); };
        _leave.Click += (_, _) => LeaveTable();
        _onchain.Click += (_, _) => SettleOnChain();
        bar.Children.Add(_deal); bar.Children.Add(_fold); bar.Children.Add(_check); bar.Children.Add(_call); bar.Children.Add(_bet); bar.Children.Add(_betBtn);
        if (_onChainSettle != null) bar.Children.Add(_onchain);   // only when the wallet/node are available
        bar.Children.Add(_leave);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(felt, 0); root.Children.Add(felt);
        var barHost = new Border { Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x16)), Padding = new Thickness(8) };
        barHost.Child = bar; Grid.SetRow(barHost, 1); root.Children.Add(barHost);
        Content = root;
        Render();
    }

    public void StartNetworked(string tableId, string tableName)
    {
        _net?.Stop();
        _practice = null;
        _lastMintedHand = -1;
        _net = new NetGame(_node, tableId, _priv, _pub);
        _net.OnUpdate += () => Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Render));
        _net.Start();
        Render();
    }

    private static Button Mk(string text, string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        return new Button { Content = text, Width = 110, Margin = new Thickness(4), Padding = new Thickness(0, 8, 0, 8), Foreground = Brushes.White, BorderThickness = new Thickness(0), Background = new SolidColorBrush(c) };
    }

    /// <summary>Lobby "Play a bot" — you (seat 0) vs a practice bot (seat 1) at the chosen variant.</summary>
    public void StartBot(Variant variant)
    {
        _net?.Stop(); _net = null; _botMode = true; _botVariant = variant;
        for (int i = 0; i < 2; i++) if (_stacks[i] < 2) _stacks[i] = 100;
        var deck = MentalPoker.ShuffledFrom(new[] { MentalPoker.FreshEntropy(), MentalPoker.FreshEntropy() }, Variants.CardSet(variant));
        _practice = HoldemState.Create(_stacks, _button, 1, 2, deck, variant);
        _button ^= 1;
        Render();
        DriveBot();
    }

    private void PracticeDeal()
    {
        _net?.Stop(); _net = null; _botMode = true; // the practice button now plays a bot at the current variant
        for (int i = 0; i < 2; i++) if (_stacks[i] < 2) _stacks[i] = 100;
        var deck = MentalPoker.ShuffledFrom(new[] { MentalPoker.FreshEntropy(), MentalPoker.FreshEntropy() }, Variants.CardSet(_botVariant));
        _practice = HoldemState.Create(_stacks, _button, 1, 2, deck, _botVariant);
        _button ^= 1;
        Render();
        DriveBot();
    }

    private void DriveBot()
    {
        // bot is seat 1; act on its turns until it's the human's turn (seat 0) or the hand ends.
        while (_botMode && _practice is { Complete: false } st && st.ToAct == 1)
        {
            try { st.Apply(BotPolicy.Decide(st)); } catch { break; }
        }
        if (_practice is { Complete: true }) _stacks = _practice.Seats.Select(s => s.Stack).ToArray();
        Render();
    }

    private void Do(ActionKind kind, long amt)
    {
        if (_net != null) { _net.Act(kind, amt); return; }
        if (_practice == null || _practice.Complete) return;
        if (_botMode && _practice.ToAct != 0) return; // in bot mode you only act for seat 0
        try { _practice.Apply(new GameAction(kind, _practice.ToAct, amt)); } catch (Exception ex) { _msg.Text = ex.Message; return; }
        if (_practice.Complete) { _stacks = _practice.Seats.Select(s => s.Stack).ToArray(); Render(); return; }
        if (_botMode) { DriveBot(); return; }
        Render();
    }

    /// <summary>
    /// Leave the table at ANY time and keep your funds — without exception. Stops the networked session
    /// (with real BSV this is where the pre-signed nLockTime recovery returns your stake), drops any bot,
    /// and returns to a clean table. The button is never disabled.
    /// </summary>
    private void LeaveTable()
    {
        _net?.Stop(); _net = null; _practice = null; _botMode = false; _lastMintedHand = -1;
        OnLeaveTable?.Invoke();
        Render();
        _msg.Text = "You left the table. Your funds are safe and yours — you can always walk away.";
    }

    /// <summary>
    /// Settle the just-completed hand ON-CHAIN: reconstruct the 9-card heads-up deck from the finished hand
    /// and hand it to the wallet, which emits + broadcasts the full transaction tape (pot escrow → ... →
    /// settlement) on the real BSV network. Only available once the hand reached a 5-card-board showdown.
    /// </summary>
    private void SettleOnChain()
    {
        if (_onChainSettle == null) { _msg.Text = "On-chain settle is unavailable (no wallet/node)."; return; }
        var deck = CompletedHandDeck();
        if (deck == null) { _msg.Text = "Play a hand to showdown (a full 5-card board) first, then press On-chain settle."; return; }
        _msg.Text = _onChainSettle(deck, OnChainPot);
    }

    /// <summary>The 9-card heads-up deck (seat0 holes, seat1 holes, 5 board cards) of a completed practice hand, or null.</summary>
    private IReadOnlyList<Card>? CompletedHandDeck()
    {
        var st = _practice;
        if (st is not { Complete: true } || st.Seats.Count < 2 || st.Board.Count < 5) return null;
        var s0 = st.Seats[0].Hole.ToList(); var s1 = st.Seats[1].Hole.ToList();
        if (s0.Count < 2 || s1.Count < 2 || s0.Concat(s1).Any(c => c.IsFaceDown)) return null;
        return new List<Card> { s0[0], s0[1], s1[0], s1[1], st.Board[0], st.Board[1], st.Board[2], st.Board[3], st.Board[4] };
    }

    private void Render()
    {
        _topCards.Children.Clear(); _botCards.Children.Clear(); _board.Children.Clear(); _standings.Text = "";
        if (_net != null) RenderNet(); else RenderPractice();
    }

    private void RenderNet()
    {
        var ng = _net!;
        var hand = ng.Hand;
        _deal.IsEnabled = true;
        if (hand == null)
        {
            for (int i = 0; i < 2; i++) { _topCards.Children.Add(new CardView()); _botCards.Children.Add(new CardView()); }
            for (int i = 0; i < 5; i++) _board.Children.Add(new CardView());
            _pot.Text = "Pot: 0"; _msg.Text = ng.Status; _botInfo.Text = "You"; _topInfo.Text = "Opponent";
            _fold.IsEnabled = _check.IsEnabled = _call.IsEnabled = _betBtn.IsEnabled = false;
            return;
        }
        int me = ng.MySeat < 0 ? 0 : ng.MySeat;
        // Cards are NFTs: mint MY hole cards into my wallet vault (sealed to me) once per dealt hand.
        if (_lastMintedHand != ng.HandNumber && ng.MySeat >= 0 && hand.Seats[me].Hole.All(c => !c.IsFaceDown))
        {
            _lastMintedHand = ng.HandNumber;
            foreach (var c in hand.Seats[me].Hole) _vault.AddCard(c.Index, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            _onCardsChanged();
        }
        foreach (var c in hand.Seats[me].Hole) { var cv = new CardView(); cv.ShowCard(c); _botCards.Children.Add(cv); }
        // one group per opponent seat (holes are face-down sentinels of the variant's count until showdown)
        _topInfo.Text = hand.Seats.Count > 2 ? "Opponents" : "";
        foreach (var s in hand.Seats.Where(s => s.Seat != me))
        {
            bool theirTurn = !hand.Complete && hand.ToAct == s.Seat;
            var grp = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(12, 0, 12, 0), HorizontalAlignment = HorizontalAlignment.Center };
            grp.Children.Add(new TextBlock { Text = $"Seat {s.Seat} — {s.Stack}{(s.Folded ? " (folded)" : "")}{(theirTurn ? "  ◀" : "")}", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            var cards = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var c in s.Hole) { var cv = new CardView(); if (c.IsFaceDown) cv.ShowBack(); else cv.ShowCard(c); cards.Children.Add(cv); }
            grp.Children.Add(cards);
            _topCards.Children.Add(grp);
        }
        for (int i = 0; i < 5; i++) { var cv = new CardView(); if (i < hand.Board.Count) cv.ShowCard(hand.Board[i]); else cv.ShowEmpty(); _board.Children.Add(cv); }
        _pot.Text = $"Pot: {hand.Pot}";
        bool myTurn = !hand.Complete && hand.ToAct == me;
        _botInfo.Text = $"You (seat {me}) — stack {hand.Seats[me].Stack}{(myTurn ? "  ◀ your turn" : "")}";
        _msg.Text = ng.Status + (ng.HandLog.Count > 0 ? "   •   " + ng.HandLog[^1] : "");
        _standings.Text = "Session — " + ng.Standings;
        var la = myTurn ? hand.Legal() : null;
        _fold.IsEnabled = la?.CanFold ?? false;
        _check.IsEnabled = la?.CanCheck ?? false;
        _call.IsEnabled = la?.CanCall ?? false;
        _betBtn.IsEnabled = la?.CanBetOrRaise ?? false;
        if (la is { CanCall: true }) _call.Content = $"Call {la.CallAmount}"; else _call.Content = "Call";
        if (la is { CanBetOrRaise: true }) _bet.Text = la.MinRaiseTo.ToString();
    }

    private void RenderPractice()
    {
        var st = _practice;
        if (st == null)
        {
            _msg.Text = "Join a table in the Lobby to play someone — or press Practice deal (hot-seat).";
            for (int i = 0; i < 2; i++) { _topCards.Children.Add(new CardView()); _botCards.Children.Add(new CardView()); }
            for (int i = 0; i < 5; i++) _board.Children.Add(new CardView());
            _pot.Text = "Pot: 0"; _botInfo.Text = $"You — {_stacks[0]}"; _topInfo.Text = $"Player 2 — {_stacks[1]}";
            _deal.IsEnabled = true; _fold.IsEnabled = _check.IsEnabled = _call.IsEnabled = _betBtn.IsEnabled = false;
            return;
        }
        foreach (var c in st.Seats[0].Hole) { var cv = new CardView(); cv.ShowCard(c); _botCards.Children.Add(cv); }
        foreach (var c in st.Seats[1].Hole) { var cv = new CardView(); if (_botMode && !st.Complete) cv.ShowBack(); else cv.ShowCard(c); _topCards.Children.Add(cv); }
        for (int i = 0; i < 5; i++) { var cv = new CardView(); if (i < st.Board.Count) cv.ShowCard(st.Board[i]); else cv.ShowEmpty(); _board.Children.Add(cv); }
        _pot.Text = $"Pot: {st.Pot}";
        bool myTurn = !st.Complete && st.ToAct == 0;
        _botInfo.Text = $"You (seat 0) — {st.Seats[0].Stack}{(myTurn ? "  ◀ your turn" : "")}";
        _topInfo.Text = (_botMode ? "Bot" : "Seat 1") + $" — {st.Seats[1].Stack}";
        _msg.Text = st.Message;
        var la = st.Complete ? null : st.Legal();
        _deal.IsEnabled = st.Complete;
        _fold.IsEnabled = la?.CanFold ?? false;
        _check.IsEnabled = la?.CanCheck ?? false;
        _call.IsEnabled = la?.CanCall ?? false;
        _betBtn.IsEnabled = la?.CanBetOrRaise ?? false;
        if (la is { CanCall: true }) _call.Content = $"Call {la.CallAmount}"; else _call.Content = "Call";
        if (la is { CanBetOrRaise: true }) _bet.Text = la.MinRaiseTo.ToString();
    }
}
