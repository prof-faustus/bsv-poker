using BsvPoker.Core.Script;
using BsvPoker.Crypto;

namespace BsvPoker.Tests;

/// <summary>
/// The Bitcoin Script smart-contract engine: number encoding, P2PKH, hash-locks, time-locks, and a
/// conditional auction/escrow — proving "every bid is a conditional smart contract" runs on-chain logic.
/// Positive AND hostile-negative cases; malformed scripts must return false, never throw.
/// </summary>
public static class ScriptTests
{
    // a checker bound to a fixed digest + a tx locktime (stands in for the spending tx context)
    private sealed class Checker : ScriptEngine.IChecker
    {
        private readonly byte[] _digest; private readonly long _txLockTime;
        public Checker(byte[] digest, long txLockTime) { _digest = digest; _txLockTime = txLockTime; }
        public bool CheckSig(byte[] sig, byte[] pubKey) { try { return Secp256k1.VerifyDigest(pubKey, _digest, sig); } catch { return false; } }
        public bool CheckLockTime(long lockTime) => _txLockTime >= lockTime;
        public bool CheckSequence(long sequence) => true;
    }

    public static void All()
    {
        Console.WriteLine("Bitcoin Script smart-contract engine:");
        var digest = Hashes.Sha256(System.Text.Encoding.ASCII.GetBytes("spend-context"));
        var seller = Secp256k1.GenerateKeyPair();
        var bidder = Secp256k1.GenerateKeyPair();

        T.Run("script number encoding round-trips (incl. negative and sign byte)", () =>
        {
            foreach (long n in new long[] { 0, 1, -1, 16, 127, 128, -128, 255, 256, 1000000, -1000000, 2147483647 })
                T.Eq((long)ScriptEngine.AsNum(ScriptEngine.Num(n)), n, $"num {n}");
        });

        T.Run("P2PKH contract: correct sig+pubkey unlocks; wrong key fails", () =>
        {
            var sig = Secp256k1.SignDigest(seller.Priv, digest);
            var spk = Contracts.P2pkh(seller.Pub);
            T.True(ScriptEngine.Verify(Contracts.P2pkhUnlock(sig, seller.Pub), spk, new Checker(digest, 0)), "valid P2PKH spend");
            T.False(ScriptEngine.Verify(Contracts.P2pkhUnlock(sig, bidder.Pub), spk, new Checker(digest, 0)), "wrong pubkey rejected");
        });

        T.Run("hash-lock: the correct preimage unlocks; a wrong one fails", () =>
        {
            var preimage = System.Text.Encoding.ASCII.GetBytes("the-secret-card-seed");
            var spk = Contracts.HashLock(Hashes.Hash160(preimage));
            T.True(ScriptEngine.Verify(Contracts.HashLockUnlock(preimage), spk, new ScriptEngine.NoChecker()), "preimage unlocks");
            T.False(ScriptEngine.Verify(Contracts.HashLockUnlock(new byte[] { 1, 2, 3 }), spk, new ScriptEngine.NoChecker()), "wrong preimage fails");
        });

        T.Run("time-lock: spend only once the tx locktime reaches the contract's height", () =>
        {
            var sig = Secp256k1.SignDigest(seller.Priv, digest);
            var spk = Contracts.TimeLockedKey(800000, seller.Pub);
            var unlock = Contracts.P2pkhUnlock(sig, seller.Pub).Length > 0 ? new ScriptBuilder().Push(sig).Build() : Array.Empty<byte>();
            T.True(ScriptEngine.Verify(unlock, spk, new Checker(digest, 800001)), "after locktime → spendable");
            T.False(ScriptEngine.Verify(unlock, spk, new Checker(digest, 799999)), "before locktime → rejected");
        });

        T.Run("auction/escrow: winner spends via preimage; else bidder refunds after timeout", () =>
        {
            var preimage = System.Text.Encoding.ASCII.GetBytes("winning-bid-secret");
            var spk = Contracts.AuctionEscrow(Hashes.Hash160(preimage), seller.Pub, 850000, bidder.Pub);
            var sellerSig = Secp256k1.SignDigest(seller.Priv, digest);
            var bidderSig = Secp256k1.SignDigest(bidder.Priv, digest);
            // winning branch (seller reveals preimage)
            T.True(ScriptEngine.Verify(Contracts.AuctionWinUnlock(sellerSig, preimage), spk, new Checker(digest, 0)), "winner claims with preimage");
            // wrong preimage on the win branch fails
            T.False(ScriptEngine.Verify(Contracts.AuctionWinUnlock(sellerSig, new byte[] { 9 }), spk, new Checker(digest, 0)), "bad preimage fails");
            // refund branch after timeout
            T.True(ScriptEngine.Verify(Contracts.AuctionRefundUnlock(bidderSig), spk, new Checker(digest, 850001)), "bidder refunds after timeout");
            // refund branch before timeout fails
            T.False(ScriptEngine.Verify(Contracts.AuctionRefundUnlock(bidderSig), spk, new Checker(digest, 849999)), "no refund before timeout");
        });

        T.Run("arithmetic contract: a bid above the reserve passes, at/below fails", () =>
        {
            // scriptPubKey: <reserve> OP_GREATERTHAN  (bid pushed by scriptSig)
            byte[] reserveGate(long reserve) => new ScriptBuilder().Num(reserve).Op(ScriptEngine.OP_GREATERTHAN).Build();
            T.True(ScriptEngine.Verify(new ScriptBuilder().Num(150).Build(), reserveGate(100), new ScriptEngine.NoChecker()), "150 > 100 passes");
            T.False(ScriptEngine.Verify(new ScriptBuilder().Num(100).Build(), reserveGate(100), new ScriptEngine.NoChecker()), "100 > 100 fails");
        });

        T.Run("HOSTILE: malformed scripts return false, never throw", () =>
        {
            T.False(ScriptEngine.Verify(new byte[] { 0x4b }, Array.Empty<byte>(), new ScriptEngine.NoChecker()), "truncated push");
            T.False(ScriptEngine.Verify(Array.Empty<byte>(), new byte[] { ScriptEngine.OP_ADD }, new ScriptEngine.NoChecker()), "underflow on OP_ADD");
            T.False(ScriptEngine.Verify(Array.Empty<byte>(), new byte[] { 0xff }, new ScriptEngine.NoChecker()), "unknown opcode");
            T.False(ScriptEngine.Verify(Array.Empty<byte>(), new byte[] { ScriptEngine.OP_IF }, new ScriptEngine.NoChecker()), "unbalanced IF");
            T.False(ScriptEngine.Verify(Array.Empty<byte>(), new byte[] { ScriptEngine.OP_RETURN }, new ScriptEngine.NoChecker()), "OP_RETURN aborts");
        });
    }
}
