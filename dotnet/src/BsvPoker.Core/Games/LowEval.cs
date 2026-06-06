namespace BsvPoker.Core.Games;

/// <summary>
/// Ace-to-five LOW hand evaluation (for Razz and the low half of hi-lo split). Aces are LOW; straights and
/// flushes do NOT count against a low. The best low is the lowest five-card high-hand: a no-pair hand
/// always beats any pair, and within a category lower cards win — so the wheel 5-4-3-2-A is the best low.
/// Returns a comparable score where LOWER is a better low. "8-or-better" requires five distinct ranks all
/// ≤ 8. Pure and exact; no assumptions.
/// </summary>
public static class LowEval
{
    private static int Low(Card c) => c.Rank == 14 ? 1 : c.Rank; // ace counts as 1

    public readonly record struct Result(long Score, bool QualifiesEightOrBetter);

    /// <summary>Best low from 5..7 cards (LOWER score is better). Set <paramref name="eightOrBetter"/> to require a qualifying low.</summary>
    public static Result? Best(IReadOnlyList<Card> cards, bool eightOrBetter = false)
    {
        if (cards.Count < 5) throw new ArgumentException("need at least 5 cards");
        long best = long.MaxValue; bool bestQ = false; bool any = false;
        foreach (var combo in Combinations(cards.Count, 5))
        {
            var five = new Card[5];
            for (int k = 0; k < 5; k++) five[k] = cards[combo[k]];
            var (score, q) = Score5(five);
            if (eightOrBetter && !q) continue;
            if (score < best) { best = score; bestQ = q; any = true; }
        }
        return any ? new Result(best, bestQ) : null;
    }

    private static (long Score, bool Qualifies) Score5(Card[] five)
    {
        var count = new int[14]; // lowRank 1..13
        foreach (var c in five) count[Low(c)]++;
        // category: 0 high card, 1 pair, 2 two pair, 3 trips, 4 full house, 5 quads (straights/flushes ignored)
        var mult = Enumerable.Range(1, 13).Select(r => count[r]).Where(m => m > 0).OrderByDescending(m => m).ToArray();
        int category = mult[0] switch
        {
            4 => 5,
            3 => mult.Length > 1 && mult[1] == 2 ? 4 : 3,
            2 => mult.Length > 1 && mult[1] == 2 ? 2 : 1,
            _ => 0,
        };
        // tiebreak ranks ordered by (multiplicity desc, rank desc) so the highest card is most significant
        var tb = Enumerable.Range(1, 13).Where(r => count[r] > 0)
            .OrderByDescending(r => count[r]).ThenByDescending(r => r).ToArray();
        long score = category;
        foreach (var r in tb) score = score * 16 + r;
        for (int pad = tb.Length; pad < 5; pad++) score *= 16;
        bool qualifies = category == 0 && five.Max(Low) <= 8; // five distinct ranks, all ≤ 8
        return (score, qualifies);
    }

    private static IEnumerable<int[]> Combinations(int n, int k)
    {
        var idx = Enumerable.Range(0, k).ToArray();
        while (true)
        {
            yield return (int[])idx.Clone();
            int i = k - 1;
            while (i >= 0 && idx[i] == n - k + i) i--;
            if (i < 0) yield break;
            idx[i]++;
            for (int j = i + 1; j < k; j++) idx[j] = idx[j - 1] + 1;
        }
    }
}
