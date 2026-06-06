namespace BsvPoker.Core.Games;

/// <summary>
/// Ties the games to the chain: every game action (create table, start hand, bet, card-NFT, deal, reveal,
/// settlement, role bid…) is a funded, signed, TYPED on-chain transaction — the wallet funds the typed
/// template output and pays the fee. The resulting tx is broadcast by the client's BsvNode. This is the
/// "everything on-chain, maximize transactions" model in one call.
/// </summary>
public static class OnChainTable
{
    /// <summary>Build a funded, signed transaction whose output is the typed template for this action.</summary>
    public static OnChainWallet.Spend Action(OnChainWallet wallet, byte[] ownerPub33, TxKind kind, IReadOnlyList<byte[]> fields, long outputValue = 1, long fee = 500)
        => wallet.BuildAction(TxTemplates.BuildOutput(kind, fields, ownerPub33), outputValue, fee);
}
