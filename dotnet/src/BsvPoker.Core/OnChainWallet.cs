using BsvPoker.Crypto;

namespace BsvPoker.Core;

/// <summary>
/// The real-BSV spending wallet: it tracks UTXOs owned by keys derived from the master seed, selects coins
/// to fund a payment, builds the transaction, signs every input (secp256k1, low-S, FORKID), returns change,
/// and the result is consensus-verifiable. UTXOs arrive over the P2P/SPV layer (a payer hands them over
/// with merkle proofs); broadcasting is done by the client's own <c>BsvNode</c>. Same model on all networks.
/// </summary>
public sealed class OnChainWallet
{
    /// <summary>An unspent output owned by this wallet, with the (chain,index) of the key that locks it.</summary>
    public sealed record Utxo(string Txid, uint Vout, long Value, uint KeyChain, uint KeyIndex);

    private readonly byte[] _seed;
    private readonly List<Utxo> _utxos = new();
    private uint _nextChange;

    public OnChainWallet(byte[] seed32) { _seed = seed32; }

    public void Add(Utxo u) => _utxos.Add(u);
    public long Balance => _utxos.Sum(u => u.Value);
    public IReadOnlyList<Utxo> Coins => _utxos;

    public sealed record Spend(Chain.Tx Tx, IReadOnlyList<Utxo> Inputs, long Fee, long Change);

    /// <summary>
    /// Build and sign a payment of <paramref name="amount"/> to <paramref name="recipientPub33"/> with the
    /// given <paramref name="fee"/>. Selects coins largest-first, returns change to a fresh change key.
    /// Throws on insufficient funds. The returned tx is fully signed and verifiable.
    /// </summary>
    public Spend BuildPayment(byte[] recipientPub33, long amount, long fee)
    {
        if (amount <= 0 || fee < 0) throw new ArgumentException("bad amount/fee");
        long need = amount + fee;
        var chosen = new List<Utxo>(); long sum = 0;
        foreach (var u in _utxos.OrderByDescending(u => u.Value))
        {
            chosen.Add(u); sum += u.Value;
            if (sum >= need) break;
        }
        if (sum < need) throw new InvalidOperationException($"insufficient funds: have {sum}, need {need}");

        long change = sum - need;
        var ins = chosen.Select(u => new Chain.TxIn(u.Txid, u.Vout, Array.Empty<byte>(), 0xffffffff)).ToList();
        var outs = new List<Chain.TxOut> { new(amount, Chain.P2pkhLockForPub(recipientPub33)) };
        byte[]? changePub = null;
        if (change > 0)
        {
            var ck = WalletKeys.Account(_seed, 1, _nextChange++);
            changePub = ck.Pub;
            outs.Add(new Chain.TxOut(change, Chain.P2pkhLockForPub(changePub)));
        }
        var tx = new Chain.Tx(2, ins, outs, 0);
        for (int i = 0; i < chosen.Count; i++)
        {
            var k = WalletKeys.Account(_seed, chosen[i].KeyChain, chosen[i].KeyIndex);
            tx = Chain.SignP2pkhInput(tx, i, k.Priv, k.Pub, chosen[i].Value);
        }
        return new Spend(tx, chosen, fee, change);
    }

    /// <summary>
    /// Fund an arbitrary OUTPUT SCRIPT (e.g. a typed transaction template or a Script contract) with the
    /// given output value, paying the fee and returning change. This is how every on-chain game action
    /// (table genesis, deal, bet, card-NFT, escrow, settlement) is funded: a typed/contract output + change,
    /// fully signed. Throws on insufficient funds.
    /// </summary>
    public Spend BuildAction(byte[] outputScript, long outputValue, long fee)
    {
        if (outputValue < 0 || fee < 0) throw new ArgumentException("bad value/fee");
        long need = outputValue + fee;
        var chosen = new List<Utxo>(); long sum = 0;
        foreach (var u in _utxos.OrderByDescending(u => u.Value)) { chosen.Add(u); sum += u.Value; if (sum >= need) break; }
        if (sum < need) throw new InvalidOperationException($"insufficient funds: have {sum}, need {need}");
        long change = sum - need;
        var ins = chosen.Select(u => new Chain.TxIn(u.Txid, u.Vout, Array.Empty<byte>(), 0xffffffff)).ToList();
        var outs = new List<Chain.TxOut> { new(outputValue, outputScript) };
        if (change > 0) outs.Add(new Chain.TxOut(change, Chain.P2pkhLockForPub(WalletKeys.Account(_seed, 1, _nextChange++).Pub)));
        var tx = new Chain.Tx(2, ins, outs, 0);
        for (int i = 0; i < chosen.Count; i++)
        {
            var k = WalletKeys.Account(_seed, chosen[i].KeyChain, chosen[i].KeyIndex);
            tx = Chain.SignP2pkhInput(tx, i, k.Priv, k.Pub, chosen[i].Value);
        }
        return new Spend(tx, chosen, fee, change);
    }

    /// <summary>Verify every input of a spend this wallet built (consensus check); also confirms value conservation.</summary>
    public bool VerifySpend(Spend s)
    {
        long inSum = 0;
        for (int i = 0; i < s.Inputs.Count; i++)
        {
            var u = s.Inputs[i];
            var pub = WalletKeys.Account(_seed, u.KeyChain, u.KeyIndex).Pub;
            if (!Chain.VerifyP2pkhInput(s.Tx, i, pub, u.Value)) return false;
            inSum += u.Value;
        }
        long outSum = s.Tx.Outs.Sum(o => o.Value);
        return inSum == outSum + s.Fee;   // value conserved (inputs = outputs + fee)
    }
}
