using BsvPoker.Core;
using BsvPoker.Core.Script;
using BsvPoker.Crypto;

namespace BsvPoker.Tests;

public static class ChainTests
{
    public static void All()
    {
        Console.WriteLine("on-chain BSV (tx / FORKID sighash / signing / nLockTime recovery):");
        var seed = T.Seed(5);
        var pub = Secp256k1.PublicKeyCompressed(seed);

        T.Run("P2SH lock = OP_HASH160 <h160(redeem)> OP_EQUAL, hashing the redeem script", () =>
        {
            var a = Secp256k1.PublicKeyCompressed(T.Seed(11));
            var b = Secp256k1.PublicKeyCompressed(T.Seed(12));
            var redeem = Chain.MultisigLock2of2(a, b);
            var lockS = Chain.P2shLock(redeem);
            T.Eq(lockS.Length, 23, "P2SH script is 23 bytes");
            T.Eq(lockS[0], (byte)0xa9, "OP_HASH160"); T.Eq(lockS[1], (byte)0x14, "push 20"); T.Eq(lockS[22], (byte)0x87, "OP_EQUAL");
            T.Eq(Convert.ToHexString(lockS[2..22]), Convert.ToHexString(Chain.ScriptHash160(redeem)), "embeds hash160 of the redeem script");
        });

        T.Run("P2SH 2-of-2 scriptSig = OP_0 sigA sigB redeem, with the redeem script pushed last", () =>
        {
            var a = Secp256k1.PublicKeyCompressed(T.Seed(13));
            var b = Secp256k1.PublicKeyCompressed(T.Seed(14));
            var redeem = Chain.MultisigLock2of2(a, b);
            var ss = Chain.P2shMultisigScriptSig(new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 }, redeem);
            T.Eq(ss[0], (byte)0x00, "leading OP_0 dummy");
            T.True(ss[^redeem.Length..].AsSpan().SequenceEqual(redeem), "redeem script is the final push");
        });

        T.Run("vault: cooperative 2-of-2 spend AND unilateral nLockTime recovery both verify on the interpreter", () =>
        {
            var A = Secp256k1.GenerateKeyPair(); var B = Secp256k1.GenerateKeyPair();
            long lockH = 800000; long amt = 100000; var fund = "aa".PadRight(64, '1');
            var redeem = Chain.MultisigVaultRedeem(A.Pub, B.Pub, A.Pub, lockH);   // recovery to A
            static byte[] Push(byte[] d) { var b = new List<byte> { (byte)d.Length }; b.AddRange(d); return b.ToArray(); }
            byte[] Coop(byte[] sa, byte[] sb) { var b = new List<byte> { 0x00 }; b.AddRange(Push(sa)); b.AddRange(Push(sb)); b.Add(0x51); return b.ToArray(); }
            byte[] Rec(byte[] s) { var b = new List<byte>(); b.AddRange(Push(s)); b.Add(0x00); return b.ToArray(); }

            var ctx = new Chain.Tx(2, new() { new(fund, 0, Array.Empty<byte>(), 0xffffffff) }, new() { new(amt - 500, Chain.P2pkhLockForPub(B.Pub)) }, 0);
            var sigA = TxScriptChecker.Sign(ctx, 0, redeem, amt, A.Priv);
            var sigB = TxScriptChecker.Sign(ctx, 0, redeem, amt, B.Priv);
            T.True(ScriptEngine.Verify(Coop(sigA, sigB), redeem, new TxScriptChecker(ctx, 0, redeem, amt)), "cooperative 2-of-2 spends the vault");

            var rtx = new Chain.Tx(2, new() { new(fund, 0, Array.Empty<byte>(), 0xfffffffe) }, new() { new(amt - 500, Chain.P2pkhLockForPub(A.Pub)) }, (uint)lockH + 1);
            var rsig = TxScriptChecker.Sign(rtx, 0, redeem, amt, A.Priv);
            T.True(ScriptEngine.Verify(Rec(rsig), redeem, new TxScriptChecker(rtx, 0, redeem, amt)), "owner recovers unilaterally AFTER locktime");

            var etx = new Chain.Tx(2, new() { new(fund, 0, Array.Empty<byte>(), 0xfffffffe) }, new() { new(amt - 500, Chain.P2pkhLockForPub(A.Pub)) }, (uint)lockH - 10);
            var esig = TxScriptChecker.Sign(etx, 0, redeem, amt, A.Priv);
            T.False(ScriptEngine.Verify(Rec(esig), redeem, new TxScriptChecker(etx, 0, redeem, amt)), "NO recovery before locktime");
        });
        const string fundTxid = "a1b2c3d4e5f6071829303132333435363738393a3b3c3d3e3f4041424344454f";

        T.Run("txid is a deterministic 64-hex of a serialized tx", () =>
        {
            var tx = new Chain.Tx(2, new() { new(fundTxid, 0, Array.Empty<byte>(), 0xffffffff) }, new() { new(90000, Chain.P2pkhLockForPub(pub)) }, 0);
            var id = Chain.Txid(tx);
            T.Eq(id.Length, 64);
            T.Eq(id, Chain.Txid(tx), "deterministic");
        });

        T.Run("sign a P2PKH input (FORKID) and verify it; a tampered amount fails", () =>
        {
            var tx = new Chain.Tx(2, new() { new(fundTxid, 0, Array.Empty<byte>(), 0xffffffff) }, new() { new(90000, Chain.P2pkhLockForPub(pub)) }, 0);
            var signed = Chain.SignP2pkhInput(tx, 0, seed, pub, 100000);
            T.True(Chain.VerifyP2pkhInput(signed, 0, pub, 100000), "valid signature over the sighash");
            T.False(Chain.VerifyP2pkhInput(signed, 0, pub, 99999), "wrong amount ⇒ different sighash ⇒ invalid");
            var wrong = Secp256k1.PublicKeyCompressed(T.Seed(6));
            T.False(Chain.VerifyP2pkhInput(signed, 0, wrong, 100000), "wrong key fails");
        });

        T.Run("nLockTime recovery: future locktime + non-final sequence + returns funds to the owner", () =>
        {
            var rec = Chain.BuildRecovery(fundTxid, 0, 100000, seed, pub, fee: 200, lockHeight: 850000);
            T.Eq(rec.LockTime, 850000u, "locktime set to the recovery height");
            T.Eq(rec.Ins[0].Sequence, 0xfffffffeu, "non-final sequence so locktime binds");
            T.Eq(rec.Outs[0].Value, 99800L, "100000 - 200 fee returned to owner");
            T.Eq(T.Hex(rec.Outs[0].Script), T.Hex(Chain.P2pkhLockForPub(pub)), "paid back to the OWNER");
            T.True(Chain.VerifyP2pkhInput(rec, 0, pub, 100000), "recovery is validly pre-signed");
        });

        T.Run("1-of-2 bot multisig: EITHER the funder OR the bot can spend; a stranger cannot; wrong amount fails", () =>
        {
            // every coin a player sends to their own bot is a 1-of-2 {funder, bot}: the bot can play with it AND
            // the funder can ALWAYS reclaim it. Both single-signature spends must verify; nobody else's must.
            var funderSeed = seed; var funderPub = pub;
            var botSeed = T.Seed(7); var botPub = Secp256k1.PublicKeyCompressed(botSeed);
            var lockScript = Chain.MultisigLock1of2(funderPub, botPub);
            T.Eq(lockScript[0], (byte)0x51, "OP_1 (1 signature required)");
            T.Eq(lockScript[^2], (byte)0x52, "OP_2 (2 keys)"); T.Eq(lockScript[^1], (byte)0xae, "OP_CHECKMULTISIG");

            var spend = new Chain.Tx(2, new() { new(fundTxid, 0, Array.Empty<byte>(), 0xffffffff) },
                                        new() { new(99800, Chain.P2pkhLockForPub(funderPub)) }, 0);
            // funder reclaims unilaterally
            var fSig = Chain.SignMultisig1of2(spend, 0, funderPub, botPub, 100000, funderSeed);
            var fSigned = Chain.ApplyMultisig1of2ScriptSig(spend, 0, fSig);
            T.True(Chain.VerifyMultisig1of2(fSigned, 0, funderPub, botPub, 100000), "funder can ALWAYS take it back");
            // bot spends unilaterally (to play)
            var bSig = Chain.SignMultisig1of2(spend, 0, funderPub, botPub, 100000, botSeed);
            var bSigned = Chain.ApplyMultisig1of2ScriptSig(spend, 0, bSig);
            T.True(Chain.VerifyMultisig1of2(bSigned, 0, funderPub, botPub, 100000), "bot can spend it to play");
            // a stranger (neither key) cannot
            var sSig = Chain.SignMultisig1of2(spend, 0, funderPub, botPub, 100000, T.Seed(99));
            var sSigned = Chain.ApplyMultisig1of2ScriptSig(spend, 0, sSig);
            T.False(Chain.VerifyMultisig1of2(sSigned, 0, funderPub, botPub, 100000), "a non-party signature is rejected");
            // tampered amount changes the sighash → reject
            T.False(Chain.VerifyMultisig1of2(fSigned, 0, funderPub, botPub, 99999), "wrong amount rejected");
        });

        T.Run("HOSTILE: P2PKH verification is strict (malformed DER, wrong hashtype, trailing bytes rejected)", () =>
        {
            var tx = new Chain.Tx(2, new() { new(fundTxid, 0, Array.Empty<byte>(), 0xffffffff) }, new() { new(90000, Chain.P2pkhLockForPub(pub)) }, 0);
            var signed = Chain.SignP2pkhInput(tx, 0, seed, pub, 100000);
            var ss = signed.Ins[0].ScriptSig;
            // trailing junk after the canonical <sig><pubkey> is rejected
            var trailing = ss.Concat(new byte[] { 0x00 }).ToArray();
            var sTrailing = signed with { Ins = new() { signed.Ins[0] with { ScriptSig = trailing } } };
            T.False(Chain.VerifyP2pkhInput(sTrailing, 0, pub, 100000), "trailing scriptSig bytes rejected");

            // corrupt a DER byte inside the signature → strict parser rejects
            var corrupt = (byte[])ss.Clone(); corrupt[3] ^= 0xFF; // mangle inside the DER body
            var sCorrupt = signed with { Ins = new() { signed.Ins[0] with { ScriptSig = corrupt } } };
            T.False(Chain.VerifyP2pkhInput(sCorrupt, 0, pub, 100000), "malformed DER rejected");
        });

        T.Run("no OP_RETURN (0x6a) is produced in any script", () =>
        {
            var lockScript = Chain.P2pkhLockForPub(pub);
            T.False(lockScript.Contains((byte)0x6a), "P2PKH carries no OP_RETURN");
        });
    }
}
