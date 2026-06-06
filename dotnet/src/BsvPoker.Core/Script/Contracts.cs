using BsvPoker.Crypto;

namespace BsvPoker.Core.Script;

/// <summary>
/// Builders for on-chain smart contracts in Bitcoin Script. Every game role/bid/escrow is one of these
/// conditional contracts, enforced by <see cref="ScriptEngine"/>. No <c>OP_RETURN</c>; secp256k1 only.
/// </summary>
public sealed class ScriptBuilder
{
    private readonly List<byte> _b = new();
    public ScriptBuilder Op(byte op) { _b.Add(op); return this; }
    public ScriptBuilder Push(byte[] data)
    {
        if (data.Length < ScriptEngine.OP_PUSHDATA1) _b.Add((byte)data.Length);
        else if (data.Length <= 0xff) { _b.Add(ScriptEngine.OP_PUSHDATA1); _b.Add((byte)data.Length); }
        else if (data.Length <= 0xffff) { _b.Add(ScriptEngine.OP_PUSHDATA2); _b.Add((byte)data.Length); _b.Add((byte)(data.Length >> 8)); }
        else { _b.Add(ScriptEngine.OP_PUSHDATA4); _b.Add((byte)data.Length); _b.Add((byte)(data.Length >> 8)); _b.Add((byte)(data.Length >> 16)); _b.Add((byte)(data.Length >> 24)); }
        _b.AddRange(data); return this;
    }
    public ScriptBuilder Num(long n) => Push(ScriptEngine.Num(n));
    public byte[] Build() => _b.ToArray();
}

public static class Contracts
{
    /// <summary>P2PKH lock: OP_DUP OP_HASH160 &lt;hash160(pub)&gt; OP_EQUALVERIFY OP_CHECKSIG.</summary>
    public static byte[] P2pkh(byte[] pub33) => new ScriptBuilder()
        .Op(ScriptEngine.OP_DUP).Op(ScriptEngine.OP_HASH160).Push(Hashes.Hash160(pub33))
        .Op(ScriptEngine.OP_EQUALVERIFY).Op(ScriptEngine.OP_CHECKSIG).Build();

    public static byte[] P2pkhUnlock(byte[] sig, byte[] pub33) => new ScriptBuilder().Push(sig).Push(pub33).Build();

    /// <summary>Hash-lock (HTLC primitive): reveal a preimage of a known HASH160 to unlock. OP_HASH160 &lt;h&gt; OP_EQUAL.</summary>
    public static byte[] HashLock(byte[] hash160) => new ScriptBuilder().Op(ScriptEngine.OP_HASH160).Push(hash160).Op(ScriptEngine.OP_EQUAL).Build();
    public static byte[] HashLockUnlock(byte[] preimage) => new ScriptBuilder().Push(preimage).Build();

    /// <summary>Time-lock then key: &lt;locktime&gt; OP_CHECKLOCKTIMEVERIFY OP_DROP &lt;pub&gt; OP_CHECKSIG.</summary>
    public static byte[] TimeLockedKey(long lockTime, byte[] pub33) => new ScriptBuilder()
        .Num(lockTime).Op(ScriptEngine.OP_CHECKLOCKTIMEVERIFY).Op(ScriptEngine.OP_DROP)
        .Push(pub33).Op(ScriptEngine.OP_CHECKSIG).Build();

    /// <summary>
    /// Auction/escrow conditional: IF the bidder's preimage is revealed (won) the seller's key spends;
    /// ELSE after the timeout the bidder's key reclaims. A worked example of "every bid is a conditional
    /// smart contract":
    ///   OP_IF  OP_HASH160 &lt;h&gt; OP_EQUALVERIFY &lt;sellerPub&gt; OP_CHECKSIG
    ///   OP_ELSE &lt;timeout&gt; OP_CHECKLOCKTIMEVERIFY OP_DROP &lt;bidderPub&gt; OP_CHECKSIG OP_ENDIF
    /// </summary>
    public static byte[] AuctionEscrow(byte[] hash160, byte[] sellerPub, long timeout, byte[] bidderPub) => new ScriptBuilder()
        .Op(ScriptEngine.OP_IF)
            .Op(ScriptEngine.OP_HASH160).Push(hash160).Op(ScriptEngine.OP_EQUALVERIFY).Push(sellerPub).Op(ScriptEngine.OP_CHECKSIG)
        .Op(ScriptEngine.OP_ELSE)
            .Num(timeout).Op(ScriptEngine.OP_CHECKLOCKTIMEVERIFY).Op(ScriptEngine.OP_DROP).Push(bidderPub).Op(ScriptEngine.OP_CHECKSIG)
        .Op(ScriptEngine.OP_ENDIF).Build();

    /// <summary>Unlock the winning (IF) branch: &lt;sig&gt; &lt;preimage&gt; OP_1.</summary>
    public static byte[] AuctionWinUnlock(byte[] sellerSig, byte[] preimage) => new ScriptBuilder().Push(sellerSig).Push(preimage).Op(ScriptEngine.OP_1).Build();
    /// <summary>Unlock the refund (ELSE) branch after timeout: &lt;sig&gt; OP_0.</summary>
    public static byte[] AuctionRefundUnlock(byte[] bidderSig) => new ScriptBuilder().Push(bidderSig).Op(ScriptEngine.OP_0).Build();
}
