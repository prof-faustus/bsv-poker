using System.Security.Cryptography;
using System.Text;
using BsvPoker.Crypto;

namespace BsvPoker.Core;

/// <summary>
/// A poker card as an ENCRYPTED 1-sat NFT held in a player's wallet. The card's reveal secret
/// (cardIndex ‖ blind) is sealed to the RECIPIENT'S PUBLIC KEY using a fresh ephemeral ECDH key agreement
/// (ephemeral key → ECDH with the recipient's pubkey → HKDF → AES-256-GCM). Only the recipient's PRIVATE
/// key can derive the same secret and open it; the sender never needs the recipient's private key. This
/// makes a real Alice→Bob transfer possible: Alice opens her card and re-seals it to BOB'S PUBLIC KEY, after
/// which she can no longer open it. ECDH + AES only (no ECIES). The on-chain 1-sat NFT binds H(sealed)+owner.
/// </summary>
public static class CardNft
{
    private static readonly byte[] Info = Encoding.ASCII.GetBytes("bsvpoker-card-ecdh-key-v2");

    public readonly record struct Opened(int CardIndex, byte[] Blind);

    /// <summary>
    /// Seal (cardIndex, blind) to <paramref name="recipientPub33"/> so only that recipient's private key can
    /// open it. Blob = ephemeralPub(33) ‖ nonce(16) ‖ aead. The sealer needs only the recipient's PUBLIC key.
    /// </summary>
    public static string SealToPub(int cardIndex, byte[] blind, byte[] recipientPub33)
    {
        if (cardIndex < 0 || cardIndex > 51) throw new ArgumentException("card index 0..51");
        if (recipientPub33.Length != 33) throw new ArgumentException("recipient pubkey must be 33-byte compressed");
        var eph = Secp256k1.GenerateKeyPair();
        var shared = Secp256k1.Ecdh(eph.Priv, recipientPub33);          // ephemeral ECDH to the recipient's PUBLIC key
        var nonce = RandomNumberGenerator.GetBytes(16);
        var key = Aead.Hkdf(Concat(shared, eph.Pub), nonce, Info);
        var plaintext = Concat(new[] { (byte)cardIndex }, blind);
        var aead = Aead.Seal(key, plaintext, recipientPub33);          // recipient pubkey bound as AAD
        return Convert.ToHexString(Concat(Concat(eph.Pub, nonce), aead)).ToLowerInvariant();
    }

    /// <summary>Open a sealed card with the recipient's private key. Throws if not the recipient / tampered.</summary>
    public static Opened Open(string sealedHex, byte[] recipientPriv32)
    {
        var blob = Convert.FromHexString(sealedHex);
        if (blob.Length < 33 + 16 + 12 + 16 + 1) throw new ArgumentException("sealed blob too short");
        var ephPub = blob[..33];
        var nonce = blob[33..49];
        var aead = blob[49..];
        var myPub = Secp256k1.PublicKeyCompressed(recipientPriv32);
        var shared = Secp256k1.Ecdh(recipientPriv32, ephPub);          // same shared secret from the recipient side
        var key = Aead.Hkdf(Concat(shared, ephPub), nonce, Info);
        var pt = Aead.Open(key, aead, myPub);                          // throws on wrong key / tamper
        return new Opened(pt[0], pt[1..]);
    }

    /// <summary>True iff this private key can open the card (is the current recipient and the blob is intact).</summary>
    public static bool CanOpen(string sealedHex, byte[] recipientPriv32)
    {
        try { Open(sealedHex, recipientPriv32); return true; } catch { return false; }
    }

    /// <summary>
    /// Transfer: the current owner opens with their PRIVATE key and re-seals to the recipient's PUBLIC key.
    /// The sender needs only <paramref name="toPub33"/> (never the recipient's private key); afterwards the
    /// sender can no longer open it, so they LOSE ACCESS. This is a real Alice→Bob wallet transfer.
    /// </summary>
    public static string Transfer(string sealedHex, byte[] fromPriv32, byte[] toPub33)
    {
        var opened = Open(sealedHex, fromPriv32);
        return SealToPub(opened.CardIndex, opened.Blind, toPub33);
    }

    /// <summary>Commitment to a sealed blob (H(sealed)) — bound into the on-chain 1-sat NFT output.</summary>
    public static byte[] SealCommitment(string sealedHex) => Hashes.Sha256(Convert.FromHexString(sealedHex));

    /// <summary>
    /// The 1-sat NFT locking script: &lt;state&gt; OP_DROP &lt;ownerPub&gt; OP_CHECKSIG, where state =
    /// TAG ‖ H(sealed). No OP_RETURN. Issued from the player's own sats (no banker).
    /// </summary>
    public static byte[] NftLock(string sealedHex, byte[] ownerPub33)
    {
        var tag = Encoding.ASCII.GetBytes("BSVPOKER-CARD-NFT-V1");
        var state = Concat(tag, SealCommitment(sealedHex));
        var b = new List<byte>();
        b.Add(0x4c); b.Add((byte)state.Length); b.AddRange(state); // OP_PUSHDATA1 <state>
        b.Add(0x75); // OP_DROP
        b.Add((byte)ownerPub33.Length); b.AddRange(ownerPub33);
        b.Add(0xac); // OP_CHECKSIG
        return b.ToArray();
    }

    private static byte[] Concat(byte[] a, byte[] b) { var o = new byte[a.Length + b.Length]; a.CopyTo(o, 0); b.CopyTo(o, a.Length); return o; }
}
