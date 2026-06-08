using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using BsvPoker.Crypto;

namespace BsvPoker.Core;

/// <summary>
/// Type-42 key derivation (the BSV-native key system) — ported from the user's own estates implementation
/// onto bsv-poker's in-tree secp256k1. The ROOT (Base ID) key is NEVER shared and NEVER used as an address;
/// every key use derives a UNIQUE sub-key from an ECDH shared secret + an invoice number:
/// <code>
///   shared    = ECDH(rootPriv, counterpartyPub)              (32-byte shared X)
///   k         = HMAC-SHA256(key = shared, msg = invoice) mod n
///   childPriv = (rootPriv + k) mod n        childPub = rootPub + k·G
/// </code>
/// The payer computes the recipient's child PUBLIC key (to pay to) without the recipient's root private key;
/// the recipient computes the matching child PRIVATE key to spend it. A different invoice ⇒ a different key,
/// so every payment/message uses a fresh, one-time key. No BIP32/39/44, no mnemonic — BSV-native only.
/// </summary>
public static class Type42
{
    /// <summary>k = HMAC-SHA256(shared, invoice) reduced mod n, as a 32-byte scalar.</summary>
    private static byte[] K(byte[] shared, string invoice)
    {
        using var h = new HMACSHA256(shared);
        var mac = h.ComputeHash(Encoding.UTF8.GetBytes(invoice));
        var k = new BigInteger(mac, isUnsigned: true, isBigEndian: true) % Order;
        return To32(k);
    }

    /// <summary>Child PRIVATE key: (rootPriv + k) mod n, k binding ECDH(rootPriv, counterpartyPub) to the invoice.</summary>
    public static byte[] DerivePrivate(byte[] rootPriv, byte[] counterpartyPub, string invoice)
        => Secp256k1.ScalarAddModN(rootPriv, K(Secp256k1.Ecdh(rootPriv, counterpartyPub), invoice));

    /// <summary>The matching child PUBLIC key (counterparty/payer side): rootPub + k·G.</summary>
    public static byte[] DerivePublic(byte[] rootPub, byte[] counterpartyPriv, string invoice)
        => Secp256k1.PointAddCompressed(rootPub, Secp256k1.PublicKeyCompressed(K(Secp256k1.Ecdh(counterpartyPriv, rootPub), invoice)));

    /// <summary>A wallet's own UNIQUE sub-key bound to an invoice (counterparty = the wallet's own key, so only
    /// the root holder can derive it; the root is never exposed).</summary>
    public static byte[] UniqueKey(byte[] rootPriv, string invoice)
        => DerivePrivate(rootPriv, Secp256k1.PublicKeyCompressed(rootPriv), invoice);

    private static readonly BigInteger Order =
        BigInteger.Parse("0FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141", System.Globalization.NumberStyles.HexNumber);
    private static byte[] To32(BigInteger v)
    {
        var raw = v.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (raw.Length == 32) return raw;
        var o = new byte[32]; Array.Copy(raw, 0, o, 32 - raw.Length, raw.Length); return o;
    }
}

/// <summary>One link of the identity key hash-chain: its index, the chain link hash, and the derived sub-key.</summary>
public sealed record ChainedKey(int Index, byte[] Link, byte[] Priv, byte[] Pub);

/// <summary>
/// Hash-chained Type-42 key derivation — the user's MANDATORY design (ported from estates KeyChain). The Base
/// ID root is never shared/used directly; every sub-key is a UNIQUE key on a verifiable, ordered HASH CHAIN:
/// <code>
///   link[0] = SHA256("bsvpoker-keychain/v1" ‖ rootPub)            (genesis link)
///   link[i] = SHA256(link[i-1] ‖ be32(i))                          (the hash chain index)
///   k[i]    = HMAC-SHA256(ECDH(rootPriv, counterpartyPub), link[i] ‖ be32(i)) mod n
///   childPriv[i] = (rootPriv + k[i]) mod n     childPub[i] = rootPub + k[i]·G
/// </code>
/// Each key binds an INDEX, an ECDH secret, an HMAC, AND the prior link — so the whole set is provably one
/// chain (<see cref="Verify"/>), there is no path from a sub-key back to the root or a sibling, and no key is
/// ever reused. This is the system that backs the wallet's addresses and the per-message conversation keys.
/// </summary>
public static class KeyChain
{
    private static readonly byte[] GenesisTag = Encoding.ASCII.GetBytes("bsvpoker-keychain/v1");
    private static byte[] Be32(int i) => new[] { (byte)(i >> 24), (byte)(i >> 16), (byte)(i >> 8), (byte)i };
    private static byte[] Cat(byte[] a, byte[] b) { var o = new byte[a.Length + b.Length]; Array.Copy(a, o, a.Length); Array.Copy(b, 0, o, a.Length, b.Length); return o; }

    /// <summary>The genesis link of a chain: SHA256("bsvpoker-keychain/v1" ‖ rootPub).</summary>
    public static byte[] GenesisLink(byte[] rootPub) => SHA256.HashData(Cat(GenesisTag, rootPub));

    private static byte[] K(byte[] shared, byte[] link, int index)
    {
        using var h = new HMACSHA256(shared);
        var mac = h.ComputeHash(Cat(link, Be32(index)));
        var k = new BigInteger(mac, isUnsigned: true, isBigEndian: true) % Order;
        var raw = k.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (raw.Length == 32) return raw;
        var o = new byte[32]; Array.Copy(raw, 0, o, 32 - raw.Length, raw.Length); return o;
    }

    /// <summary>Derive the chained key at <paramref name="index"/>: hash-chains <paramref name="prevLink"/>
    /// forward, binds an ECDH secret (with <paramref name="counterpartyPub"/>) + HMAC, returns the sub-key + link.</summary>
    public static ChainedKey Derive(byte[] rootPriv, byte[] counterpartyPub, int index, byte[] prevLink)
    {
        var link = SHA256.HashData(Cat(prevLink, Be32(index)));
        var k = K(Secp256k1.Ecdh(rootPriv, counterpartyPub), link, index);
        var priv = Secp256k1.ScalarAddModN(rootPriv, k);
        return new ChainedKey(index, link, priv, Secp256k1.PublicKeyCompressed(priv));
    }

    /// <summary>The matching child PUBLIC key (counterparty side): rootPub + k·G.</summary>
    public static byte[] DerivePublic(byte[] rootPub, byte[] counterpartyPriv, int index, byte[] prevLink)
    {
        var link = SHA256.HashData(Cat(prevLink, Be32(index)));
        var k = K(Secp256k1.Ecdh(counterpartyPriv, rootPub), link, index);
        return Secp256k1.PointAddCompressed(rootPub, Secp256k1.PublicKeyCompressed(k));
    }

    /// <summary>A wallet's own hash-chained sub-keys [0..count) — counterparty is the wallet's own key.</summary>
    public static List<ChainedKey> WalletChain(byte[] rootPriv, int count)
    {
        var rootPub = Secp256k1.PublicKeyCompressed(rootPriv);
        var outp = new List<ChainedKey>(count);
        var link = GenesisLink(rootPub);
        for (int i = 0; i < count; i++) { var ck = Derive(rootPriv, rootPub, i, link); outp.Add(ck); link = ck.Link; }
        return outp;
    }

    /// <summary>Verify a chain is intact: each link = SHA256(prevLink ‖ be32(index)) in order from genesis, and
    /// each key's pub matches its priv. Any break ⇒ false (tamper-evidence over the whole ordered set).</summary>
    public static bool Verify(byte[] rootPub, IReadOnlyList<ChainedKey> chain)
    {
        var link = GenesisLink(rootPub);
        for (int i = 0; i < chain.Count; i++)
        {
            var ck = chain[i];
            var expect = SHA256.HashData(Cat(link, Be32(ck.Index)));
            if (ck.Index != i || !expect.AsSpan().SequenceEqual(ck.Link)) return false;
            if (!Secp256k1.PublicKeyCompressed(ck.Priv).AsSpan().SequenceEqual(ck.Pub)) return false;
            link = ck.Link;
        }
        return true;
    }

    private static readonly BigInteger Order =
        BigInteger.Parse("0FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141", System.Globalization.NumberStyles.HexNumber);
}

/// <summary>
/// The wallet's KEY RING (ported from estates) — from ONE 32-byte seed it yields an effectively unlimited
/// space of UNIQUE keys: a fresh key for every receive and every message, never reused. The IDENTITY (Base ID)
/// key is the one fixed, never-rotating slot — it is the user's identity (an NFT-equivalent handle owner) and
/// is used only to derive ECDH sub-keys, never directly as an address. Fully deterministic, so the only state
/// worth persisting is the next-index cursor.
/// </summary>
public sealed class KeyRing
{
    private readonly byte[] _seed;
    private long _nextReceive;     // next unused receive index (index 0 reserved; identity is its own slot)

    public KeyRing(byte[] seed32, long nextReceive = 1)
    {
        if (seed32 is null || seed32.Length != 32) throw new ArgumentException("seed must be 32 bytes");
        _seed = (byte[])seed32.Clone();
        _nextReceive = nextReceive < 1 ? 1 : nextReceive;
    }

    /// <summary>The ONE fixed Base ID / identity key — never rotates, never an address (derives sub-keys only).</summary>
    public byte[] IdentityPriv() => Type42.UniqueKey(_seed, "bsvpoker/identity");
    public byte[] IdentityPub() => Secp256k1.PublicKeyCompressed(IdentityPriv());

    /// <summary>A deterministic receive key at an arbitrary index (the index space is trillions+).</summary>
    public byte[] PrivAt(long index) => Type42.UniqueKey(_seed, "bsvpoker/wallet/" + index);
    public byte[] PubAt(long index) => Secp256k1.PublicKeyCompressed(PrivAt(index));

    /// <summary>The next FRESH, never-before-used receive key. Advances the cursor; never returned twice.</summary>
    public (long Index, byte[] Priv, byte[] Pub) NextReceive()
    {
        long i = _nextReceive++;
        var p = PrivAt(i);
        return (i, p, Secp256k1.PublicKeyCompressed(p));
    }
    public long NextIndex => _nextReceive;

    /// <summary>A per-message key for a conversation with a counterparty — ECDH-bound and advanced per seq, so
    /// every message uses a NEW key (a hash chain). The counterparty derives the matching public with the same
    /// invoice via <see cref="Type42.DerivePublic"/>.</summary>
    public byte[] MessagePriv(byte[] counterpartyPub, string convId, long seq)
        => Type42.DerivePrivate(_seed, counterpartyPub, "bsvpoker/msg/" + convId + "/" + seq);
    public byte[] MessagePub(byte[] counterpartyPub, string convId, long seq)
        => Secp256k1.PublicKeyCompressed(MessagePriv(counterpartyPub, convId, seq));
}

/// <summary>
/// Pay-to-identity (the number-42 payment scheme): a payee NEVER publishes an address — only its identity
/// (Base ID) MASTER public key. For each payment the payer and payee share an ECDH secret + an agreed invoice
/// number and BOTH derive the SAME fresh sub-key — the payer the PUBLIC key to pay to, the payee the matching
/// PRIVATE key to spend it. Every payment is a new one-time address. This is how the wallet pays a handle/
/// identity (Bob, Alice) instead of a raw address.
/// </summary>
public static class IdentityPayment
{
    /// <summary>Payer side: the fresh PUBLIC key (the one-time address to pay) for the payee identity.</summary>
    public static byte[] PayToPub(byte[] payeeIdentityPub, byte[] payerIdentityPriv, string invoice)
        => Type42.DerivePublic(payeeIdentityPub, payerIdentityPriv, invoice);

    /// <summary>Payee side: the matching PRIVATE key to SPEND that payment (PublicKey(this) == PayToPub(...)).</summary>
    public static byte[] SpendPriv(byte[] payeeIdentityPriv, byte[] payerIdentityPub, string invoice)
        => Type42.DerivePrivate(payeeIdentityPriv, payerIdentityPub, invoice);
}
