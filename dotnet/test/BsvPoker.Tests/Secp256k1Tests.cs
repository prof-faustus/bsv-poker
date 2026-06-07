using System.Text;
using BsvPoker.Crypto;

namespace BsvPoker.Tests;

public static class Secp256k1Tests
{
    public static void All()
    {
        Console.WriteLine("secp256k1 (the BSV curve; Ed25519 banned):");

        T.Run("pubkey for d=1 is the generator G (compressed) — known vector", () =>
            T.Eq(T.Hex(Secp256k1.PublicKeyCompressed(T.Seed(1))),
                 "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"));

        T.Run("pubkey for d=2 is 2G (compressed) — known vector", () =>
            T.Eq(T.Hex(Secp256k1.PublicKeyCompressed(T.Seed(2))),
                 "02c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee5"));

        T.Run("sign/verify round-trips; the nonce is RANDOM (two sigs differ, both verify)", () =>
        {
            var msg = Encoding.UTF8.GetBytes("the cake is a lie");
            var pub = Secp256k1.PublicKeyCompressed(T.Seed(1));
            var s1 = Secp256k1.Sign(T.Seed(1), msg);
            var s2 = Secp256k1.Sign(T.Seed(1), msg);
            T.False(T.Hex(s1) == T.Hex(s2), "random nonce → repeated signatures over the same digest DIFFER");
            T.Eq(s1.Length, 64, "compact 64-byte sig");
            T.True(Secp256k1.Verify(pub, msg, s1) && Secp256k1.Verify(pub, msg, s2), "both random-nonce sigs verify");
        });

        T.Run("signatures are low-S (BSV requirement)", () =>
        {
            var msg = Encoding.UTF8.GetBytes("low-s");
            var sig = Secp256k1.Sign(T.Seed(2), msg);
            var s = new System.Numerics.BigInteger(sig[32..], isUnsigned: true, isBigEndian: true);
            var halfN = System.Numerics.BigInteger.Parse("07FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF5D576E7357A4501DDFE92F46681B20A0", System.Globalization.NumberStyles.HexNumber);
            T.True(s <= halfN, "s must be in the lower half");
        });

        T.Run("verify fails on wrong key, tampered message, tampered sig (fail-closed)", () =>
        {
            var msg = Encoding.UTF8.GetBytes("authentic");
            var pub1 = Secp256k1.PublicKeyCompressed(T.Seed(1));
            var pub2 = Secp256k1.PublicKeyCompressed(T.Seed(2));
            var sig = Secp256k1.Sign(T.Seed(1), msg);
            T.False(Secp256k1.Verify(pub2, msg, sig), "wrong key");
            T.False(Secp256k1.Verify(pub1, Encoding.UTF8.GetBytes("forged"), sig), "tampered msg");
            var bad = (byte[])sig.Clone(); bad[63] ^= 0x01;
            T.False(Secp256k1.Verify(pub1, msg, bad), "tampered sig");
        });

        T.Run("ECDH agrees both ways (priv_a·pub_b == priv_b·pub_a)", () =>
        {
            var (privA, pubA) = Secp256k1.GenerateKeyPair();
            var (privB, pubB) = Secp256k1.GenerateKeyPair();
            T.Eq(T.Hex(Secp256k1.Ecdh(privA, pubB)), T.Hex(Secp256k1.Ecdh(privB, pubA)), "shared secret matches");
        });

        T.Run("a zero scalar is rejected", () => T.Throws(() => Secp256k1.PublicKeyCompressed(new byte[32])));
    }
}
