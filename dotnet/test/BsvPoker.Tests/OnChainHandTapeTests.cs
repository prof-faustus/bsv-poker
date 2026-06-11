using BsvPoker.Core;
using BsvPoker.Core.Games;
using BsvPoker.Crypto;

namespace BsvPoker.Tests;

/// <summary>
/// A whole hand as a SEQUENCE of on-chain transactions ("maximize transactions"): the tape emits a typed
/// transaction for every protocol step plus the real pot escrow and settlement, all funded from one wallet,
/// every tx distinct and consensus-shaped, the typed steps parse back to their kind+owner, and the pot
/// settles to the real winner.
/// </summary>
public static class OnChainHandTapeTests
{
    private static Card C(string s)
    {
        int rank = s[0] switch { 'A' => 14, 'K' => 13, 'Q' => 12, 'J' => 11, 'T' => 10, _ => s[0] - '0' };
        var suit = s[1] switch { 's' => Suit.Spades, 'h' => Suit.Hearts, 'd' => Suit.Diamonds, _ => Suit.Clubs };
        return new Card(rank, suit);
    }

    public static void All()
    {
        Console.WriteLine("on-chain hand TAPE (every step its own transaction):");

        T.Run("a heads-up hand emits the full ordered tape of typed + money transactions", () =>
        {
            var a = Secp256k1.GenerateKeyPair(); var b = Secp256k1.GenerateKeyPair();
            var w = new OnChainWallet(WalletKeys.NewSeed());
            w.Add(new OnChainWallet.Utxo("aa".PadRight(64, '1'), 0, 100_000_000, 0, 0));
            var deck = new[] { C("As"), C("Ah"), C("2c"), C("3d"), C("Ad"), C("Kh"), C("Qs"), C("Jc"), C("9h") };
            long pot = 40000;
            var tape = OnChainHandTape.BuildHoldem(w, a, b, deck, pot, tableId: new byte[16], stepValue: 1000, fee: 500);

            var expected = new[]
            {
                TxKind.TableGenesis, TxKind.GameStart, TxKind.HandStart, TxKind.PotEscrow, TxKind.Recovery,
                TxKind.ShuffleStage, TxKind.ShuffleStage,
                TxKind.Deal, TxKind.Deal, TxKind.Deal, TxKind.Deal,
                TxKind.BoardReveal, TxKind.BoardReveal, TxKind.BoardReveal,
                TxKind.Bet, TxKind.Bet, TxKind.Bet, TxKind.Bet,
                TxKind.Showdown, TxKind.Showdown, TxKind.ShuffleReveal, TxKind.ShuffleReveal, TxKind.Settlement,
            };
            T.Eq(tape.Steps.Count, expected.Length, "the full ordered tape (with always-on recovery + shuffle reveal)");
            for (int i = 0; i < expected.Length; i++) T.Eq(tape.Steps[i].Kind.ToString(), expected[i].ToString(), $"step {i} kind");

            // every transaction is distinct (real, separate on-chain txs)
            var txids = tape.Steps.Select(s => Chain.Txid(s.Tx)).ToHashSet();
            T.Eq(txids.Count, tape.Steps.Count, "all txids distinct");

            // every TYPED step parses back to its kind + owner (PotEscrow + Settlement are money txs, not typed)
            foreach (var step in tape.Steps)
            {
                if (step.Kind is TxKind.PotEscrow or TxKind.Settlement or TxKind.Recovery) continue;
                var parsed = TxTemplates.Parse(step.Tx.Outs[0].Script);
                T.True(parsed != null, $"{step.Kind} output is a typed output");
                T.Eq(parsed!.Kind.ToString(), step.Kind.ToString(), $"{step.Kind} parses to its kind");
                T.Eq(T.Hex(parsed.OwnerPub), T.Hex(step.OwnerPub), $"{step.Kind} owner round-trips");
            }

            // the Deal steps carry the hole card ENCRYPTED to the recipient seat — ONLY that seat can open it.
            // The cleartext index is NEVER on-chain at deal time (true mental-poker privacy).
            var deals = tape.Steps.Where(s => s.Kind == TxKind.Deal).ToList();
            for (int pos = 0; pos < 4; pos++)
            {
                var f = TxTemplates.Parse(deals[pos].Tx.Outs[0].Script)!.Fields;
                T.Eq(f[1][0], (byte)pos, $"deal {pos} position");
                var sealedHex = Convert.ToHexString(f[2]);
                var recipientPriv = pos / 2 == 0 ? a.Priv : b.Priv;
                var otherPriv = pos / 2 == 0 ? b.Priv : a.Priv;
                T.Eq(CardNft.Open(sealedHex, recipientPriv).CardIndex, deck[pos].Index, $"deal {pos}: the recipient opens the real card");
                T.True(!CardNft.CanOpen(sealedHex, otherPriv), $"deal {pos}: the OTHER seat CANNOT open it (privacy)");
            }

            // the ShuffleStage steps carry a 32-byte COMMITMENT then the REAL masked deck (33-byte points)
            var shuffles = tape.Steps.Where(s => s.Kind == TxKind.ShuffleStage).ToList();
            foreach (var sstep in shuffles)
            {
                var fields = TxTemplates.Parse(sstep.Tx.Outs[0].Script)!.Fields; // fields: handId, step, commitment, deck
                T.Eq(fields[2].Length, 32, "shuffle commitment is a 32-byte hash (commit-then-reveal)");
                var deckField = fields[3];
                T.Eq(deckField.Length, deck.Length * 33, "masked deck = one 33-byte point per card");
                for (int i = 0; i < deck.Length; i++) T.True(deckField[i * 33] is 0x02 or 0x03, "each masked card is a compressed point");
            }

            // the real pot settles to the winner (seat 0 = trip aces), co-signed 2-of-2
            T.Eq(tape.WinnerSeat, 0, "seat 0 wins");
            T.True(Chain.VerifyMultisig2of2(tape.Settlement, 0, a.Pub, b.Pub, pot), "settlement is a valid 2-of-2 spend");
            T.Eq(T.Hex(tape.Settlement.Outs[0].Script), T.Hex(Chain.P2pkhLockForPub(a.Pub)), "pot paid to the winner");

            // value left the wallet only as fees + the parked step/pot values (no money created)
            T.True(w.Balance < 100_000_000 && w.Balance > 100_000_000 - 1_000_000, "wallet spent only modest fees/values");
        });

        T.Run("a Blackjack hand emits the full on-chain tape (sealed NFT cards, real pot escrow, settle to winner)", () =>
        {
            var player = Secp256k1.GenerateKeyPair(); var dealer = Secp256k1.GenerateKeyPair();
            var w = new OnChainWallet(WalletKeys.NewSeed());
            w.Add(new OnChainWallet.Utxo("bb".PadRight(64, '2'), 0, 100_000_000, 0, 0));
            // player Ts+9h=19 (stands); dealer Td+6c=16 draws Kh=26 → dealer busts → player wins.
            var deck = new[] { C("Ts"), C("Td"), C("9h"), C("6c"), C("Kh"), C("2s"), C("3s"), C("4s"), C("5s"), C("7s"), C("8s"), C("Js") };
            long bet = 20000;
            var tape = OnChainHandTape.BuildBlackjack(w, player, dealer, deck, bet, tableId: new byte[16], stepValue: 1, fee: 1);

            // every step is a distinct, real transaction
            var txids = tape.Steps.Select(s => Chain.Txid(s.Tx)).ToHashSet();
            T.Eq(txids.Count, tape.Steps.Count, "all Blackjack tape txids distinct");

            // genesis + escrow + recovery + 2 shuffle + 4 deal are all present at the front
            T.Eq(tape.Steps[0].Kind.ToString(), TxKind.TableGenesis.ToString(), "starts with table genesis");
            T.True(tape.Steps.Any(s => s.Kind == TxKind.PotEscrow), "has a real pot escrow");
            T.True(tape.Steps.Any(s => s.Kind == TxKind.Recovery), "has the up-front recovery");
            T.Eq(tape.Steps.Count(s => s.Kind == TxKind.Deal), 4, "four initial deal txs");

            // the player's two hole cards are ECDH-sealed NFTs only the player opens; the dealer up-card is revealed
            var deals = tape.Steps.Where(s => s.Kind == TxKind.Deal).ToList();
            var p0 = TxTemplates.Parse(deals[0].Tx.Outs[0].Script)!.Fields;   // player card (sealed)
            T.Eq(CardNft.Open(Convert.ToHexString(p0[2]), player.Priv).CardIndex, deck[0].Index, "player opens their own sealed card");
            T.True(!CardNft.CanOpen(Convert.ToHexString(p0[2]), dealer.Priv), "dealer cannot open the player's card (privacy)");
            var d0 = TxTemplates.Parse(deals[1].Tx.Outs[0].Script)!.Fields;   // dealer up-card (revealed cleartext index)
            T.Eq(d0[2][0], (byte)deck[1].Index, "dealer up-card is revealed");

            // the pot settles to the WINNER (player) as a valid 2-of-2 spend
            T.Eq(tape.WinnerSeat, 0, "player wins (dealer busts)");
            T.True(Chain.VerifyMultisig2of2(tape.Settlement, 0, player.Pub, dealer.Pub, bet * 2), "settlement is a valid 2-of-2 spend");
            T.Eq(T.Hex(tape.Settlement.Outs[0].Script), T.Hex(Chain.P2pkhLockForPub(player.Pub)), "pot paid to the winning player");
        });

        T.Run("the on-chain shuffle is a REAL commutative-encryption masking (unmasks back to the base deck)", () =>
        {
            var sh = OnChainHandTape.RealShuffle(9);
            // every masked card is a valid compressed point
            foreach (var p in sh.Masked1) { T.Eq(p.Length, 33, "33-byte point"); T.True(p[0] is 0x02 or 0x03, "compressed"); }
            // removing BOTH seats' global scalars from the final deck recovers exactly the base points (hidden order)
            var recovered = sh.Masked1.Select(p => T.Hex(MentalPokerEC.Unmask(p, new[] { sh.G1, sh.G0 }))).OrderBy(x => x).ToArray();
            var baseSet = sh.Base.Select(T.Hex).OrderBy(x => x).ToArray();
            T.Eq(string.Join(",", recovered), string.Join(",", baseSet), "unmasking both globals recovers the base deck — no card lost, none added, none fixable by one seat");
        });

        T.Run("the on-chain shuffle is COMMITTED and the reveal VERIFIES (provably honest)", () =>
        {
            var sh = OnChainHandTape.RealShuffle(9);
            T.True(ShuffleProof.VerifyShuffle(sh.Base, sh.Masked0, sh.G0, sh.P0, ShuffleProof.CommitShuffle(sh.G0, sh.P0)),
                   "seat 0's shuffle recomputes to its commitment");
            T.True(ShuffleProof.VerifyShuffle(sh.Masked0, sh.Masked1, sh.G1, sh.P1, ShuffleProof.CommitShuffle(sh.G1, sh.P1)),
                   "seat 1's shuffle recomputes to its commitment");
            var badPerm = (int[])sh.P0.Clone(); (badPerm[0], badPerm[1]) = (badPerm[1], badPerm[0]);
            T.True(!ShuffleProof.VerifyShuffle(sh.Base, sh.Masked0, sh.G0, badPerm, ShuffleProof.CommitShuffle(sh.G0, sh.P0)),
                   "a tampered shuffle is provably CAUGHT");
        });
    }
}
