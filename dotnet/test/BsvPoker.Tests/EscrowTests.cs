using BsvPoker.Core;
using BsvPoker.Crypto;

namespace BsvPoker.Tests;

/// <summary>
/// The on-chain money layer: a 2-of-2 escrow funds the pot, a cooperative settlement pays the winner, and
/// a pre-signed nLockTime recovery refunds each funder. Verification is strict (consensus-grade DER,
/// bounds-checked scriptSig, exact hashtype, sig order), proven with positive AND hostile-negative tests.
/// </summary>
public static class EscrowTests
{
    private const string EscrowTxid = "b7c0de11223344556677889900aabbccddeeff00112233445566778899aabbcc";
    private const long Amount = 200_000;
    private const long Fee = 500;

    public static void All()
    {
        Console.WriteLine("on-chain 2-of-2 escrow (pot funding / cooperative settlement / unilateral recovery):");

        var A = Secp256k1.GenerateKeyPair();
        var B = Secp256k1.GenerateKeyPair();
        var C = Secp256k1.GenerateKeyPair(); // an outsider

        T.Run("the 2-of-2 lock is OP_2 <pubA> <pubB> OP_2 OP_CHECKMULTISIG", () =>
        {
            var s = Chain.MultisigLock2of2(A.Pub, B.Pub);
            T.Eq(s.Length, 1 + 34 + 34 + 1 + 1, "script length");
            T.True(s[0] == 0x52 && s[^1] == 0xae && s[^2] == 0x52, "OP_2 … OP_2 OP_CHECKMULTISIG");
            T.Eq(s[1], (byte)33, "33-byte push for pubA");
            T.Eq(s[35], (byte)33, "33-byte push for pubB");
        });

        T.Run("cooperative settlement: both sign, it verifies and pays the winner amount-fee", () =>
        {
            var tx = Chain.BuildCooperativeSettlement(EscrowTxid, 0, Amount, A.Pub, Fee);
            var sigA = Chain.SignMultisig(tx, 0, A.Pub, B.Pub, Amount, A.Priv);
            var sigB = Chain.SignMultisig(tx, 0, A.Pub, B.Pub, Amount, B.Priv);
            var signed = Chain.ApplyMultisigScriptSig(tx, 0, sigA, sigB);
            T.True(Chain.VerifyMultisig2of2(signed, 0, A.Pub, B.Pub, Amount), "both signatures verify");
            T.Eq(signed.Outs[0].Value, Amount - Fee, "winner is paid amount-fee");
        });

        T.Run("HOSTILE: a tampered amount fails verification", () =>
        {
            var tx = Chain.BuildCooperativeSettlement(EscrowTxid, 0, Amount, A.Pub, Fee);
            var sigA = Chain.SignMultisig(tx, 0, A.Pub, B.Pub, Amount, A.Priv);
            var sigB = Chain.SignMultisig(tx, 0, A.Pub, B.Pub, Amount, B.Priv);
            var signed = Chain.ApplyMultisigScriptSig(tx, 0, sigA, sigB);
            T.False(Chain.VerifyMultisig2of2(signed, 0, A.Pub, B.Pub, Amount + 1), "wrong amount → invalid sighash");
        });

        T.Run("HOSTILE: an outsider's signature in place of a signer fails", () =>
        {
            var tx = Chain.BuildCooperativeSettlement(EscrowTxid, 0, Amount, A.Pub, Fee);
            var sigA = Chain.SignMultisig(tx, 0, A.Pub, B.Pub, Amount, A.Priv);
            var sigC = Chain.SignMultisig(tx, 0, A.Pub, B.Pub, Amount, C.Priv); // outsider tries to be B
            var signed = Chain.ApplyMultisigScriptSig(tx, 0, sigA, sigC);
            T.False(Chain.VerifyMultisig2of2(signed, 0, A.Pub, B.Pub, Amount), "outsider cannot sign for B");
        });

        T.Run("HOSTILE: signatures in the wrong order fail (CHECKMULTISIG is order-sensitive)", () =>
        {
            var tx = Chain.BuildCooperativeSettlement(EscrowTxid, 0, Amount, A.Pub, Fee);
            var sigA = Chain.SignMultisig(tx, 0, A.Pub, B.Pub, Amount, A.Priv);
            var sigB = Chain.SignMultisig(tx, 0, A.Pub, B.Pub, Amount, B.Priv);
            var swapped = Chain.ApplyMultisigScriptSig(tx, 0, sigB, sigA); // wrong order
            T.False(Chain.VerifyMultisig2of2(swapped, 0, A.Pub, B.Pub, Amount), "order mismatch rejected");
        });

        T.Run("HOSTILE: a wrong hashtype byte (not 0x41) fails", () =>
        {
            var tx = Chain.BuildCooperativeSettlement(EscrowTxid, 0, Amount, A.Pub, Fee);
            var sigA = Chain.SignMultisig(tx, 0, A.Pub, B.Pub, Amount, A.Priv);
            var sigB = Chain.SignMultisig(tx, 0, A.Pub, B.Pub, Amount, B.Priv);
            sigA[^1] = 0x01; // SIGHASH_ALL without FORKID — not allowed on BSV
            var signed = Chain.ApplyMultisigScriptSig(tx, 0, sigA, sigB);
            T.False(Chain.VerifyMultisig2of2(signed, 0, A.Pub, B.Pub, Amount), "non-0x41 hashtype rejected");
        });

        T.Run("HOSTILE: trailing bytes in the scriptSig are rejected", () =>
        {
            var tx = Chain.BuildCooperativeSettlement(EscrowTxid, 0, Amount, A.Pub, Fee);
            var sigA = Chain.SignMultisig(tx, 0, A.Pub, B.Pub, Amount, A.Priv);
            var sigB = Chain.SignMultisig(tx, 0, A.Pub, B.Pub, Amount, B.Priv);
            var ss = Chain.MultisigScriptSig(sigA, sigB).Concat(new byte[] { 0x99 }).ToArray();
            var ins = tx.Ins.ToList(); ins[0] = ins[0] with { ScriptSig = ss };
            T.False(Chain.VerifyMultisig2of2(tx with { Ins = ins }, 0, A.Pub, B.Pub, Amount), "trailing byte rejected");
        });

        T.Run("HOSTILE: a missing OP_0 CHECKMULTISIG dummy is rejected", () =>
        {
            var tx = Chain.BuildCooperativeSettlement(EscrowTxid, 0, Amount, A.Pub, Fee);
            var sigA = Chain.SignMultisig(tx, 0, A.Pub, B.Pub, Amount, A.Priv);
            var sigB = Chain.SignMultisig(tx, 0, A.Pub, B.Pub, Amount, B.Priv);
            var noDummy = new List<byte> { (byte)sigA.Length }; noDummy.AddRange(sigA); noDummy.Add((byte)sigB.Length); noDummy.AddRange(sigB);
            var ins = tx.Ins.ToList(); ins[0] = ins[0] with { ScriptSig = noDummy.ToArray() };
            T.False(Chain.VerifyMultisig2of2(tx with { Ins = ins }, 0, A.Pub, B.Pub, Amount), "missing OP_0 rejected");
        });

        T.Run("HOSTILE: a non-canonical DER signature (superfluous leading zero) is rejected", () =>
        {
            var tx = Chain.BuildCooperativeSettlement(EscrowTxid, 0, Amount, A.Pub, Fee);
            var sigA = Chain.SignMultisig(tx, 0, A.Pub, B.Pub, Amount, A.Priv);
            var sigB = Chain.SignMultisig(tx, 0, A.Pub, B.Pub, Amount, B.Priv);
            // append a stray byte inside the DER body (before the hashtype) → strict parser sees trailing data
            var der = sigA[..^1]; var hashtype = sigA[^1];
            var bad = der.Concat(new byte[] { 0x00 }).Append(hashtype).ToArray();
            var signed = Chain.ApplyMultisigScriptSig(tx, 0, bad, sigB);
            T.False(Chain.VerifyMultisig2of2(signed, 0, A.Pub, B.Pub, Amount), "malformed DER rejected");
        });

        T.Run("unilateral recovery: both co-sign; refunds each funder; conserves value; locktime binds", () =>
        {
            long stakeA = 100_000, stakeB = 100_000;
            var rec = Chain.BuildEscrowRecovery(EscrowTxid, 0, A.Pub, stakeA, B.Pub, stakeB, Fee, lockHeight: 800_000);
            var sigA = Chain.SignMultisig(rec, 0, A.Pub, B.Pub, Amount, A.Priv);
            var sigB = Chain.SignMultisig(rec, 0, A.Pub, B.Pub, Amount, B.Priv);
            var signed = Chain.ApplyMultisigScriptSig(rec, 0, sigA, sigB);
            T.True(Chain.VerifyMultisig2of2(signed, 0, A.Pub, B.Pub, Amount), "recovery is co-signed and valid");
            T.Eq(signed.Outs[0].Value + signed.Outs[1].Value, Amount - Fee, "stakes (minus fee) returned to funders");
            T.Eq(signed.LockTime, 800_000u, "future locktime set");
            T.Eq(signed.Ins[0].Sequence, 0xfffffffeu, "non-final sequence so the locktime binds");
        });
    }
}
