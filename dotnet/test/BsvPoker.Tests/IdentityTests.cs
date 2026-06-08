using BsvPoker.Core;
using BsvPoker.Crypto;

namespace BsvPoker.Tests;

/// <summary>
/// The identity / Base-ID key system: a root identity key that is NEVER an address, only used to derive ECDH
/// sub-keys; all sub-keys linked in an HMAC hash-chain index; pay/receive to an IDENTITY (handle) via Type-42
/// where the payer derives the public key and the payee derives the matching private key from the SAME shared
/// secret + invoice. These are the executable claims behind "pay Bob/Alice by identity".
/// </summary>
public static class IdentityTests
{
    public static void All()
    {
        Console.WriteLine("identity (Base ID key, Type-42, hash-chained sub-keys):");

        var alice = Secp256k1.GenerateKeyPair();   // Alice's Base ID (root) key
        var bob = Secp256k1.GenerateKeyPair();     // Bob's Base ID (root) key

        T.Run("Type-42: payer's child PUBLIC key matches payee's child PRIVATE key (same one-time key)", () =>
        {
            // Alice pays Bob's identity for invoice "inv-1": she derives the public key to pay to.
            var payToPub = IdentityPayment.PayToPub(bob.Pub, alice.Priv, "inv-1");
            // Bob derives the matching private key to spend it.
            var spendPriv = IdentityPayment.SpendPriv(bob.Priv, alice.Pub, "inv-1");
            T.Eq(Convert.ToHexString(Secp256k1.PublicKeyCompressed(spendPriv)), Convert.ToHexString(payToPub),
                 "payee can spend exactly what the payer paid to");
        });

        T.Run("a different invoice yields a completely different one-time key", () =>
        {
            var a = IdentityPayment.PayToPub(bob.Pub, alice.Priv, "inv-1");
            var b = IdentityPayment.PayToPub(bob.Pub, alice.Priv, "inv-2");
            T.True(Convert.ToHexString(a) != Convert.ToHexString(b), "fresh key per invoice (no reuse)");
        });

        T.Run("the child key is NOT the root key (root is never an address)", () =>
        {
            var payTo = IdentityPayment.PayToPub(bob.Pub, alice.Priv, "inv-1");
            T.True(Convert.ToHexString(payTo) != Convert.ToHexString(bob.Pub), "payment address is a derived sub-key, not the Base ID");
        });

        T.Run("a wrong counterparty cannot derive the spend key", () =>
        {
            var mallory = Secp256k1.GenerateKeyPair();
            var payToPub = IdentityPayment.PayToPub(bob.Pub, alice.Priv, "inv-1");
            var wrong = IdentityPayment.SpendPriv(bob.Priv, mallory.Pub, "inv-1"); // Bob using the wrong payer pub
            T.True(Convert.ToHexString(Secp256k1.PublicKeyCompressed(wrong)) != Convert.ToHexString(payToPub),
                   "ECDH binds the exact payer ⇒ wrong payer ⇒ wrong key");
        });

        T.Run("hash-chained wallet sub-keys are linked, ordered, and verify", () =>
        {
            var chain = KeyChain.WalletChain(alice.Priv, 16);
            T.Eq(chain.Count, 16, "derived the requested number of links");
            T.True(KeyChain.Verify(alice.Pub, chain), "the whole chain verifies as one ordered HMAC hash chain");
            // every key is distinct
            T.Eq(chain.Select(c => Convert.ToHexString(c.Pub)).Distinct().Count(), 16, "all sub-keys distinct");
        });

        T.Run("tampering with any link breaks the chain (tamper-evident)", () =>
        {
            var chain = KeyChain.WalletChain(alice.Priv, 8).ToList();
            var bad = chain[3]; bad.Link[0] ^= 0xFF;                // corrupt one link
            T.True(!KeyChain.Verify(alice.Pub, chain), "a broken link fails verification");
        });

        T.Run("a chain verified against the wrong root is rejected", () =>
        {
            var chain = KeyChain.WalletChain(alice.Priv, 8);
            T.True(!KeyChain.Verify(bob.Pub, chain), "Bob's root does not validate Alice's chain");
        });

        T.Run("KeyRing: identity key is fixed; receive keys are fresh and never reused", () =>
        {
            var ring = new KeyRing(WalletKeys.NewSeed());
            var id1 = ring.IdentityPub(); var id2 = ring.IdentityPub();
            T.Eq(Convert.ToHexString(id1), Convert.ToHexString(id2), "identity key never rotates");
            var r1 = ring.NextReceive(); var r2 = ring.NextReceive();
            T.True(r1.Index != r2.Index, "receive cursor advances");
            T.True(Convert.ToHexString(r1.Pub) != Convert.ToHexString(r2.Pub), "each receive key is unique");
            T.True(Convert.ToHexString(r1.Pub) != Convert.ToHexString(id1), "receive keys are not the identity key");
        });

        T.Run("per-message keys: counterparty derives the matching public per message (conversation hash chain)", () =>
        {
            var pub0 = Type42.DerivePublic(alice.Pub, bob.Priv, "bsvpoker/msg/conv1/0");
            var priv0 = Type42.DerivePrivate(alice.Priv, bob.Pub, "bsvpoker/msg/conv1/0");
            T.Eq(Convert.ToHexString(Secp256k1.PublicKeyCompressed(priv0)), Convert.ToHexString(pub0),
                 "counterparty derives the matching per-message public key");
        });
    }
}
