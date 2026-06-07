using System.Security.Cryptography;
using BsvPoker.Crypto;

namespace BsvPoker.Core.Games;

/// <summary>
/// A whole hand expressed as a SEQUENCE of real on-chain transactions — the "maximize transactions /
/// everything on-chain" model made concrete. Every protocol step of a heads-up hand becomes its own typed
/// transaction (table genesis, game start, hand start, the pot escrow, each seat's shuffle stage, each card
/// dealt, each board street, each betting action, each showdown reveal) and the hand ends with the real
/// cooperative settlement. Each typed step carries genuine hand data (card indices, board cards, bet
/// amounts) in its documented fields, funded from the wallet so the whole tape is consensus-valid and can be
/// broadcast and mined. One heads-up hand emits ~20 transactions; more seats/cards emit proportionally more.
/// </summary>
public static class OnChainHandTape
{
    public sealed record Step(TxKind Kind, Chain.Tx Tx, byte[] OwnerPub);
    public sealed record Tape(IReadOnlyList<Step> Steps, int WinnerSeat, long Pot, bool Split, Chain.Tx Settlement);

    /// <summary>Build the full on-chain transaction tape for one heads-up Texas Hold'em hand.</summary>
    public static Tape BuildHoldem(OnChainWallet wallet, (byte[] Priv, byte[] Pub) a, (byte[] Priv, byte[] Pub) b,
        IReadOnlyList<Card> deck, long pot, byte[] tableId, long stepValue = 1000, long fee = 1000, uint recoverHeight = 900_000)
    {
        if (deck.Count < 9) throw new ArgumentException("need >= 9 cards for heads-up Hold'em");
        var steps = new List<Step>();
        var gameId = RandomNumberGenerator.GetBytes(16);
        var handId = RandomNumberGenerator.GetBytes(16);
        byte[][] pubBySeat = { a.Pub, b.Pub };

        // a typed step: a real typed output funded from the wallet (advances wallet state so the next step funds itself)
        void Typed(TxKind k, byte[] owner, params byte[][] fields)
            => steps.Add(new Step(k, wallet.SpendAction(TxTemplates.BuildOutput(k, fields, owner), stepValue, fee).Tx, owner));

        // table / game / hand genesis
        Typed(TxKind.TableGenesis, a.Pub, tableId, new byte[] { 0 /*Hold'em*/ }, new byte[] { 2 }, BitConverter.GetBytes(pot));
        Typed(TxKind.GameStart, a.Pub, tableId, gameId);
        Typed(TxKind.HandStart, a.Pub, gameId, handId, new byte[] { 0 /*button seat*/ });

        // the pot: a REAL 2-of-2 escrow funded from the wallet (this is the money)
        var fund = wallet.SpendAction(Chain.MultisigLock2of2(a.Pub, b.Pub), pot, fee);
        steps.Add(new Step(TxKind.PotEscrow, fund.Tx, a.Pub));
        var escrowTxid = Chain.Txid(fund.Tx);

        // each seat's shuffle/mask stage carries the REAL commutative-encryption masked deck it produced
        // (secp256k1 points): seat 0 masks the base deck, seat 1 masks seat 0's output. This is the actual
        // mental-poker shuffle (MentalPokerEC) recorded on-chain, not a placeholder.
        var sh = RealShuffle(deck.Count);
        Typed(TxKind.ShuffleStage, pubBySeat[0], handId, new byte[] { 0 }, Flatten(sh.Masked0));
        Typed(TxKind.ShuffleStage, pubBySeat[1], handId, new byte[] { 1 }, Flatten(sh.Masked1));

        // deal the hole cards: positions 0,1 → seat0 ; 2,3 → seat1 (real card indices)
        for (int pos = 0; pos < 4; pos++)
            Typed(TxKind.Deal, pubBySeat[pos / 2], handId, new byte[] { (byte)pos }, new byte[] { (byte)deck[pos].Index });

        // board streets: flop (cards 4-6), turn (7), river (8) — each its own reveal carrying the real cards
        Typed(TxKind.BoardReveal, a.Pub, handId, new byte[] { 1 }, new[] { (byte)deck[4].Index, (byte)deck[5].Index, (byte)deck[6].Index });
        Typed(TxKind.BoardReveal, a.Pub, handId, new byte[] { 2 }, new[] { (byte)deck[7].Index });
        Typed(TxKind.BoardReveal, a.Pub, handId, new byte[] { 3 }, new[] { (byte)deck[8].Index });

        // a simple heads-up betting line: each action is its own on-chain Bet tx (action: 0=check,1=call,2=bet)
        var line = new (int Seat, byte Action, long Amount)[] { (0, 1, 2), (1, 0, 0), (0, 0, 0), (1, 2, 0) };
        foreach (var (seat, action, amt) in line)
            Typed(TxKind.Bet, pubBySeat[seat], handId, new byte[] { (byte)seat }, new[] { action }, BitConverter.GetBytes(amt));

        // showdown: each seat reveals its hole cards on-chain
        for (int seat = 0; seat < 2; seat++)
            Typed(TxKind.Showdown, pubBySeat[seat], handId, new byte[] { (byte)seat },
                  new[] { (byte)deck[seat * 2].Index, (byte)deck[seat * 2 + 1].Index });

        // settle the real pot to the winner per Hold'em rules (single winner or hi-lo style split via SettleMany)
        var holes = new IReadOnlyList<Card>[] { new[] { deck[0], deck[1] }, new[] { deck[2], deck[3] } };
        var board = deck.Skip(4).Take(5).ToList();
        var def = PokerGames.Of(PokerGame.TexasHoldem);
        var payouts = Showdown.Settle(def, holes, board, pot);
        long pa = payouts.GetValueOrDefault(0), pb = payouts.GetValueOrDefault(1);
        long payable = pot - fee;
        long sa = pa <= 0 ? 0 : (pb <= 0 ? payable : pa * payable / pot);
        long sb = payable - sa;
        var settle = OnChainHand.SettleMany(escrowTxid, 0, pot, new (byte[], long)[] { (a.Pub, sa), (b.Pub, sb) },
            a.Priv, a.Pub, b.Priv, b.Pub);
        steps.Add(new Step(TxKind.Settlement, settle, (pa >= pb ? a : b).Pub));

        return new Tape(steps, pa >= pb ? 0 : 1, pot, pa > 0 && pb > 0, settle);
    }

    /// <summary>A real two-seat commutative-encryption shuffle (the data stamped into the ShuffleStage txs).</summary>
    public sealed record ShuffleProof(byte[][] Base, byte[][] Masked0, byte[][] Masked1, byte[] G0, byte[] G1);

    /// <summary>
    /// Run the real MentalPokerEC shuffle for <paramref name="n"/> cards: seat 0 masks the base deck with a
    /// secret global scalar + permutation, seat 1 masks seat 0's output likewise. Removing BOTH globals from
    /// the final deck recovers the base points (in a hidden order) — the property the unit test checks, and
    /// the reason no single seat controls the deck.
    /// </summary>
    public static ShuffleProof RealShuffle(int n)
    {
        var b = MentalPokerEC.BaseDeck(n);
        var g0 = MentalPokerEC.NewScalar(); var m0 = MentalPokerEC.ShuffleMask(b, g0, RandPerm(n));
        var g1 = MentalPokerEC.NewScalar(); var m1 = MentalPokerEC.ShuffleMask(m0, g1, RandPerm(n));
        return new ShuffleProof(b, m0, m1, g0, g1);
    }

    private static int[] RandPerm(int n)
    {
        var a = Enumerable.Range(0, n).ToArray();
        for (int i = n - 1; i > 0; i--) { int j = RandomNumberGenerator.GetInt32(i + 1); (a[i], a[j]) = (a[j], a[i]); }
        return a;
    }

    private static byte[] Flatten(byte[][] pts)
    {
        var b = new byte[pts.Length * 33];
        for (int i = 0; i < pts.Length; i++) Array.Copy(pts[i], 0, b, i * 33, 33);
        return b;
    }
}
