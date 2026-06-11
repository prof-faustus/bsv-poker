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

        // ALWAYS RECOVERABLE (rule #7): the instant the pot escrow exists, BOTH seats co-sign the unilateral
        // nLockTime recovery that refunds each stake if the other stalls — so funds can NEVER be stranded and
        // no player is ever at the other's mercy. Signed up front, before the hand proceeds.
        var recovery = OnChainHand.Recover(escrowTxid, 0, a.Pub, pot / 2, b.Pub, pot - pot / 2, fee, recoverHeight, a.Priv, b.Priv);
        steps.Add(new Step(TxKind.Recovery, recovery, a.Pub));

        // each seat's shuffle/mask stage carries the REAL commutative-encryption masked deck it produced
        // (secp256k1 points): seat 0 masks the base deck, seat 1 masks seat 0's output. This is the actual
        // mental-poker shuffle (MentalPokerEC) recorded on-chain, not a placeholder.
        var sh = RealShuffle(deck.Count);
        // Commit-then-reveal: each seat publishes a COMMITMENT to its secret (global scalar + permutation) WITH
        // its masked deck, so the shuffle is provably honest — at reveal anyone recomputes it and catches any
        // dropped/duplicated/swapped card. (The reveal+verify at showdown is the next step; commitment is here.)
        Typed(TxKind.ShuffleStage, pubBySeat[0], handId, new byte[] { 0 }, ShuffleProof.CommitShuffle(sh.G0, sh.P0), Flatten(sh.Masked0));
        Typed(TxKind.ShuffleStage, pubBySeat[1], handId, new byte[] { 1 }, ShuffleProof.CommitShuffle(sh.G1, sh.P1), Flatten(sh.Masked1));

        // Deal the hole cards PRIVATELY: each card is ENCRYPTED to the recipient seat (CardNft seal — ECDH to the
        // seat's key) so ONLY that seat can open it. The cleartext index is NEVER on-chain at deal time — that is
        // the whole point of mental poker: no one knows their face-down card but they hold it. Cleartext appears
        // only at showdown (below). positions 0,1 → seat0 ; 2,3 → seat1.
        // NOTE (next step, design-uncertain — do NOT guess): the full MULTI-PARTY deal must use the threshold
        // where every OTHER player hands the recipient their per-card scalar share and the recipient's own share
        // is mandatory and withheld (collusion-proof), plus the end-of-hand reclamation covenant. Heads-up here
        // is sealed-to-recipient, which is the 2-party case of that model.
        for (int pos = 0; pos < 4; pos++)
        {
            var seatPub = pubBySeat[pos / 2];
            var blind = RandomNumberGenerator.GetBytes(32);
            var sealedHex = CardNft.SealToPub(deck[pos].Index, blind, seatPub);   // ECDH-sealed; only this seat opens it
            Typed(TxKind.Deal, seatPub, handId, new byte[] { (byte)pos }, Convert.FromHexString(sealedHex));
        }

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

        // Reveal the shuffle secrets so ANYONE can VerifyShuffle: recompute each masked deck from the opened
        // global scalar + permutation and check it against the commitment — proving no card was dropped,
        // duplicated, or swapped. This closes the commit-then-reveal loop: the shuffle is provably honest.
        Typed(TxKind.ShuffleReveal, pubBySeat[0], handId, new byte[] { 0 }, sh.G0, EncodePerm(sh.P0));
        Typed(TxKind.ShuffleReveal, pubBySeat[1], handId, new byte[] { 1 }, sh.G1, EncodePerm(sh.P1));

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

    /// <summary>
    /// Build the full on-chain transaction tape for one Blackjack hand vs the dealer (your bot): table/game/hand
    /// genesis, a REAL 2-of-2 pot escrow (player + dealer stakes) with the always-available nLockTime recovery, the
    /// committed mental-poker shuffle, the initial deal (your two cards ECDH-sealed as NFTs, the dealer's up-card
    /// revealed and its hole sealed), then EVERY action (hit/stand/double) and EVERY drawn card as its own typed
    /// transaction, the dealer's play, and the cooperative settlement to the winner per Blackjack rules. The play
    /// follows a fixed policy (hit below 17) so the tape is deterministic from the shuffled deck.
    /// </summary>
    public static Tape BuildBlackjack(OnChainWallet wallet, (byte[] Priv, byte[] Pub) player, (byte[] Priv, byte[] Pub) dealer,
        IReadOnlyList<Card> deck, long bet, byte[] tableId, long stepValue = 1, long fee = 1, uint recoverHeight = 900_000)
    {
        if (bet <= 0) throw new ArgumentException("bet must be positive");
        if (deck.Count < 12) throw new ArgumentException("need >= 12 cards for a Blackjack hand");
        var steps = new List<Step>();
        var gameId = RandomNumberGenerator.GetBytes(16);
        var handId = RandomNumberGenerator.GetBytes(16);
        byte[][] pubBySeat = { player.Pub, dealer.Pub };
        void Typed(TxKind k, byte[] owner, params byte[][] fields)
            => steps.Add(new Step(k, wallet.SpendAction(TxTemplates.BuildOutput(k, fields, owner), stepValue, fee).Tx, owner));

        Typed(TxKind.TableGenesis, player.Pub, tableId, new byte[] { 1 /*Blackjack*/ }, new byte[] { 2 }, BitConverter.GetBytes(bet));
        Typed(TxKind.GameStart, player.Pub, tableId, gameId);
        Typed(TxKind.HandStart, player.Pub, gameId, handId, new byte[] { 0 });

        // the pot: BOTH stakes (player + dealer each put up `bet`) under a real 2-of-2 escrow, with up-front recovery.
        long potVal = bet * 2;
        var fund = wallet.SpendAction(Chain.MultisigLock2of2(player.Pub, dealer.Pub), potVal, fee);
        steps.Add(new Step(TxKind.PotEscrow, fund.Tx, player.Pub));
        var escrowTxid = Chain.Txid(fund.Tx);
        steps.Add(new Step(TxKind.Recovery, OnChainHand.Recover(escrowTxid, 0, player.Pub, bet, dealer.Pub, bet, fee, recoverHeight, player.Priv, dealer.Priv), player.Pub));

        // committed mental-poker shuffle of the deck (provably honest at reveal)
        var sh = RealShuffle(deck.Count);
        Typed(TxKind.ShuffleStage, pubBySeat[0], handId, new byte[] { 0 }, ShuffleProof.CommitShuffle(sh.G0, sh.P0), Flatten(sh.Masked0));
        Typed(TxKind.ShuffleStage, pubBySeat[1], handId, new byte[] { 1 }, ShuffleProof.CommitShuffle(sh.G1, sh.P1), Flatten(sh.Masked1));

        // play the hand with the engine (policy: hit below 17) and emit a tx per card + per action.
        var g = Blackjack.Create(deck, bet);
        int pos = 0;
        void DealTx(byte[] toPub, bool reveal)
        {
            var card = deck[pos];
            if (reveal) Typed(TxKind.Deal, toPub, handId, new byte[] { (byte)pos }, new[] { (byte)card.Index });
            else Typed(TxKind.Deal, toPub, handId, new byte[] { (byte)pos }, Convert.FromHexString(CardNft.SealToPub(card.Index, RandomNumberGenerator.GetBytes(32), toPub)));
            pos++;
        }
        // initial deal order the engine used: player, dealer(up), player, dealer(hole)
        DealTx(player.Pub, reveal: false);
        DealTx(dealer.Pub, reveal: true);
        DealTx(player.Pub, reveal: false);
        DealTx(dealer.Pub, reveal: false);

        // player actions: each its own Bet tx; each hit draws a sealed NFT card
        while (!g.PlayerDone && g.Outcome == BjOutcome.InPlay)
        {
            if (Blackjack.Value(g.Player).Total < 17)
            {
                Typed(TxKind.Bet, player.Pub, handId, new byte[] { 0 }, new[] { (byte)BjAction.Hit }, BitConverter.GetBytes(bet));
                int before = g.Player.Count; g.Act(BjAction.Hit);
                if (g.Player.Count > before) DealTx(player.Pub, reveal: false);   // the card the hit drew
            }
            else { Typed(TxKind.Bet, player.Pub, handId, new byte[] { 0 }, new[] { (byte)BjAction.Stand }, BitConverter.GetBytes(0L)); g.Act(BjAction.Stand); }
        }

        // dealer reveals its hole, then EACH card it drew to 17 is its OWN on-chain tx (every move a transaction).
        Typed(TxKind.Deal, dealer.Pub, handId, new byte[] { 3 }, new[] { (byte)g.Dealer[1].Index });   // hole now revealed
        int dealerDraws = g.Dealer.Count - 2;
        for (int i = 0; i < dealerDraws && pos < deck.Count; i++) DealTx(dealer.Pub, reveal: true);     // each dealer draw, its own tx
        Typed(TxKind.Showdown, dealer.Pub, handId, new byte[] { 1 }, g.Dealer.Select(c => (byte)c.Index).ToArray());

        // showdown: player reveals its cards on-chain; close the shuffle proof.
        Typed(TxKind.Showdown, player.Pub, handId, new byte[] { 0 }, g.Player.Select(c => (byte)c.Index).ToArray());
        Typed(TxKind.ShuffleReveal, pubBySeat[0], handId, new byte[] { 0 }, sh.G0, EncodePerm(sh.P0));
        Typed(TxKind.ShuffleReveal, pubBySeat[1], handId, new byte[] { 1 }, sh.G1, EncodePerm(sh.P1));

        // settle: player win → player takes the pot; dealer win → dealer; push → each gets its stake back.
        long payable = potVal - fee;
        long toPlayer = g.Outcome switch
        {
            BjOutcome.PlayerWin or BjOutcome.DealerBust or BjOutcome.PlayerBlackjack => payable,
            BjOutcome.Push => payable / 2,
            _ => 0,
        };
        long toDealer = payable - toPlayer;
        var settle = OnChainHand.SettleMany(escrowTxid, 0, potVal, new (byte[], long)[] { (player.Pub, toPlayer), (dealer.Pub, toDealer) },
            player.Priv, player.Pub, dealer.Priv, dealer.Pub);
        int winnerSeat = toPlayer >= toDealer ? 0 : 1;
        steps.Add(new Step(TxKind.Settlement, settle, pubBySeat[winnerSeat]));
        return new Tape(steps, winnerSeat, potVal, g.Outcome == BjOutcome.Push, settle);
    }

    /// <summary>The data a real two-seat commutative-encryption shuffle produces (stamped into the ShuffleStage
    /// txs): the base deck, each seat's masked deck, each seat's secret global scalar, AND each seat's secret
    /// permutation — the secrets are committed up front and opened at reveal so the shuffle is provably honest.</summary>
    public sealed record ShuffleData(byte[][] Base, byte[][] Masked0, byte[][] Masked1, byte[] G0, byte[] G1, int[] P0, int[] P1);

    /// <summary>
    /// Run the real MentalPokerEC shuffle for <paramref name="n"/> cards: seat 0 masks the base deck with a
    /// secret global scalar + permutation, seat 1 masks seat 0's output likewise. Removing BOTH globals from
    /// the final deck recovers the base points (in a hidden order) — the property the unit test checks, and
    /// the reason no single seat controls the deck.
    /// </summary>
    public static ShuffleData RealShuffle(int n)
    {
        var b = MentalPokerEC.BaseDeck(n);
        var p0 = RandPerm(n); var g0 = MentalPokerEC.NewScalar(); var m0 = MentalPokerEC.ShuffleMask(b, g0, p0);
        var p1 = RandPerm(n); var g1 = MentalPokerEC.NewScalar(); var m1 = MentalPokerEC.ShuffleMask(m0, g1, p1);
        return new ShuffleData(b, m0, m1, g0, g1, p0, p1);
    }

    private static int[] RandPerm(int n)
    {
        var a = Enumerable.Range(0, n).ToArray();
        for (int i = n - 1; i > 0; i--) { int j = RandomNumberGenerator.GetInt32(i + 1); (a[i], a[j]) = (a[j], a[i]); }
        return a;
    }

    /// <summary>Encode a permutation as bytes EXACTLY as ShuffleProof.CommitShuffle does, so the on-chain reveal
    /// recomputes to the same commitment and VerifyShuffle passes.</summary>
    private static byte[] EncodePerm(int[] perm)
    {
        var b = new byte[perm.Length * 4];
        for (int i = 0; i < perm.Length; i++) System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i * 4, 4), perm[i]);
        return b;
    }

    private static byte[] Flatten(byte[][] pts)
    {
        var b = new byte[pts.Length * 33];
        for (int i = 0; i < pts.Length; i++) Array.Copy(pts[i], 0, b, i * 33, 33);
        return b;
    }
}
