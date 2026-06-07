namespace BsvPoker.Core.Games;

/// <summary>
/// REAL two-party pot funding: both players put their OWN coins into ONE shared 2-of-2 escrow transaction.
/// The funding tx has an input from each player and a single 2-of-2 output for the combined pot (plus each
/// player's change). Each player signs ONLY their own input (the FORKID sighash for an input does not depend
/// on the other input's scriptSig, so the two signatures are independent and can be produced separately and
/// exchanged). This is not one wallet funding the whole pot — it is a genuine two-sided contribution.
/// </summary>
public static class TwoPartyEscrow
{
    public sealed record Funding(Chain.Tx Tx, uint EscrowVout, long Pot);

    /// <summary>
    /// Build the UNSIGNED funding transaction: input 0 = player A's coin, input 1 = player B's coin; output 0
    /// = the 2-of-2 pot (stakeA+stakeB); then each player's change. The fee is split evenly. Each side later
    /// signs its own input. Throws if either side cannot cover its stake + share of the fee.
    /// </summary>
    public static Funding BuildUnsigned(
        OnChainWallet.Utxo aUtxo, byte[] aChangePub, long stakeA,
        OnChainWallet.Utxo bUtxo, byte[] bChangePub, long stakeB,
        byte[] pubA, byte[] pubB, long fee)
    {
        if (stakeA <= 0 || stakeB <= 0 || fee < 0) throw new ArgumentException("bad stake/fee");
        long feeA = fee / 2, feeB = fee - feeA;
        long changeA = aUtxo.Value - stakeA - feeA;
        long changeB = bUtxo.Value - stakeB - feeB;
        if (changeA < 0) throw new InvalidOperationException("player A cannot cover stake+fee");
        if (changeB < 0) throw new InvalidOperationException("player B cannot cover stake+fee");
        long pot = stakeA + stakeB;

        var ins = new List<Chain.TxIn>
        {
            new(aUtxo.Txid, aUtxo.Vout, Array.Empty<byte>(), 0xffffffff),
            new(bUtxo.Txid, bUtxo.Vout, Array.Empty<byte>(), 0xffffffff),
        };
        var outs = new List<Chain.TxOut> { new(pot, Chain.MultisigLock2of2(pubA, pubB)) }; // escrow at vout 0
        if (changeA > 0) outs.Add(new Chain.TxOut(changeA, Chain.P2pkhLockForPub(aChangePub)));
        if (changeB > 0) outs.Add(new Chain.TxOut(changeB, Chain.P2pkhLockForPub(bChangePub)));
        return new Funding(new Chain.Tx(2, ins, outs, 0), 0, pot);
    }

    /// <summary>Player A signs ONLY input 0 (their own coin).</summary>
    public static Chain.Tx SignA(Chain.Tx tx, byte[] aPriv, byte[] aPub, long aValue)
        => Chain.SignP2pkhInput(tx, 0, aPriv, aPub, aValue);

    /// <summary>Player B signs ONLY input 1 (their own coin), on top of A's signed tx.</summary>
    public static Chain.Tx SignB(Chain.Tx tx, byte[] bPriv, byte[] bPub, long bValue)
        => Chain.SignP2pkhInput(tx, 1, bPriv, bPub, bValue);

    /// <summary>Verify both players' inputs are validly signed and value is conserved (inputs = outputs + fee).</summary>
    public static bool Verify(Chain.Tx tx, byte[] aPub, long aValue, byte[] bPub, long bValue, long fee)
    {
        if (!Chain.VerifyP2pkhInput(tx, 0, aPub, aValue)) return false;
        if (!Chain.VerifyP2pkhInput(tx, 1, bPub, bValue)) return false;
        long outSum = tx.Outs.Sum(o => o.Value);
        return aValue + bValue == outSum + fee;
    }
}
