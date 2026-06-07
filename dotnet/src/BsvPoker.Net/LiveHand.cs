using BsvPoker.Core;
using BsvPoker.Core.Games;

namespace BsvPoker.Net;

/// <summary>
/// A complete LIVE two-party on-chain hand between two real peers, exchanged entirely as messages (each a
/// Bitcoin transaction over the channel): (1) both fund a real 2-of-2 pot — each contributes and signs ONLY
/// their own input (<see cref="TwoPartyEscrow"/>); (2) the dealerless mental-poker deal runs with verifiable
/// shuffle/remask proofs and per-card privacy (<see cref="LiveDeal"/>); (3) the winner is decided by the real
/// evaluator and the pot is paid by a co-signed settlement (each signs, signatures exchanged). No shared RNG,
/// no single wallet funding both sides, no off-chain channel.
/// </summary>
public static class LiveHand
{
    public sealed record Result(Chain.Tx EscrowTx, long Pot, IReadOnlyList<Card> MyHoles, IReadOnlyList<Card> OppHoles,
        IReadOnlyList<Card> Board, bool ProofsVerified, int WinnerSeat, Chain.Tx Settlement);

    public static Result RunInitiator(LiveDeal.IDealChannel ch, OnChainWallet.Utxo myUtxo, byte[] myChangePub,
        (byte[] Priv, byte[] Pub) me, long stake, long fee = 2000, long settleFee = 1000)
        => Run(ch, true, myUtxo, myChangePub, me, stake, fee, settleFee);

    public static Result RunResponder(LiveDeal.IDealChannel ch, OnChainWallet.Utxo myUtxo, byte[] myChangePub,
        (byte[] Priv, byte[] Pub) me, long stake, long fee = 2000, long settleFee = 1000)
        => Run(ch, false, myUtxo, myChangePub, me, stake, fee, settleFee);

    private static Result Run(LiveDeal.IDealChannel ch, bool initiator, OnChainWallet.Utxo myUtxo, byte[] myChangePub,
        (byte[] Priv, byte[] Pub) me, long stake, long fee, long settleFee)
    {
        // ---- 1) two-party escrow funding ----
        // `me` is the SEAT key: it controls my funding UTXO AND is my 2-of-2 escrow member. Both are exchanged.
        ch.Send("E:" + myUtxo.Txid + "," + myUtxo.Vout + "," + myUtxo.Value + "," + Convert.ToHexString(myChangePub) + "," + Convert.ToHexString(me.Pub) + "," + stake);
        var opp = ParseEscrowInfo(Expect(ch.Receive(), "E:"));
        var peerPub = opp.SeatPub;

        // both build the SAME unsigned escrow with the initiator as input 0
        var aUtxo = initiator ? myUtxo : opp.Utxo;
        var aChange = initiator ? myChangePub : opp.ChangePub;
        var aPub = initiator ? me.Pub : peerPub;
        var bUtxo = initiator ? opp.Utxo : myUtxo;
        var bChange = initiator ? opp.ChangePub : myChangePub;
        var bPub = initiator ? peerPub : me.Pub;
        var ef = TwoPartyEscrow.BuildUnsigned(aUtxo, aChange, stake, bUtxo, bChange, stake, aPub, bPub, fee);

        // each signs ONLY its own input, then exchange the two scriptSigs and assemble
        int myIdx = initiator ? 0 : 1;
        var signedSelf = initiator
            ? TwoPartyEscrow.SignA(ef.Tx, me.Priv, me.Pub, myUtxo.Value)
            : TwoPartyEscrow.SignB(ef.Tx, me.Priv, me.Pub, myUtxo.Value);
        var myScriptSig = signedSelf.Ins[myIdx].ScriptSig;
        ch.Send("ES:" + Convert.ToHexString(myScriptSig));
        var oppScriptSig = Convert.FromHexString(Expect(ch.Receive(), "ES:"));
        var ins = ef.Tx.Ins.ToList();
        ins[myIdx] = ins[myIdx] with { ScriptSig = myScriptSig };
        ins[1 - myIdx] = ins[1 - myIdx] with { ScriptSig = oppScriptSig };
        var escrow = ef.Tx with { Ins = ins };
        var escrowTxid = Chain.Txid(escrow);

        // ---- 2) the dealerless deal (with shuffle proofs + per-card privacy) over the same channel ----
        var deal = initiator ? LiveDeal.RunInitiator(ch) : LiveDeal.RunResponder(ch);

        // ---- 3) showdown winner (both seats known after the deal's showdown reveal) + co-signed settlement ----
        var seat0Holes = initiator ? deal.MyHoles : deal.OppHoles;   // seat 0 = initiator, by convention
        var seat1Holes = initiator ? deal.OppHoles : deal.MyHoles;
        var def = PokerGames.Of(PokerGame.TexasHoldem);
        var payouts = Showdown.Settle(def, new[] { seat0Holes, seat1Holes }, deal.Board, ef.Pot);
        long p0 = payouts.GetValueOrDefault(0), p1 = payouts.GetValueOrDefault(1);
        int winnerSeat = p0 >= p1 ? 0 : 1;
        var winnerPub = winnerSeat == 0 ? aPub : bPub;             // aPub = initiator, bPub = responder

        var settlement = Chain.BuildCooperativeSettlement(escrowTxid, ef.EscrowVout, ef.Pot, winnerPub, settleFee);
        var mySig = Chain.SignMultisig(settlement, 0, aPub, bPub, ef.Pot, me.Priv);
        ch.Send("SG:" + Convert.ToHexString(mySig));
        var oppSig = Convert.FromHexString(Expect(ch.Receive(), "SG:"));
        var sigA = initiator ? mySig : oppSig;                     // signatures in pubkey order (A=initiator)
        var sigB = initiator ? oppSig : mySig;
        var finalSettle = Chain.ApplyMultisigScriptSig(settlement, 0, sigA, sigB);

        return new Result(escrow, ef.Pot, deal.MyHoles, deal.OppHoles, deal.Board, deal.ProofsVerified, winnerSeat, finalSettle);
    }

    private readonly record struct EscrowInfo(OnChainWallet.Utxo Utxo, byte[] ChangePub, byte[] SeatPub);
    private static EscrowInfo ParseEscrowInfo(string s)
    {
        var p = s.Split(',');
        var utxo = new OnChainWallet.Utxo(p[0], uint.Parse(p[1]), long.Parse(p[2]), 0, 0);
        return new EscrowInfo(utxo, Convert.FromHexString(p[3]), Convert.FromHexString(p[4]));
    }
    private static string Expect(string msg, string tag)
    {
        if (!msg.StartsWith(tag, StringComparison.Ordinal)) throw new InvalidOperationException($"protocol: expected {tag}");
        return msg[tag.Length..];
    }
}
