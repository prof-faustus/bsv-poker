using BsvPoker.Core;
using BsvPoker.Crypto;

namespace BsvPoker.Tests;

public static class CardNftTests
{
    public static void All()
    {
        Console.WriteLine("card NFTs (encrypted, held in wallet; transfer = sender loses access):");
        var alice = Secp256k1.GenerateKeyPair();
        var bob = Secp256k1.GenerateKeyPair();
        var blind = new byte[32]; for (int i = 0; i < 32; i++) blind[i] = (byte)(i + 1);

        T.Run("a card sealed to a PUBLIC key opens only with that key's private key", () =>
        {
            // sealed using ONLY Bob's public key — the sender never has Bob's private key
            var s = CardNft.SealToPub(42, blind, bob.Pub);
            var o = CardNft.Open(s, bob.Priv);
            T.Eq(o.CardIndex, 42); T.Eq(T.Hex(o.Blind), T.Hex(blind));
            T.False(CardNft.CanOpen(s, alice.Priv), "a different key cannot open");
        });

        T.Run("TRANSFER uses ONLY the recipient's PUBLIC key: Alice→Bob, Bob opens, Alice cannot", () =>
        {
            var toAlice = CardNft.SealToPub(7, blind, alice.Pub);            // Alice holds the card
            var toBob = CardNft.Transfer(toAlice, alice.Priv, bob.Pub);      // Alice transfers using bob.PUB only
            T.Eq(CardNft.Open(toBob, bob.Priv).CardIndex, 7, "Bob opens to the same card with his private key");
            T.False(CardNft.CanOpen(toBob, alice.Priv), "sender LOST access on transfer");
            // (the test never references bob.Priv to perform the transfer — only to verify Bob can open)
        });

        T.Run("a non-owner cannot transfer (cannot open ⇒ cannot re-seal)", () =>
        {
            var toAlice = CardNft.SealToPub(3, blind, alice.Pub);
            T.Throws(() => CardNft.Transfer(toAlice, bob.Priv, alice.Pub)); // bob isn't the owner → can't open
        });

        T.Run("tampering the sealed blob is rejected (AES-GCM tag/AAD)", () =>
        {
            var s = CardNft.SealToPub(10, blind, alice.Pub);
            var b = Convert.FromHexString(s); b[^1] ^= 0x01;
            T.False(CardNft.CanOpen(Convert.ToHexString(b), alice.Priv));
        });

        T.Run("the 1-sat NFT script binds H(sealed) as PUSHDATA (never an OP_RETURN opcode)", () =>
        {
            var s = CardNft.SealToPub(20, blind, alice.Pub);
            var lockScript = CardNft.NftLock(s, alice.Pub);
            // structure: OP_PUSHDATA1 <len> <state> OP_DROP <33> <pub> OP_CHECKSIG. The commitment is
            // pushed DATA (a coincidental 0x6a inside the hash is fine); we never emit an OP_RETURN opcode.
            T.Eq(lockScript[0], (byte)0x4c, "OP_PUSHDATA1 (state is pushed data)");
            T.Eq(lockScript[^1], (byte)0xac, "ends with OP_CHECKSIG");
            int stateLen = lockScript[1];
            T.Eq(lockScript[2 + stateLen], (byte)0x75, "OP_DROP follows the pushed state");
            T.True(Contains(lockScript, CardNft.SealCommitment(s)), "locking script binds H(sealed)");
        });
    }

    private static bool Contains(byte[] hay, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hay.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++) if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) return true;
        }
        return false;
    }
}
