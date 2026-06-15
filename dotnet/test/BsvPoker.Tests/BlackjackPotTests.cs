using BsvPoker.Core;
using BsvPoker.Core.Games;
using BsvPoker.Crypto;

namespace BsvPoker.Tests;

/// <summary>
/// The multiplayer Blackjack POT: an n-of-n locked output funded by all players (3 players ⇒ 3-of-3), settled
/// by the hand result with the remaining bank split among the players. Proven: the settlement conserves the
/// pot to the satoshi, verifies only when EVERY player signed, and a single missing/forged signature (an
/// (n-1)-collusion) is rejected — no subset of players can move the money.
/// </summary>
public static class BlackjackPotTests
{
    private static Card C(int rank) => new Card(rank, Suit.Clubs);
    private static List<Card> Deck(params int[] r) => r.Select(C).ToList();

    public static void All()
    {
        Console.WriteLine("multiplayer Blackjack pot (n-of-n funded, result-distributed, collusion-safe):");

        T.Run("3-of-3 pot: settle a hand, distribute by result + remaining bank, conserve the pot", () =>
        {
            var ks = Enumerable.Range(0, 3).Select(_ => Secp256k1.GenerateKeyPair()).ToArray();
            var pubs = ks.Select(k => k.Pub).ToList();
            var seeds = ks.Select(k => k.Priv).ToList();

            // p0=[T,T]=20 (win), p1=[5,6]=11 (lose), p2=[9,9]=18 (win); dealer=[T,7]=17
            var g = GroupBlackjack.Create(new long[] { 10, 10, 10 }, Deck(10, 5, 9, 10, 10, 6, 9, 7));
            g.Act(0, BjAction.Stand); g.Act(1, BjAction.Stand); g.Act(2, BjAction.Stand);
            T.True(g.Complete, "the hand completed");

            long dealerBank = 30, pot = dealerBank + 30;
            var final = BlackjackPot.Settle(g, dealerBank);
            T.Eq(final.Sum(), pot, "the distributed amounts sum EXACTLY to the pot (bank + all bets)");
            T.True(final[0] > final[1] && final[2] > final[1], "winners receive more than the loser");

            var unsigned = BlackjackPot.BuildSettlement("ab".PadRight(64, '0'), 0, pot, pubs, final, fee: 0);
            var signed = BlackjackPot.CoSign(unsigned, pubs, pot, seeds);
            T.True(Chain.VerifyMultisigNofN(signed, 0, pubs, pot), "the settlement verifies when ALL THREE players co-signed");
            // value conserved on-chain: outputs sum to the pot (fee 0)
            T.Eq(signed.Outs.Sum(o => o.Value), pot, "the settlement outputs conserve the pot");
        });

        T.Run("3 players FUND one n-of-n pot (an input each), then the funded pot pays out co-signed — full lifecycle", () =>
        {
            var ks = Enumerable.Range(0, 3).Select(_ => Secp256k1.GenerateKeyPair()).ToArray();
            var pubs = ks.Select(k => k.Pub).ToList();
            // each player contributes a 100k coin, staking 10k into the pot (change 90k each)
            var contribs = Enumerable.Range(0, 3).Select(i =>
                new BlackjackPot.Contribution(new OnChainWallet.Utxo(((char)('a' + i)).ToString().PadRight(64, (char)('1' + i)), 0, 100_000, 0, 0), pubs[i], 10_000, pubs[i])).ToList();

            var fund = BlackjackPot.BuildFunding(contribs, pubs, fee: 0);
            T.Eq(fund.Pot, 30_000, "the pot is the sum of all stakes");
            var tx = fund.Tx;
            for (int i = 0; i < 3; i++) tx = BlackjackPot.SignInput(tx, i, ks[i].Priv, ks[i].Pub, 100_000);
            T.True(BlackjackPot.VerifyFunding(tx, contribs, 0), "every player signed ONLY their own input; value is conserved");
            T.True(Chain.VerifyP2pkhInput(tx, 0, pubs[0], 100_000) && Chain.VerifyP2pkhInput(tx, 2, pubs[2], 100_000), "each contribution input is independently valid");

            // the pot (vout 0) can now be settled — and ONLY with all three signatures
            var final = new long[] { fund.Pot, 0, 0 };   // any distribution summing to the pot
            var settle = BlackjackPot.BuildSettlement(Chain.Txid(tx), 0, fund.Pot, pubs, final, fee: 0);
            var signed = BlackjackPot.CoSign(settle, pubs, fund.Pot, ks.Select(k => k.Priv).ToList());
            T.True(Chain.VerifyMultisigNofN(signed, 0, pubs, fund.Pot), "the FUNDED n-of-n pot is spent only with ALL players' signatures");
        });

        T.Run("pre-signed nLockTime REFUND: griefing-safe — every stake comes back if the payout is never co-signed", () =>
        {
            var ks = Enumerable.Range(0, 3).Select(_ => Secp256k1.GenerateKeyPair()).ToArray();
            var pubs = ks.Select(k => k.Pub).ToList();
            // three per-player escrow coins (one each) into the same n-of-n pot, 10k stake each
            var pot = new List<BlackjackPot.PotIn>
            {
                new("aa".PadRight(64, '1'), 0, 10_000),
                new("bb".PadRight(64, '2'), 0, 10_000),
                new("cc".PadRight(64, '3'), 0, 10_000),
            };
            var stakes = new long[] { 10_000, 10_000, 10_000 };

            var refund = BlackjackPot.BuildSessionRecovery(pot, pubs, stakes, fee: 0, lockHeight: 950_000);
            T.Eq((int)refund.LockTime, 950_000, "the refund is locked until the agreed height");
            T.True(refund.Ins.All(i => i.Sequence != 0xffffffff), "inputs are non-final so the locktime binds");

            // every player co-signs the refund AT FUNDING (here, all of them) — each input needs all three sigs
            var perInput = new List<IReadOnlyList<byte[]>>();
            for (int j = 0; j < pot.Count; j++)
            {
                var col = new List<byte[]>();
                for (int p = 0; p < pubs.Count; p++) col.Add(BlackjackPot.SignSessionInput(refund, j, pubs, pot[j].Value, ks[p].Priv));
                perInput.Add(col);
            }
            var signed = BlackjackPot.ApplySessionSigs(refund, perInput);
            for (int j = 0; j < pot.Count; j++)
                T.True(Chain.VerifyMultisigNofN(signed, j, pubs, pot[j].Value), $"refund input {j} is a valid n-of-n spend");
            T.Eq(signed.Outs.Sum(o => o.Value), 30_000L, "the refund returns every stake (the whole pot)");
            for (int i = 0; i < 3; i++)
                T.Eq(T.Hex(signed.Outs[i].Script), T.Hex(Chain.P2pkhLockForPub(pubs[i])), $"player {i}'s stake is refunded to player {i}");
        });

        T.Run("LEAVER SPLIT: one tx pays the leaver AND re-escrows the remainder to a new n-of-n of the rest", () =>
        {
            var ks = Enumerable.Range(0, 3).Select(_ => Secp256k1.GenerateKeyPair()).ToArray();
            var pubs = ks.Select(k => k.Pub).ToList();
            var pot = new List<BlackjackPot.PotIn>
            {
                new("aa".PadRight(64, '1'), 0, 10_000),
                new("bb".PadRight(64, '2'), 0, 10_000),
                new("cc".PadRight(64, '3'), 0, 10_000),
            };
            // player 0 leaves with 9,000; the remaining 21,000 (minus fee) re-escrows to a 2-of-2 of players 1 & 2
            var leaverPubs = new List<byte[]> { pubs[0] };
            var leaverPayouts = new List<long> { 9_000 };
            var remaining = new List<byte[]> { pubs[1], pubs[2] };
            long newPot = 30_000 - 9_000 - 0;
            var split = BlackjackPot.BuildLeaverSplit(pot, leaverPubs, leaverPayouts, remaining, newPot, fee: 0);

            // co-signed by ALL THREE current players (each input is the 3-of-3)
            var perInput = new List<IReadOnlyList<byte[]>>();
            for (int j = 0; j < pot.Count; j++)
            {
                var col = new List<byte[]>();
                for (int p = 0; p < pubs.Count; p++) col.Add(BlackjackPot.SignSessionInput(split, j, pubs, pot[j].Value, ks[p].Priv));
                perInput.Add(col);
            }
            var signed = BlackjackPot.ApplySessionSigs(split, perInput);
            for (int j = 0; j < pot.Count; j++) T.True(Chain.VerifyMultisigNofN(signed, j, pubs, pot[j].Value), $"split input {j} is a valid 3-of-3 spend");
            T.Eq(signed.Outs.Sum(o => o.Value), 30_000L, "the split conserves the pot");
            T.Eq(T.Hex(signed.Outs[0].Script), T.Hex(Chain.P2pkhLockForPub(pubs[0])), "the leaver is paid to their own address");
            uint vout = BlackjackPot.SplitNewPotVout(leaverPayouts);
            T.Eq((int)vout, 1, "the new pot is the output after the single leaver payout");
            T.Eq(T.Hex(signed.Outs[(int)vout].Script), T.Hex(Chain.MultisigLockNofN(remaining)), "the remainder is re-escrowed to the 2-of-2 of the players who stay");
            T.Eq(signed.Outs[(int)vout].Value, newPot, "the new pot holds the remainder");
        });

        T.Run("(n-1)-collusion FAILS: a settlement missing one player's real signature is rejected", () =>
        {
            var ks = Enumerable.Range(0, 3).Select(_ => Secp256k1.GenerateKeyPair()).ToArray();
            var pubs = ks.Select(k => k.Pub).ToList();
            var attacker = Secp256k1.GenerateKeyPair();

            var g = GroupBlackjack.Create(new long[] { 10, 10, 10 }, Deck(10, 5, 9, 10, 10, 6, 9, 7));
            g.Act(0, BjAction.Stand); g.Act(1, BjAction.Stand); g.Act(2, BjAction.Stand);
            long pot = 30 + 30;
            var final = BlackjackPot.Settle(g, 30);
            var unsigned = BlackjackPot.BuildSettlement("cd".PadRight(64, '0'), 0, pot, pubs, final, 0);

            // players 0 and 1 sign honestly; player 2's slot is signed by an ATTACKER key (collusion to cut p2 out)
            var seeds = new List<byte[]> { ks[0].Priv, ks[1].Priv, attacker.Priv };
            var forged = BlackjackPot.CoSign(unsigned, pubs, pot, seeds);
            T.False(Chain.VerifyMultisigNofN(forged, 0, pubs, pot), "a settlement without player 2's REAL signature is rejected (no n-1 theft)");
        });
    }
}
