using BsvPoker.Core;
using BsvPoker.Core.Games;

namespace BsvPoker.Tests;

/// <summary>
/// MULTIPLAYER group Blackjack: N players share ONE communal dealer + ONE deck; each plays vs the dealer; the
/// dealer plays to 17; the pot (dealer bank + all bets) is distributed by result and conserved exactly. Never
/// one-on-one. Deterministic decks drive exact outcomes. Deal order: a card to each player, dealer up, a second
/// to each player, dealer hole, then draws.
/// </summary>
public static class GroupBlackjackTests
{
    private static Card C(int rank) => new Card(rank, Suit.Clubs);   // suit is irrelevant to Blackjack value
    private static List<Card> Deck(params int[] ranks) => ranks.Select(C).ToList();

    public static void All()
    {
        Console.WriteLine("multiplayer group Blackjack (N players, one communal dealer, shared pot):");

        T.Run("never one-on-one: a table needs >= 2 players", () =>
        {
            bool threw = false; try { GroupBlackjack.Create(new long[] { 10 }, Deck(10, 10, 10, 10)); } catch { threw = true; }
            T.True(threw, "Create with a single player is rejected (no vs-self Blackjack)");
        });

        T.Run("two players vs one dealer: each settles independently; pot conserved", () =>
        {
            // p0=[T,T]=20, p1=[5,6]=11, dealer=[T,7]=17. Both stand. p0 beats 17, p1 loses to 17.
            var g = GroupBlackjack.Create(new long[] { 10, 10 }, Deck(10, 5, 10, 10, 6, 7));
            T.Eq(g.Players, 2, "two seats");
            T.Eq(g.Seats[0].Hand.Count, 2, "player 0 dealt two cards");
            T.Eq(g.Dealer.Count, 2, "dealer dealt two cards");
            T.Eq(g.ToAct, 0, "player 0 acts first");
            bool oot = false; try { g.Act(1, BjAction.Stand); } catch { oot = true; }
            T.True(oot, "a player cannot act out of turn");
            g.Act(0, BjAction.Stand);
            T.Eq(g.ToAct, 1, "turn passes to player 1");
            g.Act(1, BjAction.Stand);
            T.True(g.Complete, "after the last player, the dealer plays and the hand settles");
            T.Eq((int)g.Seats[0].Outcome, (int)BjOutcome.PlayerWin, "player 0 (20) beats the dealer (17)");
            T.Eq(g.Seats[0].Net, 10, "player 0 wins his bet");
            T.Eq((int)g.Seats[1].Outcome, (int)BjOutcome.DealerWin, "player 1 (11) loses to the dealer (17)");
            T.Eq(g.Seats[1].Net, -10, "player 1 loses his bet");

            var (payouts, remaining) = g.Distribute(dealerBank: 100);
            T.Eq(payouts[0], 20, "winner gets bet + winnings back (10+10)");
            T.Eq(payouts[1], 0, "loser gets nothing back");
            T.Eq(payouts[0] + payouts[1] + remaining, 100 + 20, "the pot (bank + all bets) is conserved exactly");
        });

        T.Run("dealer busts: every standing player wins", () =>
        {
            // p0=[T,T]=20, p1=[9,9]=18, dealer=[T,6]=16 then draws T -> 26 bust
            var g = GroupBlackjack.Create(new long[] { 10, 10 }, Deck(10, 9, 10, 10, 9, 6, 10));
            g.Act(0, BjAction.Stand);
            g.Act(1, BjAction.Stand);
            T.True(g.Complete && Blackjack.Value(g.Dealer).Total > 21, "the dealer drew to a bust");
            T.Eq((int)g.Seats[0].Outcome, (int)BjOutcome.DealerBust, "player 0 wins (dealer bust)");
            T.Eq((int)g.Seats[1].Outcome, (int)BjOutcome.DealerBust, "player 1 wins (dealer bust)");
            T.True(g.Seats[0].Net == 10 && g.Seats[1].Net == 10, "both win their bet");
        });

        T.Run("a player's blackjack pays 3:2 and a busting player loses", () =>
        {
            // p0=[A,T]=21 natural; p1=[6,5]=11 then hits a T -> 21? no, test a bust: p1=[T,6]=16 hit T ->26 bust
            // deck: p0c1=A, p1c1=T, dealerUp=9, p0c2=T, p1c2=6, dealerHole=8 (dealer 17), draw for p1 = T
            var g = GroupBlackjack.Create(new long[] { 10, 10 }, Deck(14, 10, 9, 10, 6, 8, 10));
            // p0 has a natural 21 -> auto-done; p1 acts first
            T.Eq(g.ToAct, 1, "the player with a natural is skipped; player 1 acts");
            g.Act(1, BjAction.Hit);   // 16 + T = 26 -> bust
            T.True(g.Complete, "player 1 busting (and being the last active seat) ends the hand");
            T.Eq((int)g.Seats[0].Outcome, (int)BjOutcome.PlayerBlackjack, "player 0 has blackjack");
            T.Eq(g.Seats[0].Net, 15, "blackjack pays 3:2 (10 -> 15)");
            T.Eq((int)g.Seats[1].Outcome, (int)BjOutcome.PlayerBust, "player 1 busted");
            T.Eq(g.Seats[1].Net, -10, "the busting player loses his bet");
            var (payouts, remaining) = g.Distribute(dealerBank: 50);
            T.Eq(payouts[0], 25, "blackjack player gets 10 + 15 back");
            T.Eq(payouts[1], 0, "busted player gets nothing");
            T.Eq(payouts[0] + payouts[1] + remaining, 50 + 20, "pot conserved (bank 50 + bets 20)");
        });
    }
}
