namespace BsvPoker.Core.Games;

/// <summary>
/// MULTIPLAYER (group) Blackjack — NEVER one-on-one and never vs yourself. N players (2..6, like a poker
/// table) share ONE communal dealer and ONE dealerless deck (the same mental-poker deal as poker: each
/// player's hole cards are private; the dealer's cards and the draw pile are revealed to everyone as they are
/// used). Each player plays their own hand against the dealer; after all players act, the dealer plays to 17.
/// The bank/pot is funded by ALL players together (an N-of-N locked output), so no one can cheat, and the
/// dealer always has funds to pay winners. Settlement distributes the pot by each player's result; the
/// remaining bank is split among the remaining players when the table ends.
///
/// This class is the pure GAME engine (deal → play → dealer → per-seat result → pot distribution). It is
/// transport-agnostic, so the networked driver feeds it the shared deck and relays each player's action.
/// </summary>
public sealed class GroupBlackjack
{
    public sealed class Seat
    {
        public int Index;
        public long Bet;
        public List<Card> Hand = new();
        public bool Done;
        public bool Doubled;
        public BjOutcome Outcome = BjOutcome.InPlay;
        public long Net;        // chips won (+) or lost (-) vs the dealer, set at settlement
    }

    private readonly List<Card> _deck;
    private int _pos;
    public List<Seat> Seats { get; } = new();
    public List<Card> Dealer { get; } = new();
    public bool Complete { get; private set; }
    public int ToAct { get; private set; } = -1;     // index of the seat whose turn it is, or -1
    public int Players => Seats.Count;

    private GroupBlackjack(IReadOnlyList<Card> deck) { _deck = deck.ToList(); }

    /// <summary>Deal a group hand: two cards to every player, two to the communal dealer, from the shared deck.</summary>
    public static GroupBlackjack Create(IReadOnlyList<long> bets, IReadOnlyList<Card> deck)
    {
        if (bets.Count < 2) throw new ArgumentException("group Blackjack needs >= 2 players (never one-on-one)");
        foreach (var b in bets) if (b <= 0) throw new ArgumentException("each player's bet must be positive");
        var g = new GroupBlackjack(deck);
        for (int i = 0; i < bets.Count; i++) g.Seats.Add(new Seat { Index = i, Bet = bets[i] });
        foreach (var s in g.Seats) s.Hand.Add(g.Draw());     // first card to each player
        g.Dealer.Add(g.Draw());                              // dealer up card
        foreach (var s in g.Seats) s.Hand.Add(g.Draw());     // second card to each player
        g.Dealer.Add(g.Draw());                              // dealer hole card
        foreach (var s in g.Seats) if (Blackjack.IsBlackjack(s.Hand)) s.Done = true;   // a natural stands pat
        g.ToAct = g.NextNotDoneFrom(0);
        if (g.ToAct < 0) g.DealerPlayAndSettle();            // everyone had a natural
        return g;
    }

    private Card Draw() { if (_pos >= _deck.Count) throw new InvalidOperationException("deck exhausted"); return _deck[_pos++]; }
    private int NextNotDoneFrom(int start) { for (int i = start; i < Seats.Count; i++) if (!Seats[i].Done) return i; return -1; }

    /// <summary>The current player hits / stands / doubles. Turns pass in seat order; when all are done the
    /// dealer plays and the hand settles.</summary>
    public void Act(int seat, BjAction a)
    {
        if (Complete) throw new InvalidOperationException("hand complete");
        if (seat != ToAct) throw new InvalidOperationException($"not seat {seat}'s turn");
        var s = Seats[seat];
        switch (a)
        {
            case BjAction.Hit:
                s.Hand.Add(Draw());
                if (Blackjack.Value(s.Hand).Total > 21) { s.Outcome = BjOutcome.PlayerBust; s.Done = true; }
                break;
            case BjAction.Double:
                s.Bet *= 2; s.Doubled = true; s.Hand.Add(Draw()); s.Done = true;
                if (Blackjack.Value(s.Hand).Total > 21) s.Outcome = BjOutcome.PlayerBust;
                break;
            case BjAction.Stand:
                s.Done = true;
                break;
        }
        if (s.Done) { ToAct = NextNotDoneFrom(seat + 1); if (ToAct < 0) DealerPlayAndSettle(); }
    }

    private void DealerPlayAndSettle()
    {
        while (Blackjack.Value(Dealer).Total < 17) Dealer.Add(Draw());   // dealer stands on all 17
        int d = Blackjack.Value(Dealer).Total; bool dbj = Blackjack.IsBlackjack(Dealer);
        foreach (var s in Seats)
        {
            if (s.Outcome == BjOutcome.PlayerBust) { s.Net = -s.Bet; continue; }   // busted earlier — loses
            int p = Blackjack.Value(s.Hand).Total; bool pbj = Blackjack.IsBlackjack(s.Hand);
            if (pbj && dbj) { s.Outcome = BjOutcome.Push; s.Net = 0; }
            else if (pbj) { s.Outcome = BjOutcome.PlayerBlackjack; s.Net = s.Bet * 3 / 2; }
            else if (dbj) { s.Outcome = BjOutcome.DealerWin; s.Net = -s.Bet; }
            else if (d > 21) { s.Outcome = BjOutcome.DealerBust; s.Net = s.Bet; }
            else if (p > d) { s.Outcome = BjOutcome.PlayerWin; s.Net = s.Bet; }
            else if (d > p) { s.Outcome = BjOutcome.DealerWin; s.Net = -s.Bet; }
            else { s.Outcome = BjOutcome.Push; s.Net = 0; }
        }
        ToAct = -1; Complete = true;
    }

    /// <summary>Distribute the pot. The total pot = the dealer bank (funded jointly by the N-of-N) + every
    /// player's bet. Each player walks away with bet + net (≥ 0); the bank keeps the remainder. Conserves the
    /// total exactly, so the on-chain N-of-N settlement pays out these amounts and nothing is created or lost.</summary>
    public (IReadOnlyDictionary<int, long> Payouts, long RemainingBank) Distribute(long dealerBank)
    {
        if (!Complete) throw new InvalidOperationException("hand not complete");
        long totalBets = Seats.Sum(s => s.Bet);
        long pot = dealerBank + totalBets;
        var payouts = new Dictionary<int, long>();
        long paid = 0;
        foreach (var s in Seats) { long pay = Math.Max(0, s.Bet + s.Net); payouts[s.Index] = pay; paid += pay; }
        long remaining = pot - paid;   // the bank's share after paying the players (can be ±; conserves the pot)
        return (payouts, remaining);
    }
}
