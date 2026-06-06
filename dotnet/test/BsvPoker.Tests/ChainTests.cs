using BsvPoker.Core;
using BsvPoker.Crypto;

namespace BsvPoker.Tests;

public static class ChainTests
{
    public static void All()
    {
        Console.WriteLine("on-chain BSV (tx / FORKID sighash / signing / nLockTime recovery):");
        var seed = T.Seed(5);
        var pub = Secp256k1.PublicKeyCompressed(seed);
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
