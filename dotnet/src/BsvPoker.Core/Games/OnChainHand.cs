namespace BsvPoker.Core.Games;

/// <summary>
/// The on-chain money lifecycle of a hand: players fund a shared 2-of-2 escrow from their wallets; the pot
/// is paid to the winner by a cooperative settlement both sign; and BEFORE any of that, both co-sign an
/// nLockTime recovery that refunds each player's stake if the other stalls — so a player can ALWAYS get
/// their money back (the always-recoverable rule). All real BSV transactions, broadcast by the node.
/// </summary>
public static class OnChainHand
{
    /// <summary>Fund the pot: spend wallet coins into a 2-of-2 escrow output for the two players.</summary>
    public static OnChainWallet.Spend FundEscrow(OnChainWallet wallet, byte[] pubA, byte[] pubB, long amount, long fee = 500)
        => wallet.BuildAction(Chain.MultisigLock2of2(pubA, pubB), amount, fee);

    /// <summary>Cooperative settlement: both players sign a tx paying the escrow to the winner.</summary>
    public static Chain.Tx Settle(string escrowTxid, uint vout, long amount, byte[] winnerPub, long fee,
        byte[] privA, byte[] pubA, byte[] privB, byte[] pubB)
    {
        var tx = Chain.BuildCooperativeSettlement(escrowTxid, vout, amount, winnerPub, fee);
        var sigA = Chain.SignMultisig(tx, 0, pubA, pubB, amount, privA);
        var sigB = Chain.SignMultisig(tx, 0, pubA, pubB, amount, privB);
        return Chain.ApplyMultisigScriptSig(tx, 0, sigA, sigB);
    }

    /// <summary>Cooperative settlement paying MULTIPLE shares (e.g. a hi-lo split): both players sign.</summary>
    public static Chain.Tx SettleMany(string escrowTxid, uint vout, long escrowValue,
        IReadOnlyList<(byte[] Pub, long Amount)> payouts, byte[] privA, byte[] pubA, byte[] privB, byte[] pubB)
    {
        var tx = Chain.BuildSplitSettlement(escrowTxid, vout, payouts, escrowValue); // enforce payouts ≤ escrow
        var sigA = Chain.SignMultisig(tx, 0, pubA, pubB, escrowValue, privA);
        var sigB = Chain.SignMultisig(tx, 0, pubA, pubB, escrowValue, privB);
        return Chain.ApplyMultisigScriptSig(tx, 0, sigA, sigB);
    }

    /// <summary>The pre-signed unilateral recovery: refunds each player's stake after lockHeight (both co-sign).</summary>
    public static Chain.Tx Recover(string escrowTxid, uint vout, byte[] pubA, long stakeA, byte[] pubB, long stakeB,
        long fee, uint lockHeight, byte[] privA, byte[] privB)
    {
        long amount = stakeA + stakeB;
        var tx = Chain.BuildEscrowRecovery(escrowTxid, vout, pubA, stakeA, pubB, stakeB, fee, lockHeight);
        var sigA = Chain.SignMultisig(tx, 0, pubA, pubB, amount, privA);
        var sigB = Chain.SignMultisig(tx, 0, pubA, pubB, amount, privB);
        return Chain.ApplyMultisigScriptSig(tx, 0, sigA, sigB);
    }
}
