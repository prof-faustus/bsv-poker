using BsvPoker.Core;
using BsvPoker.Core.Games;
using BsvPoker.Crypto;

namespace BsvPoker.Tests;

/// <summary>
/// Real two-party pot funding: each player contributes their OWN input to a single 2-of-2 escrow, signs only
/// their own input, and the result is consensus-valid with value conserved — not one wallet funding the pot.
/// </summary>
public static class TwoPartyEscrowTests
{
    public static void All()
    {
        Console.WriteLine("two-party escrow funding (each player funds their own stake):");

        T.Run("both players sign their own input; the pot is a single 2-of-2 output, value conserved", () =>
        {
            var a = Secp256k1.GenerateKeyPair(); var b = Secp256k1.GenerateKeyPair();
            var aChange = Secp256k1.GenerateKeyPair().Pub; var bChange = Secp256k1.GenerateKeyPair().Pub;
            var aUtxo = new OnChainWallet.Utxo("aa".PadRight(64, '1'), 0, 1_000_000, 0, 0);
            var bUtxo = new OnChainWallet.Utxo("bb".PadRight(64, '2'), 1, 800_000, 0, 1);
            long stakeA = 300_000, stakeB = 300_000, fee = 2000;

            var f = TwoPartyEscrow.BuildUnsigned(aUtxo, aChange, stakeA, bUtxo, bChange, stakeB, a.Pub, b.Pub, fee);
            T.Eq(f.Pot, 600_000L, "pot = stakeA + stakeB");
            T.Eq(T.Hex(f.Tx.Outs[0].Script), T.Hex(Chain.MultisigLock2of2(a.Pub, b.Pub)), "escrow output is the 2-of-2");

            // each side signs ONLY its own input (independently — this is the distributed step)
            var signedA = TwoPartyEscrow.SignA(f.Tx, a.Priv, a.Pub, aUtxo.Value);
            var fully = TwoPartyEscrow.SignB(signedA, b.Priv, b.Pub, bUtxo.Value);

            T.True(TwoPartyEscrow.Verify(fully, a.Pub, aUtxo.Value, b.Pub, bUtxo.Value, fee), "both inputs valid + value conserved");
            T.True(Chain.VerifyP2pkhInput(fully, 0, a.Pub, aUtxo.Value), "A's input is signed by A");
            T.True(Chain.VerifyP2pkhInput(fully, 1, b.Pub, bUtxo.Value), "B's input is signed by B");
        });

        T.Run("a player who cannot cover stake + fee is rejected", () =>
        {
            var a = Secp256k1.GenerateKeyPair(); var b = Secp256k1.GenerateKeyPair();
            var aUtxo = new OnChainWallet.Utxo("aa".PadRight(64, '1'), 0, 100_000, 0, 0);
            var bUtxo = new OnChainWallet.Utxo("bb".PadRight(64, '2'), 1, 800_000, 0, 1);
            T.Throws(() => TwoPartyEscrow.BuildUnsigned(aUtxo, a.Pub, 300_000, bUtxo, b.Pub, 300_000, a.Pub, b.Pub, 2000),
                "A cannot cover a 300k stake from a 100k coin");
        });

        T.Run("the settlement from a two-party escrow pays the winner and is co-signed", () =>
        {
            var a = Secp256k1.GenerateKeyPair(); var b = Secp256k1.GenerateKeyPair();
            var aUtxo = new OnChainWallet.Utxo("aa".PadRight(64, '1'), 0, 1_000_000, 0, 0);
            var bUtxo = new OnChainWallet.Utxo("bb".PadRight(64, '2'), 1, 1_000_000, 0, 1);
            var f = TwoPartyEscrow.BuildUnsigned(aUtxo, a.Pub, 400_000, bUtxo, b.Pub, 400_000, a.Pub, b.Pub, 2000);
            var fully = TwoPartyEscrow.SignB(TwoPartyEscrow.SignA(f.Tx, a.Priv, a.Pub, aUtxo.Value), b.Priv, b.Pub, bUtxo.Value);
            var escrowTxid = Chain.Txid(fully);
            // winner A takes the pot via a co-signed settlement of the real two-party escrow
            var settle = OnChainHand.Settle(escrowTxid, f.EscrowVout, f.Pot, a.Pub, 1000, a.Priv, a.Pub, b.Priv, b.Pub);
            T.True(Chain.VerifyMultisig2of2(settle, 0, a.Pub, b.Pub, f.Pot), "co-signed settlement of the two-party pot is valid");
        });
    }
}
