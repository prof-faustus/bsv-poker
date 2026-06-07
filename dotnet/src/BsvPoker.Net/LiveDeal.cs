using BsvPoker.Core;

namespace BsvPoker.Net;

/// <summary>
/// The LIVE two-player dealerless deal, run as a message exchange where every message is delivered as a
/// Bitcoin transaction (the caller's <see cref="IDealChannel"/> wraps the TxLink/on-chain-chat transport).
/// Two parties mask the deck in turn (commutative encryption), exchange only the masked decks during play
/// (no secrets), then SELECTIVELY reveal per-card scalars so each player can read ONLY their own hole cards
/// and the shared board — opponent holes stay private. At showdown both fully reveal and each verifies the
/// other's shuffle/remask proofs (<see cref="ShuffleProof"/>), so any cheat is caught. Heads-up: the
/// initiator is seat 0 (hole positions 0,1); the responder is seat 1 (positions 2,3); board = positions 4..8.
/// </summary>
public static class LiveDeal
{
    public interface IDealChannel { void Send(string msg); string Receive(); }

    public sealed record Result(IReadOnlyList<Card> MyHoles, IReadOnlyList<Card> OppHoles, IReadOnlyList<Card> Board, bool ProofsVerified);

    private const int N = 52;

    public static Result RunInitiator(IDealChannel ch) => Run(ch, seat0: true);
    public static Result RunResponder(IDealChannel ch) => Run(ch, seat0: false);

    private static Result Run(IDealChannel ch, bool seat0)
    {
        var c = MentalPokerEC.NewScalar();
        var perm = RandPerm();
        var d = MentalPokerEC.NewPerCardScalars(N);
        var commitShuf = ShuffleProof.CommitShuffle(c, perm);
        var commitRem = ShuffleProof.CommitRemask(c, d);
        int[] myHoles = seat0 ? new[] { 0, 1 } : new[] { 2, 3 };
        int[] oppHoles = seat0 ? new[] { 2, 3 } : new[] { 0, 1 };
        int[] boardPos = { 4, 5, 6, 7, 8 };

        // 1) exchange commitments
        ch.Send("C:" + Hex(commitShuf) + ":" + Hex(commitRem));
        var (csOpp, crOpp) = ParseCommit(Expect(ch.Receive(), "C:"));

        // 2) masking passes — seat0 starts each pass on its own deck; the masked decks (no secrets) are exchanged
        byte[][] dBase = MentalPokerEC.BaseDeck(N);
        byte[][] d1, d2, d3, d4;
        if (seat0)
        {
            d1 = MentalPokerEC.ShuffleMask(dBase, c, perm); ch.Send("D1:" + FlatDeck(d1));
            d2 = ParseDeck(Expect(ch.Receive(), "D2:"));
            d3 = MentalPokerEC.Remask(d2, c, d); ch.Send("D3:" + FlatDeck(d3));
            d4 = ParseDeck(Expect(ch.Receive(), "D4:"));
        }
        else
        {
            d1 = ParseDeck(Expect(ch.Receive(), "D1:"));
            d2 = MentalPokerEC.ShuffleMask(d1, c, perm); ch.Send("D2:" + FlatDeck(d2));
            d3 = ParseDeck(Expect(ch.Receive(), "D3:"));
            d4 = MentalPokerEC.Remask(d3, c, d); ch.Send("D4:" + FlatDeck(d4));
        }

        // 3) selective reveal: I reveal MY per-card scalars at the OPPONENT's hole positions + the board, so the
        //    opponent can read their holes and we both can read the board; I keep my own hole scalars secret.
        ch.Send("R:" + RevealScalars(d, oppHoles.Concat(boardPos)));
        var oppReveal = ParseScalars(Expect(ch.Receive(), "R:"), myHoles.Concat(boardPos));

        // read MY holes (I have my own d; the opponent revealed theirs at my positions) and the board
        var holes = myHoles.Select(k => DecodeCard(d4[k], d[k], oppReveal[k])).ToList();
        var board = boardPos.Select(k => DecodeCard(d4[k], d[k], oppReveal[k])).ToList();
        IReadOnlyList<Card> oppHoleCards = Array.Empty<Card>();

        // 4) showdown: fully reveal and verify the opponent's shuffle/remask proofs (cheating is caught)
        ch.Send("S:" + Hex(c) + "|" + PermCsv(perm) + "|" + AllScalars(d));
        var (cOpp, permOpp, dOpp) = ParseShowdown(Expect(ch.Receive(), "S:"));
        // with the opponent's full per-card scalars revealed, decode their holes too (to decide the winner)
        oppHoleCards = oppHoles.Select(k => DecodeCard(d4[k], d[k], dOpp[k])).ToList();
        bool proofs;
        if (seat0)
            proofs = ShuffleProof.VerifyShuffle(dBase, d1, c, perm, commitShuf)
                  && ShuffleProof.VerifyShuffle(d1, d2, cOpp, permOpp, csOpp)
                  && ShuffleProof.VerifyRemask(d2, d3, c, d, commitRem)
                  && ShuffleProof.VerifyRemask(d3, d4, cOpp, dOpp, crOpp);
        else
            proofs = ShuffleProof.VerifyShuffle(dBase, d1, cOpp, permOpp, csOpp)
                  && ShuffleProof.VerifyShuffle(d1, d2, c, perm, commitShuf)
                  && ShuffleProof.VerifyRemask(d2, d3, cOpp, dOpp, crOpp)
                  && ShuffleProof.VerifyRemask(d3, d4, c, d, commitRem);

        return new Result(holes, oppHoleCards, board, proofs);
    }

    private static Card DecodeCard(byte[] point, byte[] myScalar, byte[] oppScalar)
    {
        var m = MentalPokerEC.Unmask(point, new[] { myScalar, oppScalar });
        int idx = MentalPokerEC.Identify(m, N);
        if (idx < 0) throw new InvalidOperationException("card did not decode (bad reveal)");
        return Card.FromIndex(idx);
    }

    // ---- wire helpers (the channel encrypts + delivers these as Bitcoin transactions) ----
    private static int[] RandPerm()
    {
        var p = Enumerable.Range(0, N).ToArray();
        for (int i = N - 1; i > 0; i--) { int j = System.Security.Cryptography.RandomNumberGenerator.GetInt32(i + 1); (p[i], p[j]) = (p[j], p[i]); }
        return p;
    }
    private static string Hex(byte[] b) => Convert.ToHexString(b);
    private static string FlatDeck(byte[][] d) => string.Join(",", d.Select(Hex));
    private static byte[][] ParseDeck(string s) => s.Split(',').Select(Convert.FromHexString).ToArray();
    private static string PermCsv(int[] p) => string.Join(",", p);
    private static int[] ParsePerm(string s) => s.Split(',').Select(int.Parse).ToArray();
    private static string AllScalars(byte[][] d) => string.Join(",", d.Select(Hex));
    private static string RevealScalars(byte[][] d, IEnumerable<int> positions) => string.Join(",", positions.Select(k => k + ":" + Hex(d[k])));
    private static Dictionary<int, byte[]> ParseScalars(string s, IEnumerable<int> _) =>
        s.Split(',').Select(p => p.Split(':')).ToDictionary(p => int.Parse(p[0]), p => Convert.FromHexString(p[1]));
    private static (byte[], byte[]) ParseCommit(string s) { var p = s.Split(':'); return (Convert.FromHexString(p[0]), Convert.FromHexString(p[1])); }
    private static (byte[] C, int[] Perm, byte[][] D) ParseShowdown(string s)
    {
        var p = s.Split('|');
        return (Convert.FromHexString(p[0]), ParsePerm(p[1]), p[2].Split(',').Select(Convert.FromHexString).ToArray());
    }
    private static string Expect(string msg, string tag)
    {
        if (!msg.StartsWith(tag, StringComparison.Ordinal)) throw new InvalidOperationException($"protocol: expected {tag}, got {msg[..Math.Min(8, msg.Length)]}");
        return msg[tag.Length..];
    }
}
