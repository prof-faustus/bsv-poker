namespace BsvPoker.Core.Games;

/// <summary>
/// Orchestrates a complete on-chain hand end to end: the wallet funds a 2-of-2 escrow, the dealerless deck
/// is dealt to the seats, the showdown decides the winner per the game's rules, and a cooperative
/// settlement pays the winner — with the co-signed nLockTime recovery ready the whole time so funds are
/// never stranded. Every step is a real BSV transaction. This is the session the live UI drives.
/// </summary>
public static class OnChainGameSession
{
    public sealed record HandResult(OnChainWallet.Spend Funding, Chain.Tx Settlement, Chain.Tx Recovery, int WinnerSeat, long Pot);

    /// <summary>Play one heads-up Texas Hold'em hand on-chain from a shuffled <paramref name="deck"/>.</summary>
    public static HandResult PlayHoldem(OnChainWallet funder, (byte[] Priv, byte[] Pub) a, (byte[] Priv, byte[] Pub) b,
        IReadOnlyList<Card> deck, long pot, long fee = 500, uint recoverHeight = 900_000)
    {
        if (deck.Count < 9) throw new ArgumentException("need >= 9 cards for heads-up Hold'em");
        var fund = OnChainHand.FundEscrow(funder, a.Pub, b.Pub, pot, fee);
        var escrowTxid = Chain.Txid(fund.Tx);

        // Hold'em deal layout: seat0 holes = deck[0,1], seat1 = deck[2,3], board = deck[4..8]
        var holes = new IReadOnlyList<Card>[] { new[] { deck[0], deck[1] }, new[] { deck[2], deck[3] } };
        var board = deck.Skip(4).Take(5).ToList();
        var def = PokerGames.Of(PokerGame.TexasHoldem);
        var payouts = Showdown.Settle(def, holes, board, pot);
        int winner = payouts.GetValueOrDefault(0) >= payouts.GetValueOrDefault(1) ? 0 : 1;
        var winnerPub = winner == 0 ? a.Pub : b.Pub;

        var settle = OnChainHand.Settle(escrowTxid, 0, pot, winnerPub, fee, a.Priv, a.Pub, b.Priv, b.Pub);
        var recover = OnChainHand.Recover(escrowTxid, 0, a.Pub, pot / 2, b.Pub, pot - pot / 2, fee, recoverHeight, a.Priv, b.Priv);
        return new HandResult(fund, settle, recover, winner, pot);
    }
}
