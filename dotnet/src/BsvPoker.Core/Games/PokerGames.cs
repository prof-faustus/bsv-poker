namespace BsvPoker.Core.Games;

/// <summary>The six distinct poker games. Each has different cards, rules, and showdown.</summary>
public enum PokerGame { TexasHoldem, Omaha, OmahaHiLo, SevenCardStud, Razz, FiveCardDraw }

/// <param name="Hole">cards dealt to each player.</param>
/// <param name="Board">shared community cards (0 for stud/draw).</param>
/// <param name="ExactlyHole">if set, exactly this many hole cards must be used with the board (Omaha = 2).</param>
public sealed record GameDef(PokerGame Game, string Name, int Hole, int Board, int? ExactlyHole, bool HiLo, bool LowOnly, bool Stud, bool Draw);

public static class PokerGames
{
    public static readonly IReadOnlyList<GameDef> All = new[]
    {
        new GameDef(PokerGame.TexasHoldem,   "Texas Hold'em",   2, 5, null, false, false, false, false),
        new GameDef(PokerGame.Omaha,         "Omaha",           4, 5, 2,    false, false, false, false),
        new GameDef(PokerGame.OmahaHiLo,     "Omaha Hi-Lo (8)", 4, 5, 2,    true,  false, false, false),
        new GameDef(PokerGame.SevenCardStud, "Seven-Card Stud", 7, 0, null, false, false, true,  false),
        new GameDef(PokerGame.Razz,          "Razz",            7, 0, null, false, true,  true,  false),
        new GameDef(PokerGame.FiveCardDraw,  "Five-Card Draw",  5, 0, null, false, false, false, true),
    };
    public static GameDef Of(PokerGame g) => All.First(d => d.Game == g);
}

/// <summary>
/// Per-game showdown evaluation and pot settlement. Uses <see cref="PokerEval"/> for high hands and
/// <see cref="LowEval"/> for low (Razz, and the low half of hi-lo), enforcing each game's card rules
/// (Omaha's exactly-two-hole, stud/draw using only the player's own cards). Hi-lo splits the pot.
/// </summary>
public static class Showdown
{
    /// <summary>Best HIGH score for a seat (higher better), per the game's hole/board rules.</summary>
    public static long BestHigh(GameDef d, IReadOnlyList<Card> hole, IReadOnlyList<Card> board)
    {
        if (d.ExactlyHole is int e) return BestConstrained(hole, board, e, high: true)!.Value;
        var cards = d.Board > 0 ? hole.Concat(board).ToList() : hole.ToList();
        return PokerEval.Best(cards).Score;
    }

    /// <summary>Best LOW score for a seat (lower better), or null if no qualifying low; per the game's rules.</summary>
    public static long? BestLow(GameDef d, IReadOnlyList<Card> hole, IReadOnlyList<Card> board)
    {
        if (d.LowOnly) return LowEval.Best(hole)!.Value.Score;                  // Razz: any low, lowest wins
        if (!d.HiLo) return null;
        if (d.ExactlyHole is int e) return BestConstrainedLow(hole, board, e);  // Omaha hi-lo: exactly 2 + 3, 8-or-better
        var r = LowEval.Best(hole.Concat(board).ToList(), eightOrBetter: true);
        return r?.Score;
    }

    // best 5-card score using EXACTLY `useHole` hole cards + (5-useHole) board cards
    private static long? BestConstrained(IReadOnlyList<Card> hole, IReadOnlyList<Card> board, int useHole, bool high)
    {
        long best = high ? long.MinValue : long.MaxValue; bool any = false;
        foreach (var hc in Choose(hole.Count, useHole))
            foreach (var bc in Choose(board.Count, 5 - useHole))
            {
                var five = hc.Select(i => hole[i]).Concat(bc.Select(i => board[i])).ToList();
                long s = PokerEval.Best(five).Score;
                if (s > best) { best = s; any = true; }
            }
        return any ? best : null;
    }

    private static long? BestConstrainedLow(IReadOnlyList<Card> hole, IReadOnlyList<Card> board, int useHole)
    {
        long best = long.MaxValue; bool any = false;
        foreach (var hc in Choose(hole.Count, useHole))
            foreach (var bc in Choose(board.Count, 5 - useHole))
            {
                var five = hc.Select(i => hole[i]).Concat(bc.Select(i => board[i])).ToList();
                var r = LowEval.Best(five, eightOrBetter: true);
                if (r != null && r.Value.Score < best) { best = r.Value.Score; any = true; }
            }
        return any ? best : null;
    }

    /// <summary>Settle a pot among seats' hole cards. Hi-lo splits high/low; otherwise (or low-only) one pool.</summary>
    public static Dictionary<int, long> Settle(GameDef d, IReadOnlyList<IReadOnlyList<Card>> holes, IReadOnlyList<Card> board, long pot)
    {
        var payouts = new Dictionary<int, long>();
        int n = holes.Count;

        if (d.LowOnly) { AwardLow(d, holes, board, pot, payouts, n); return payouts; }

        long highPool = d.HiLo ? pot / 2 : pot;
        long lowPool = d.HiLo ? pot - highPool : 0;

        // HIGH
        var highScores = Enumerable.Range(0, n).Select(i => BestHigh(d, holes[i], board)).ToArray();
        long bestHigh = highScores.Max();
        var highWinners = Enumerable.Range(0, n).Where(i => highScores[i] == bestHigh).ToList();
        Split(payouts, highWinners, highPool);

        // LOW (hi-lo). If no qualifying low, the low pool rolls into the high winners.
        if (d.HiLo)
        {
            var lows = Enumerable.Range(0, n).Select(i => BestLow(d, holes[i], board)).ToArray();
            var qualifiers = Enumerable.Range(0, n).Where(i => lows[i] != null).ToList();
            if (qualifiers.Count > 0)
            {
                long bestLow = qualifiers.Min(i => lows[i]!.Value);
                Split(payouts, qualifiers.Where(i => lows[i]!.Value == bestLow).ToList(), lowPool);
            }
            else Split(payouts, highWinners, lowPool);
        }
        return payouts;
    }

    private static void AwardLow(GameDef d, IReadOnlyList<IReadOnlyList<Card>> holes, IReadOnlyList<Card> board, long pot, Dictionary<int, long> payouts, int n)
    {
        var lows = Enumerable.Range(0, n).Select(i => BestLow(d, holes[i], board)!.Value).ToArray();
        long best = lows.Min();
        Split(payouts, Enumerable.Range(0, n).Where(i => lows[i] == best).ToList(), pot);
    }

    private static void Split(Dictionary<int, long> payouts, List<int> winners, long pool)
    {
        if (winners.Count == 0 || pool <= 0) return;
        long each = pool / winners.Count, rem = pool - each * winners.Count;
        foreach (var w in winners) payouts[w] = payouts.GetValueOrDefault(w) + each;
        if (rem > 0) payouts[winners.Min()] += rem; // odd chip to the lowest seat index
    }

    private static IEnumerable<int[]> Choose(int n, int k)
    {
        if (k < 0 || k > n) yield break;
        var idx = Enumerable.Range(0, k).ToArray();
        while (true)
        {
            yield return (int[])idx.Clone();
            int i = k - 1; while (i >= 0 && idx[i] == n - k + i) i--;
            if (i < 0) yield break;
            idx[i]++; for (int j = i + 1; j < k; j++) idx[j] = idx[j - 1] + 1;
        }
    }
}
