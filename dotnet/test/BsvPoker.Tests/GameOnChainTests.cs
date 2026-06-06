using System.Text;
using BsvPoker.Core;
using BsvPoker.Core.Games;
using BsvPoker.Crypto;

namespace BsvPoker.Tests;

/// <summary>Every game action is a funded, signed, TYPED on-chain transaction: the wallet funds the typed
/// template output + change, signs the inputs, and the output parses back to the right kind and fields.</summary>
public static class GameOnChainTests
{
    public static void All()
    {
        Console.WriteLine("on-chain gameplay (every action a funded typed transaction):");
        var seed = WalletKeys.NewSeed();
        var ownerPub = WalletKeys.Account(seed, 0, 0).Pub;

        OnChainWallet Funded()
        {
            var w = new OnChainWallet(seed);
            w.Add(new OnChainWallet.Utxo("aa".PadRight(64, '1'), 0, 50000, 0, 0));
            w.Add(new OnChainWallet.Utxo("bb".PadRight(64, '2'), 1, 50000, 0, 1));
            return w;
        }

        void CheckAction(TxKind kind)
        {
            var w = Funded();
            var schema = TxTemplates.Of(kind).Fields;
            var fields = schema.Select((name, i) => Encoding.ASCII.GetBytes($"{name}:{kind}:{i}")).ToArray();
            var s = OnChainTable.Action(w, ownerPub, kind, fields, outputValue: 1, fee: 500);
            T.True(w.VerifySpend(s), $"{kind}: funded tx signs + conserves value");
            T.Eq(s.Tx.Outs[0].Value, 1L, $"{kind}: 1-sat typed output");
            var parsed = TxTemplates.Parse(s.Tx.Outs[0].Script);
            T.True(parsed != null && parsed.Kind == kind, $"{kind}: typed output recognized on-chain");
            for (int i = 0; i < schema.Length; i++) T.Eq(T.Hex(parsed!.Fields[i]), T.Hex(fields[i]), $"{kind}: field {schema[i]} on-chain");
            T.True(s.Change > 0 && s.Tx.Outs.Count == 2, $"{kind}: change returned");
        }

        T.Run("table genesis is a funded typed transaction", () => CheckAction(TxKind.TableGenesis));
        T.Run("starting a hand is a funded typed transaction", () => CheckAction(TxKind.HandStart));
        T.Run("a bet is a funded typed transaction", () => CheckAction(TxKind.Bet));
        T.Run("a card NFT is a funded typed transaction", () => CheckAction(TxKind.CardNft));
        T.Run("a deal is a funded typed transaction", () => CheckAction(TxKind.Deal));
        T.Run("a settlement is a funded typed transaction", () => CheckAction(TxKind.Settlement));
        T.Run("a role-claim (auctioned role) is a funded typed transaction", () => CheckAction(TxKind.RoleClaim));
    }
}
