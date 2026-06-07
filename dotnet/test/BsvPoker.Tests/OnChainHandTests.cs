using BsvPoker.Core;
using BsvPoker.Core.Games;
using BsvPoker.Crypto;

namespace BsvPoker.Tests;

/// <summary>The on-chain money lifecycle of a hand: wallet funds a 2-of-2 escrow; settlement pays the
/// winner; the co-signed nLockTime recovery refunds each stake. All real, signed, verifiable.</summary>
public static class OnChainHandTests
{
    public static void All()
    {
        Console.WriteLine("on-chain hand money lifecycle (escrow → settle / recover):");
        var a = Secp256k1.GenerateKeyPair();
        var b = Secp256k1.GenerateKeyPair();

        OnChainWallet Funded()
        {
            var w = new OnChainWallet(WalletKeys.NewSeed());
            w.Add(new OnChainWallet.Utxo("aa".PadRight(64, '1'), 0, 50000, 0, 0));
            w.Add(new OnChainWallet.Utxo("bb".PadRight(64, '2'), 1, 50000, 0, 1));
            return w;
        }

        T.Run("the wallet funds the pot into a 2-of-2 escrow output", () =>
        {
            var w = Funded();
            var fund = OnChainHand.FundEscrow(w, a.Pub, b.Pub, 40000, 500);
            T.True(w.VerifySpend(fund), "funding tx signs + conserves value");
            T.Eq(T.Hex(fund.Tx.Outs[0].Script), T.Hex(Chain.MultisigLock2of2(a.Pub, b.Pub)), "output is the 2-of-2 escrow");
            T.Eq(fund.Tx.Outs[0].Value, 40000L, "pot funded");
        });

        T.Run("settlement pays the winner from the escrow (both sign)", () =>
        {
            var w = Funded();
            var fund = OnChainHand.FundEscrow(w, a.Pub, b.Pub, 40000, 500);
            var escrowTxid = Chain.Txid(fund.Tx);
            var settle = OnChainHand.Settle(escrowTxid, 0, 40000, a.Pub, 500, a.Priv, a.Pub, b.Priv, b.Pub);
            T.True(Chain.VerifyMultisig2of2(settle, 0, a.Pub, b.Pub, 40000), "both signatures verify");
            T.Eq(settle.Outs[0].Value, 39500L, "winner paid pot - fee");
            T.Eq(T.Hex(settle.Outs[0].Script), T.Hex(Chain.P2pkhLockForPub(a.Pub)), "paid to the winner");
        });

        T.Run("recovery refunds each stake (always-recoverable; co-signed, nLockTime)", () =>
        {
            var w = Funded();
            var fund = OnChainHand.FundEscrow(w, a.Pub, b.Pub, 40000, 500);
            var escrowTxid = Chain.Txid(fund.Tx);
            var rec = OnChainHand.Recover(escrowTxid, 0, a.Pub, 20000, b.Pub, 20000, 500, 800000, a.Priv, b.Priv);
            T.True(Chain.VerifyMultisig2of2(rec, 0, a.Pub, b.Pub, 40000), "recovery is co-signed and valid");
            T.Eq(rec.Outs[0].Value + rec.Outs[1].Value, 39500L, "both stakes (minus fee) returned");
            T.Eq(rec.LockTime, 800000u, "future locktime"); T.Eq(rec.Ins[0].Sequence, 0xfffffffeu, "non-final sequence");
        });
    }
}
