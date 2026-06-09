using System.Text;
using BsvPoker.Crypto;

namespace BsvPoker.Core;

/// <summary>
/// An identity written ON-CHAIN as a Bitcoin transaction output (an NFT). Per the hard rule, an identity is NOT
/// an identity until it exists on-chain: a local draft is nothing; only once this transaction is broadcast (and
/// confirmed) does the identity exist, and a restart that never broadcast it leaves no identity behind.
///
/// The output carries: the Base ID public key (which NEVER signs — it only roots derivation), a derived
/// ATTESTATION sub-key public key, the pseudonym and email, and a signature. The attestation sub-key signs the
/// canonical claim (so the Base ID key is never used to sign), and the output is spendable by that sub-key.
/// No OP_RETURN — the data rides as <c>&lt;push&gt; OP_DROP</c> fields ahead of a normal P2PK check.
/// </summary>
public static class OnChainIdentity
{
    public sealed record Claim(byte[] IdentityPub, byte[] AttestationPub, string Pseudonym, string Email, byte[] Signature);

    /// <summary>The exact bytes the attestation sub-key signs (and that a verifier recomputes).</summary>
    public static byte[] Canonical(byte[] identityPub, byte[] attestationPub, string pseudonym, string email)
        => Encoding.UTF8.GetBytes("bsvpoker-identity/v1|" +
            Convert.ToHexString(identityPub).ToLowerInvariant() + "|" +
            Convert.ToHexString(attestationPub).ToLowerInvariant() + "|" +
            (pseudonym ?? "") + "|" + (email ?? ""));

    /// <summary>Build the on-chain identity output. <paramref name="attestationPriv"/> is the derived sub-key (its
    /// public key is computed and used as both the attestation key and the spendable owner); the Base ID
    /// <paramref name="identityPub"/> is carried but never signs.</summary>
    public static byte[] BuildScript(byte[] identityPub, byte[] attestationPriv, string pseudonym, string email)
    {
        if (identityPub.Length != 33) throw new ArgumentException("identityPub must be 33-byte compressed");
        var attPub = Secp256k1.PublicKeyCompressed(attestationPriv);
        var sig = Secp256k1.Sign(attestationPriv, Canonical(identityPub, attPub, pseudonym, email));
        var fields = new[]
        {
            identityPub,
            attPub,
            Encoding.UTF8.GetBytes(pseudonym ?? ""),
            Encoding.UTF8.GetBytes(email ?? ""),
            sig,
        };
        // owner = the attestation sub-key (spendable); the Base ID never spends/signs.
        return TxTemplates.BuildOutput(TxKind.Identity, fields, attPub);
    }

    /// <summary>Read an identity claim from an output script, or null if it is not a (valid-shaped) identity output.</summary>
    public static Claim? TryRead(byte[] script)
    {
        var p = TxTemplates.Parse(script);
        if (p is not { Kind: TxKind.Identity } || p.Fields.Length != 5) return null;
        if (p.Fields[0].Length != 33 || p.Fields[1].Length != 33) return null;
        return new Claim(p.Fields[0], p.Fields[1], Encoding.UTF8.GetString(p.Fields[2]), Encoding.UTF8.GetString(p.Fields[3]), p.Fields[4]);
    }

    /// <summary>Scan a transaction for the first valid, signature-verified identity claim.</summary>
    public static Claim? TryReadTx(Chain.Tx tx)
    {
        foreach (var o in tx.Outs) { var c = TryRead(o.Script); if (c != null && Verify(c)) return c; }
        return null;
    }

    /// <summary>True iff the attestation signature over the canonical claim verifies under the attestation key.</summary>
    public static bool Verify(Claim c)
    {
        try { return Secp256k1.Verify(c.AttestationPub, Canonical(c.IdentityPub, c.AttestationPub, c.Pseudonym, c.Email), c.Signature); }
        catch { return false; }
    }
}
