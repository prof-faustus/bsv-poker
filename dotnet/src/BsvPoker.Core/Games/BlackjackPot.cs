using BsvPoker.Crypto;

namespace BsvPoker.Core.Games;

/// <summary>
/// The on-chain POT for multiplayer group Blackjack. Every player funds the pot together into ONE n-of-n
/// locked output (<see cref="Chain.MultisigLockNofN"/>) — for a 3-player game that is a 3-of-3 output — so no
/// subset of players can move the money: settlement requires EVERY player's signature, exactly as the
/// principal specified. After a hand the pot is distributed by each player's result (win/lose/push/blackjack),
/// and the REMAINING bank is split among the remaining players. The total is conserved to the satoshi; a
/// pre-agreed n-of-n nLockTime recovery (<see cref="Chain.BuildNofNRecovery"/>) guarantees no stake is ever
/// stranded if a player griefs by refusing to co-sign.
/// </summary>
public static class BlackjackPot
{
    /// <summary>The FINAL per-seat amount each player receives from a completed hand: their bet ± result, plus
    /// an equal share of the remaining dealer bank ("the remainder of the pot is distributed between the
    /// remaining players"). The returned amounts sum EXACTLY to the pot (dealerBank + all bets).</summary>
    public static long[] Settle(GroupBlackjack hand, long dealerBank)
    {
        var (payouts, remaining) = hand.Distribute(dealerBank);
        int n = hand.Players;
        var final = new long[n];
        long share = remaining / n, extra = remaining - share * n;   // remainder of the integer division to seat 0
        for (int i = 0; i < n; i++) final[i] = payouts[i] + share + (i == 0 ? extra : 0);
        return final;
    }

    /// <summary>Build the (unsigned) settlement that spends the n-of-n pot, paying each player their final amount
    /// to their own address. Requires sum(finalAmounts) + fee == potValue (value conserved). Every player then
    /// signs it (<see cref="Chain.SignMultisigN"/>) and the sigs are assembled in pubkey order.</summary>
    public static Chain.Tx BuildSettlement(string potTxid, uint vout, long potValue, IReadOnlyList<byte[]> playerPubs, IReadOnlyList<long> finalAmounts, long fee)
    {
        if (playerPubs.Count != finalAmounts.Count) throw new ArgumentException("a final amount per player");
        if (fee < 0) throw new ArgumentException("negative fee");
        long paid = finalAmounts.Sum();
        if (paid + fee != potValue) throw new ArgumentException($"settlement does not conserve the pot: pays {paid} + fee {fee} != pot {potValue}");
        var outs = new List<Chain.TxOut>();
        for (int i = 0; i < playerPubs.Count; i++)
            if (finalAmounts[i] > 0) outs.Add(new Chain.TxOut(finalAmounts[i], Chain.P2pkhLockForPub(playerPubs[i])));
        if (outs.Count == 0) throw new ArgumentException("no positive payouts");
        var ins = new List<Chain.TxIn> { new(potTxid, vout, Array.Empty<byte>(), 0xffffffff) };
        return new Chain.Tx(2, ins, outs, 0);
    }

    /// <summary>Assemble a fully co-signed settlement: every player signs the same unsigned tx; sigs go in
    /// pubkey order. The result verifies under <see cref="Chain.VerifyMultisigNofN"/> only when ALL signed.</summary>
    public static Chain.Tx CoSign(Chain.Tx unsigned, IReadOnlyList<byte[]> playerPubs, long potValue, IReadOnlyList<byte[]> playerSeedsInPubOrder)
    {
        var sigs = new List<byte[]>();
        for (int i = 0; i < playerPubs.Count; i++) sigs.Add(Chain.SignMultisigN(unsigned, 0, playerPubs, potValue, playerSeedsInPubOrder[i]));
        return Chain.ApplyMultisigScriptSigN(unsigned, 0, sigs);
    }

    // ===================== FUNDING: every player contributes into ONE n-of-n pot =====================
    // The multi-party funding tx the principal specified: each of the N players puts their OWN coin in (one input
    // each, signed only by that player) and the combined pot is a single n-of-n output (vout 0). No one funds the
    // whole pot; no subset can later move it. Generalises the two-party escrow to N players.

    /// <summary>One player's contribution to the pot: the coin they spend, where their change goes, how much they
    /// stake, and their funding pubkey (the key that signs their input).</summary>
    public sealed record Contribution(OnChainWallet.Utxo Utxo, byte[] ChangePub, long Stake, byte[] OwnerPub);

    public sealed record Funding(Chain.Tx Tx, uint PotVout, long Pot);

    /// <summary>Build the UNSIGNED funding tx: one input per player, a single n-of-n pot output (vout 0) holding
    /// the summed stakes, then each player's change. Fee split evenly (remainder on player 0). Each player then
    /// signs ONLY their own input via <see cref="SignInput"/>. Throws if any player cannot cover stake + fee.</summary>
    public static Funding BuildFunding(IReadOnlyList<Contribution> players, IReadOnlyList<byte[]> potPubs, long fee)
    {
        if (players.Count < 2) throw new ArgumentException("group Blackjack needs >= 2 players");
        if (fee < 0) throw new ArgumentException("negative fee");
        long feeEach = fee / players.Count, feeRem = fee - feeEach * players.Count;
        long pot = players.Sum(p => p.Stake);
        var ins = new List<Chain.TxIn>();
        var change = new List<Chain.TxOut>();
        for (int i = 0; i < players.Count; i++)
        {
            var p = players[i];
            if (p.Stake <= 0) throw new ArgumentException("each stake must be positive");
            long myFee = feeEach + (i == 0 ? feeRem : 0);
            long ch = p.Utxo.Value - p.Stake - myFee;
            if (ch < 0) throw new InvalidOperationException($"player {i} cannot cover stake + fee");
            ins.Add(new Chain.TxIn(p.Utxo.Txid, p.Utxo.Vout, Array.Empty<byte>(), 0xffffffff));
            if (ch > 0) change.Add(new Chain.TxOut(ch, Chain.P2pkhLockForPub(p.ChangePub)));
        }
        var outs = new List<Chain.TxOut> { new(pot, Chain.MultisigLockNofN(potPubs)) };   // the n-of-n pot at vout 0
        outs.AddRange(change);
        return new Funding(new Chain.Tx(2, ins, outs, 0), 0, pot);
    }

    /// <summary>Player <paramref name="index"/> signs ONLY their own funding input (their P2PKH coin).</summary>
    public static Chain.Tx SignInput(Chain.Tx tx, int index, byte[] priv, byte[] pub, long value) => Chain.SignP2pkhInput(tx, index, priv, pub, value);

    /// <summary>Verify EVERY player's funding input is validly signed and value is conserved (inputs = outputs + fee).</summary>
    public static bool VerifyFunding(Chain.Tx tx, IReadOnlyList<Contribution> players, long fee)
    {
        for (int i = 0; i < players.Count; i++) if (!Chain.VerifyP2pkhInput(tx, i, players[i].OwnerPub, players[i].Utxo.Value)) return false;
        long outSum = tx.Outs.Sum(o => o.Value);
        return players.Sum(p => p.Utxo.Value) == outSum + fee;
    }

    // ===================== PER-PLAYER ESCROW + MULTI-INPUT SETTLEMENT =====================
    // The live networked table does not need a funding handshake: each player ESCROWS their own stake into the
    // SAME n-of-n pot script with an ordinary single-party tx (one pot coin each), then announces that coin. The
    // session-end settlement spends EVERY pot coin in one tx (each input is the n-of-n, so each needs all sigs)
    // paying out the final standings — real tokens, no subset can move them, conserved to the satoshi.

    /// <summary>A funded pot coin: an escrowed n-of-n output one player created.</summary>
    public sealed record PotIn(string Txid, uint Vout, long Value);

    /// <summary>The n-of-n pot lock script every player escrows their stake into (locked to all players' pubkeys,
    /// in the given order — the same order used to sign/settle).</summary>
    public static byte[] PotScript(IReadOnlyList<byte[]> playerPubs) => Chain.MultisigLockNofN(playerPubs);

    /// <summary>Build the UNSIGNED session settlement spending ALL escrowed pot coins, paying each player their
    /// final standing to their own address. Requires sum(finalAmounts) + fee == sum(pot coin values).</summary>
    public static Chain.Tx BuildSessionSettlement(IReadOnlyList<PotIn> pot, IReadOnlyList<byte[]> playerPubs, IReadOnlyList<long> finalAmounts, long fee)
    {
        if (playerPubs.Count != finalAmounts.Count) throw new ArgumentException("a final amount per player");
        if (fee < 0) throw new ArgumentException("negative fee");
        long potValue = pot.Sum(p => p.Value), paid = finalAmounts.Sum();
        if (paid + fee != potValue) throw new ArgumentException($"settlement does not conserve the pot: pays {paid} + fee {fee} != pot {potValue}");
        var outs = new List<Chain.TxOut>();
        for (int i = 0; i < playerPubs.Count; i++)
            if (finalAmounts[i] > 0) outs.Add(new Chain.TxOut(finalAmounts[i], Chain.P2pkhLockForPub(playerPubs[i])));
        if (outs.Count == 0) throw new ArgumentException("no positive payouts");
        var ins = pot.Select(p => new Chain.TxIn(p.Txid, p.Vout, Array.Empty<byte>(), 0xffffffff)).ToList();
        return new Chain.Tx(2, ins, outs, 0);
    }

    /// <summary>One player's signature for ONE settlement input (the n-of-n pot coin at that input index).</summary>
    public static byte[] SignSessionInput(Chain.Tx unsigned, int inputIndex, IReadOnlyList<byte[]> playerPubs, long inputValue, byte[] seed)
        => Chain.SignMultisigN(unsigned, inputIndex, playerPubs, inputValue, seed);

    /// <summary>Assemble the fully co-signed settlement: for each input, the per-player sigs IN PUBKEY ORDER.</summary>
    public static Chain.Tx ApplySessionSigs(Chain.Tx unsigned, IReadOnlyList<IReadOnlyList<byte[]>> sigsPerInputInPubOrder)
    {
        var tx = unsigned;
        for (int j = 0; j < sigsPerInputInPubOrder.Count; j++) tx = Chain.ApplyMultisigScriptSigN(tx, j, sigsPerInputInPubOrder[j]);
        return tx;
    }
}
