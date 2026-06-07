using System.Text;

namespace BsvPoker.Core;

/// <summary>
/// Automatic peer discovery, with no manual key exchange: a player announces itself by broadcasting an
/// Announce transaction carrying its public key and IP endpoint. Other players' nodes receive that
/// transaction (relayed on the Bitcoin network and/or pushed IP-to-IP) and learn the peer's key + address
/// automatically — so messaging/playing never requires anyone to paste a key or an IP by hand.
/// </summary>
public static class OnChainAnnounce
{
    public sealed record Peer(byte[] Pub, string Endpoint);

    /// <summary>Build the Announce output script (fields: playerPub, endpoint), owned by the announcing player.</summary>
    public static byte[] BuildScript(byte[] myPub33, string endpoint)
        => TxTemplates.BuildOutput(TxKind.Announce, new[] { myPub33, Encoding.UTF8.GetBytes(endpoint) }, myPub33);

    /// <summary>Read an Announce output, or null if the script is not an announcement.</summary>
    public static Peer? TryRead(byte[] script)
    {
        var p = TxTemplates.Parse(script);
        if (p is not { Kind: TxKind.Announce } || p.Fields.Length != 2 || p.Fields[0].Length != 33) return null;
        return new Peer(p.Fields[0], Encoding.UTF8.GetString(p.Fields[1]));
    }

    /// <summary>Scan a transaction for an announcement.</summary>
    public static Peer? TryReadTx(Chain.Tx tx)
    {
        foreach (var o in tx.Outs) { var r = TryRead(o.Script); if (r != null) return r; }
        return null;
    }
}
