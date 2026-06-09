using BsvPoker.Core;
using BsvPoker.Crypto;

namespace BsvPoker.Tests;

/// <summary>
/// Identity is an ON-CHAIN transaction (an NFT): it is built into a typed output (no OP_RETURN), the Base ID
/// key NEVER signs (a derived attestation sub-key does), and the signature must verify or it is not an identity.
/// </summary>
public static class OnChainIdentityTests
{
    public static void All()
    {
        Console.WriteLine("on-chain identity (NFT tx, attestation-signed, Base ID never signs):");

        var seed = T.Seed(71);
        var identityPriv = Type42.UniqueKey(seed, "bsvpoker/identity");
        var identityPub = Secp256k1.PublicKeyCompressed(identityPriv);
        var attPriv = Type42.UniqueKey(seed, "bsvpoker/identity/attestation");
        var attPub = Secp256k1.PublicKeyCompressed(attPriv);

        T.Run("build → parse → verify; carries Base ID + attestation; Base ID is NOT the signer", () =>
        {
            var script = OnChainIdentity.BuildScript(identityPub, attPriv, "CsTominaga", "craig@rcjbr.org");
            var c = OnChainIdentity.TryRead(script);
            T.True(c != null, "parses as an identity output");
            T.Eq(T.Hex(c!.IdentityPub), T.Hex(identityPub), "Base ID pub carried");
            T.Eq(T.Hex(c.AttestationPub), T.Hex(attPub), "attestation pub carried");
            T.Eq(c.Pseudonym, "CsTominaga"); T.Eq(c.Email, "craig@rcjbr.org");
            T.True(OnChainIdentity.Verify(c), "attestation signature verifies");
            T.True(T.Hex(c.AttestationPub) != T.Hex(c.IdentityPub), "attestation key is a DERIVED sub-key, not the Base ID");
        });

        T.Run("found + verified inside a transaction", () =>
        {
            var script = OnChainIdentity.BuildScript(identityPub, attPriv, "Bob", "bob@example.com");
            var tx = new Chain.Tx(2, new() { new(new string('a', 64), 0, Array.Empty<byte>(), 0xffffffff) },
                                     new() { new(1, script) }, 0);
            var c = OnChainIdentity.TryReadTx(tx);
            T.True(c != null && c.Pseudonym == "Bob", "identity recovered from the tx");
        });

        T.Run("HOSTILE: tampered pseudonym fails verification", () =>
        {
            var script = OnChainIdentity.BuildScript(identityPub, attPriv, "CsTominaga", "craig@rcjbr.org");
            var c = OnChainIdentity.TryRead(script)!;
            var forged = c with { Pseudonym = "Attacker" };
            T.False(OnChainIdentity.Verify(forged), "changing the claim breaks the signature");
        });

        T.Run("HOSTILE: a signature from a different key fails (only the real attestation key attests)", () =>
        {
            var script = OnChainIdentity.BuildScript(identityPub, attPriv, "CsTominaga", "craig@rcjbr.org");
            var c = OnChainIdentity.TryRead(script)!;
            var otherPub = Secp256k1.PublicKeyCompressed(T.Seed(99));
            var forged = c with { AttestationPub = otherPub };
            T.False(OnChainIdentity.Verify(forged), "wrong attestation key rejected");
        });
    }
}
