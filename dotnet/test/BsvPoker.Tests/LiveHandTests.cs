using System.Collections.Concurrent;
using BsvPoker.Core;
using BsvPoker.Core.Games;
using BsvPoker.Crypto;
using BsvPoker.Net;

namespace BsvPoker.Tests;

/// <summary>
/// The complete LIVE two-party on-chain hand between two separate players, exchanged only as messages:
/// each funds + signs their own escrow input, the dealerless deal runs with verifiable proofs and per-card
/// privacy, and the pot settles to the winner via a co-signed settlement. No shared RNG, no single wallet
/// funding both, no off-chain channel.
/// </summary>
public static class LiveHandTests
{
    private sealed class Chan : LiveDeal.IDealChannel
    {
        private readonly BlockingCollection<string> _in, _out;
        public Chan(BlockingCollection<string> recv, BlockingCollection<string> send) { _in = recv; _out = send; }
        public void Send(string msg) => _out.Add(msg);
        public string Receive() => _in.Take();
    }

    public static void All()
    {
        Console.WriteLine("live two-party ON-CHAIN hand (escrow + proven deal + co-signed settlement):");

        T.Run("two peers fund, deal fairly, and settle the pot — chips conserved, proofs verified", () =>
        {
            var a = Secp256k1.GenerateKeyPair(); var b = Secp256k1.GenerateKeyPair();
            var aChange = Secp256k1.GenerateKeyPair().Pub; var bChange = Secp256k1.GenerateKeyPair().Pub;
            var aUtxo = new OnChainWallet.Utxo("aa".PadRight(64, '1'), 0, 1_000_000, 0, 0);
            var bUtxo = new OnChainWallet.Utxo("bb".PadRight(64, '2'), 1, 1_000_000, 0, 1);
            long stake = 300_000, fee = 2000, settleFee = 1000;

            // initiator must be the player with input 0 of the escrow; pick A as initiator
            var aToB = new BlockingCollection<string>();
            var bToA = new BlockingCollection<string>();
            var chA = new Chan(bToA, aToB);
            var chB = new Chan(aToB, bToA);

            LiveHand.Result? rA = null, rB = null;
            var tA = System.Threading.Tasks.Task.Run(() => rA = LiveHand.RunInitiator(chA, aUtxo, aChange, (a.Priv, a.Pub), stake, fee, settleFee));
            var tB = System.Threading.Tasks.Task.Run(() => rB = LiveHand.RunResponder(chB, bUtxo, bChange, (b.Priv, b.Pub), stake, fee, settleFee));
            T.True(System.Threading.Tasks.Task.WaitAll(new[] { tA, tB }, TimeSpan.FromSeconds(45)), "both peers completed the hand");

            // both agree on the escrow, the pot, the board, the winner
            T.Eq(Chain.Txid(rA!.EscrowTx), Chain.Txid(rB!.EscrowTx), "both built the same escrow tx");
            T.Eq(rA.Pot, 600_000L, "pot = both stakes");
            T.Eq(rA.WinnerSeat, rB.WinnerSeat, "both agree on the winner");
            T.True(rA.ProofsVerified && rB.ProofsVerified, "both verified the other's shuffle/remask proofs");

            // the escrow is a real two-party signed tx; the settlement is a valid co-signed 2-of-2 spend; conserved
            T.True(TwoPartyEscrow.Verify(rA.EscrowTx, a.Pub, aUtxo.Value, b.Pub, bUtxo.Value, fee), "escrow: both inputs validly signed");
            T.True(Chain.VerifyMultisig2of2(rA.Settlement, 0, a.Pub, b.Pub, rA.Pot), "settlement co-signed + consensus-valid");
            T.Eq(Chain.Txid(rA.Settlement), Chain.Txid(rB.Settlement), "both produced the same settlement");
            T.Eq(rA.Settlement.Outs.Sum(o => o.Value), rA.Pot - settleFee, "pot - fee paid out (chips conserved)");

            // each read their own holes; all dealt cards distinct
            var all = rA.MyHoles.Concat(rB.MyHoles).Concat(rA.Board).Select(c => c.Index).ToList();
            T.Eq(all.Distinct().Count(), 9, "2+2+5 distinct cards (genuine two-party shuffle)");
        });
    }
}
