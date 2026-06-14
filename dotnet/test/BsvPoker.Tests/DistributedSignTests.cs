using System.Text;
using BsvPoker.Core;
using BsvPoker.Crypto;

namespace BsvPoker.Tests;

/// <summary>
/// FULLY-distributed threshold signing: the key, the per-signature ephemeral key, and the blinding secret are
/// ALL produced by independent real DKGs (<see cref="DistributedKeyGen"/>) — no local simulation anywhere — and
/// the assembled (r,s) verifies against the DKG'd public key with the ordinary verifier. End-to-end, a pot
/// locked to a DKG'd key pays a winner on the standard consensus path: dealerless custody, dealerless signing.
/// </summary>
public static class DistributedSignTests
{
    /// <summary>Run a (t+1)-of-n DKG among n fresh parties; return their joint key shares (by index) and the
    /// agreed joint public key.</summary>
    private static ((int X, byte[] Y)[] Shares, byte[] Pub) RunDkg(int n, int t)
    {
        var kp = Enumerable.Range(1, n).ToDictionary(i => i, _ => Secp256k1.GenerateKeyPair());
        var pubs = kp.ToDictionary(k => k.Key, k => k.Value.Pub);
        var sid = System.Text.Encoding.ASCII.GetBytes("dsign-session");
        var dealt = kp.ToDictionary(k => k.Key, k => DistributedKeyGen.Deal(k.Key, k.Value.Pub, t, pubs, sid));
        var round1 = dealt.Values.Select(d => d.Msg).ToList();
        var results = kp.ToDictionary(k => k.Key, k => DistributedKeyGen.Finalize(k.Key, k.Value.Priv, round1, sid));
        foreach (var r in results.Values) if (r.Blame != null) throw new Exception("unexpected DKG blame");
        var shares = results.OrderBy(r => r.Key).Select(r => (r.Key, r.Value.JointShare)).ToArray();
        return (shares, results[1].JointPublicKey);
    }

    private static byte[]? SignDistributed(byte[] digest, (int X, byte[] Y)[] aShares, int n, int t)
    {
        for (int attempt = 0; attempt < 48; attempt++)
        {
            var (kShares, kPub) = RunDkg(n, t);     // ephemeral key, distributed
            var (bShares, _) = RunDkg(n, t);        // blinding secret, distributed
            var sig = ThresholdEcdsa.AssembleSignature(digest, aShares, kShares, kPub, bShares);
            if (sig != null) return sig;
        }
        return null;
    }

    public static void All()
    {
        Console.WriteLine("fully-distributed threshold signing (key + ephemeral + blinding all DKG'd):");

        T.Run("a signature assembled from THREE independent DKGs verifies against the DKG'd key", () =>
        {
            const int n = 3, t = 1;
            var (aShares, aPub) = RunDkg(n, t);
            var digest = Hashes.Sha256(Encoding.UTF8.GetBytes("fully distributed threshold spend"));
            var sig = SignDistributed(digest, aShares, n, t);
            T.True(sig != null, "assembled a non-degenerate signature");
            T.True(Secp256k1.VerifyDigest(aPub, digest, sig!), "the fully-distributed signature verifies against a·G");
            T.False(Secp256k1.VerifyDigest(aPub, Hashes.Sha256(Encoding.UTF8.GetBytes("other message")), sig!), "it does not verify a different message");
        });

        T.Run("end-to-end: a DKG-custodied pot pays the winner, verified on the standard consensus path", () =>
        {
            const int n = 3, t = 1;
            var (aShares, aPub) = RunDkg(n, t);                       // the pot's dealerless custody key
            const string potTxid = "44" + "00000000000000000000000000000000000000000000000000000000000000";
            const long amount = 90_000, fee = 200;
            var winner = Secp256k1.GenerateKeyPair().Pub;
            var tx = new Chain.Tx(2,
                new List<Chain.TxIn> { new(potTxid, 0, Array.Empty<byte>(), 0xffffffff) },
                new List<Chain.TxOut> { new(amount - fee, Chain.P2pkhLockForPub(winner)) }, 0);
            var digest = Chain.SighashForkId(tx, 0, Chain.P2pkhLockForPub(aPub), amount);
            var sig = SignDistributed(digest, aShares, n, t);
            T.True(sig != null, "assembled the settlement signature from DKGs");
            var signed = Chain.ApplyP2pkhSig(tx, 0, sig!, aPub);
            T.True(Chain.VerifyP2pkhInput(signed, 0, aPub, amount), "DKG-custodied pot pays the winner, verified on the standard path");
            T.Eq(signed.Outs[0].Value, amount - fee, "winner receives amount − fee (value conserved)");
        });
    }
}
