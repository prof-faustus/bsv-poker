using System.Numerics;
using System.Security.Cryptography;

namespace BsvPoker.Crypto;

/// <summary>
/// secp256k1 — the BSV curve. Pure, dependency-free C# (System.Numerics.BigInteger): ECDSA with a
/// CSPRNG RANDOM nonce (rejection-sampled into [1, n-1]) and LOW-S (BSV consensus requires low-S), ECDH
/// (for the chat key agreement), and compressed public-key derivation. Jacobian point arithmetic (one
/// field inversion per scalar-mul). Ed25519 is BANNED — every key here is secp256k1. Per project rule,
/// deterministic-nonce schemes are NOT used — each signature draws a fresh random nonce.
///
/// Verified against known key vectors (d=1→G, d=2→2G); signatures over the same digest differ each time
/// yet always verify (random-nonce property tested in the suite).
/// </summary>
public static class Secp256k1
{
    private static readonly BigInteger P = BigInteger.Parse("0FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEFFFFFC2F", System.Globalization.NumberStyles.HexNumber);
    private static readonly BigInteger N = BigInteger.Parse("0FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141", System.Globalization.NumberStyles.HexNumber);
    private static readonly BigInteger Gx = BigInteger.Parse("079BE667EF9DCBBAC55A06295CE870B07029BFCDB2DCE28D959F2815B16F81798", System.Globalization.NumberStyles.HexNumber);
    private static readonly BigInteger Gy = BigInteger.Parse("0483ADA7726A3C4655DA4FBFC0E1108A8FD17B448A68554199C47D08FFB10D4B8", System.Globalization.NumberStyles.HexNumber);
    private const int B = 7;

    private readonly record struct Aff(BigInteger X, BigInteger Y, bool Inf);
    private readonly record struct Jac(BigInteger X, BigInteger Y, BigInteger Z);
    private static readonly Aff G = new(Gx, Gy, false);
    private static readonly Aff AffInf = new(0, 0, true);
    private static readonly Jac JacInf = new(1, 1, 0);

    private static BigInteger Mod(BigInteger a, BigInteger m)
    {
        var r = a % m;
        return r.Sign < 0 ? r + m : r;
    }
    private static BigInteger PowMod(BigInteger b, BigInteger e, BigInteger m) => BigInteger.ModPow(Mod(b, m), e, m);
    private static BigInteger InvMod(BigInteger a, BigInteger m) => PowMod(a, m - 2, m); // Fermat (m prime)

    private static Jac ToJac(Aff p) => p.Inf ? JacInf : new Jac(p.X, p.Y, 1);
    private static Aff ToAff(Jac j)
    {
        if (j.Z.IsZero) return AffInf;
        var zi = InvMod(j.Z, P);
        var zi2 = Mod(zi * zi, P);
        var zi3 = Mod(zi2 * zi, P);
        return new Aff(Mod(j.X * zi2, P), Mod(j.Y * zi3, P), false);
    }

    private static Jac JacDouble(Jac j)
    {
        if (j.Z.IsZero || j.Y.IsZero) return JacInf;
        var a = Mod(j.X * j.X, P);
        var bb = Mod(j.Y * j.Y, P);
        var c = Mod(bb * bb, P);
        var d = Mod(2 * (Mod((j.X + bb) * (j.X + bb), P) - a - c), P);
        var e = Mod(3 * a, P);
        var f = Mod(e * e, P);
        var x3 = Mod(f - 2 * d, P);
        var y3 = Mod(e * (d - x3) - 8 * c, P);
        var z3 = Mod(2 * j.Y * j.Z, P);
        return new Jac(x3, y3, z3);
    }

    private static Jac JacAdd(Jac p, Jac q)
    {
        if (p.Z.IsZero) return q;
        if (q.Z.IsZero) return p;
        var z1z1 = Mod(p.Z * p.Z, P);
        var z2z2 = Mod(q.Z * q.Z, P);
        var u1 = Mod(p.X * z2z2, P);
        var u2 = Mod(q.X * z1z1, P);
        var s1 = Mod(p.Y * q.Z * z2z2, P);
        var s2 = Mod(q.Y * p.Z * z1z1, P);
        if (u1 == u2)
        {
            if (s1 != s2) return JacInf;
            return JacDouble(p);
        }
        var h = Mod(u2 - u1, P);
        var hh = Mod(h * h, P);
        var hhh = Mod(h * hh, P);
        var r = Mod(s2 - s1, P);
        var v = Mod(u1 * hh, P);
        var x3 = Mod(r * r - hhh - 2 * v, P);
        var y3 = Mod(r * (v - x3) - s1 * hhh, P);
        var z3 = Mod(p.Z * q.Z * h, P);
        return new Jac(x3, y3, z3);
    }

    private static Jac JacMul(BigInteger k, Aff pAff)
    {
        var result = JacInf;
        var addend = ToJac(pAff);
        var n = Mod(k, N);
        while (n > 0)
        {
            if (!(n & 1).IsZero) result = JacAdd(result, addend);
            addend = JacDouble(addend);
            n >>= 1;
        }
        return result;
    }

    private static Aff ScalarMul(BigInteger k, Aff p) => ToAff(JacMul(k, p));
    private static Aff PointAdd(Aff a, Aff b) => ToAff(JacAdd(ToJac(a), ToJac(b)));

    // ---- encoding ----
    private static BigInteger FromBytes(ReadOnlySpan<byte> b) => new(b, isUnsigned: true, isBigEndian: true);
    private static byte[] To32(BigInteger v)
    {
        var raw = v.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (raw.Length == 32) return raw;
        var outb = new byte[32];
        Array.Copy(raw, 0, outb, 32 - raw.Length, raw.Length);
        return outb;
    }

    /// <summary>Normalize a 32-byte seed to a valid private scalar in [1, n-1] (rejects 0).</summary>
    public static BigInteger NormalizeScalar(ReadOnlySpan<byte> seed32)
    {
        if (seed32.Length != 32) throw new ArgumentException("seed must be 32 bytes");
        var d = Mod(FromBytes(seed32), N);
        if (d.IsZero) throw new ArgumentException("invalid (zero) private scalar");
        return d;
    }

    /// <summary>33-byte SEC-1 compressed public key from a 32-byte private seed.</summary>
    public static byte[] PublicKeyCompressed(ReadOnlySpan<byte> seed32)
    {
        var d = NormalizeScalar(seed32);
        var pt = ScalarMul(d, G);
        if (pt.Inf) throw new InvalidOperationException("degenerate public key");
        return Compress(pt);
    }

    private static byte[] Compress(Aff pt)
    {
        var outb = new byte[33];
        outb[0] = (byte)((pt.Y & 1).IsZero ? 0x02 : 0x03);
        To32(pt.X).CopyTo(outb, 1);
        return outb;
    }

    private static Aff Decompress(ReadOnlySpan<byte> pub33)
    {
        if (pub33.Length != 33 || (pub33[0] != 0x02 && pub33[0] != 0x03)) throw new ArgumentException("not a compressed secp256k1 point");
        var x = FromBytes(pub33[1..]);
        if (x <= 0 || x >= P) throw new ArgumentException("x out of range");
        var y2 = Mod(x * x % P * x + B, P);
        var y = PowMod(y2, (P + 1) / 4, P); // p ≡ 3 (mod 4)
        if (Mod(y * y, P) != y2) throw new ArgumentException("point not on curve");
        if (((y & 1) == 0 ? 0x02 : 0x03) != pub33[0]) y = Mod(-y, P);
        return new Aff(x, y, false);
    }

    // ---- HMAC-SHA256 helper ----
    private static byte[] Hmac(byte[] key, byte[] msg)
    {
        using var h = new HMACSHA256(key);
        return h.ComputeHash(msg);
    }
    private static byte[] Concat(params byte[][] parts)
    {
        var len = parts.Sum(p => p.Length);
        var outb = new byte[len];
        var o = 0;
        foreach (var p in parts) { p.CopyTo(outb, o); o += p.Length; }
        return outb;
    }

    /// <summary>
    /// A fresh per-signature nonce drawn from the system CSPRNG, rejection-sampled into [1, n-1] (reject 0
    /// and any value ≥ n so there is no modulo bias). No deterministic-nonce scheme is used.
    /// </summary>
    private static BigInteger RandomNonce()
    {
        while (true)
        {
            var k = FromBytes(RandomNumberGenerator.GetBytes(32));
            if (k >= 1 && k < N) return k;
        }
    }

    /// <summary>
    /// ECDSA sign: 64-byte compact (r‖s), LOW-S, over SHA-256(message). Fresh CSPRNG random nonce per call.
    /// </summary>
    public static byte[] Sign(ReadOnlySpan<byte> seed32, ReadOnlySpan<byte> message)
        => SignDigest(seed32, SHA256.HashData(message));

    /// <summary>
    /// ECDSA sign a 32-byte DIGEST directly (no extra hashing) — for Bitcoin/BSV where the sighash is
    /// already sha256d(preimage). 64-byte compact (r‖s), LOW-S, with a fresh CSPRNG RANDOM nonce per call.
    /// </summary>
    public static byte[] SignDigest(ReadOnlySpan<byte> seed32, ReadOnlySpan<byte> digest32)
    {
        if (digest32.Length != 32) throw new ArgumentException("digest must be 32 bytes");
        var d = NormalizeScalar(seed32);
        var h = digest32.ToArray();
        var e = Mod(FromBytes(h), N);
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var k = RandomNonce();
            var rPt = ScalarMul(k, G);
            if (rPt.Inf) continue;
            var r = Mod(rPt.X, N);
            if (r.IsZero) continue;
            var s = Mod(InvMod(k, N) * (e + r * d), N);
            if (s.IsZero) continue;
            if (s > N / 2) s = N - s; // low-S
            return Concat(To32(r), To32(s));
        }
        throw new InvalidOperationException("failed to produce signature");
    }

    /// <summary>DER-encode a 64-byte compact (r‖s) signature (what OP_CHECKSIG/BSV expects, low-S already).</summary>
    public static byte[] ToDer(ReadOnlySpan<byte> sig64)
    {
        static byte[] Trim(ReadOnlySpan<byte> v)
        {
            int i = 0; while (i < v.Length - 1 && v[i] == 0) i++;
            var b = v[i..].ToArray();
            if ((b[0] & 0x80) != 0) { var t = new byte[b.Length + 1]; b.CopyTo(t, 1); b = t; }
            return b;
        }
        var r = Trim(sig64[..32]); var s = Trim(sig64[32..]);
        var outb = new byte[6 + r.Length + s.Length];
        outb[0] = 0x30; outb[1] = (byte)(4 + r.Length + s.Length);
        outb[2] = 0x02; outb[3] = (byte)r.Length; r.CopyTo(outb, 4);
        outb[4 + r.Length] = 0x02; outb[5 + r.Length] = (byte)s.Length; s.CopyTo(outb, 6 + r.Length);
        return outb;
    }

    /// <summary>Verify a 64-byte compact (r‖s) signature of SHA-256(message) against a compressed pubkey.</summary>
    public static bool Verify(ReadOnlySpan<byte> pub33, ReadOnlySpan<byte> message, ReadOnlySpan<byte> sig64)
        => VerifyDigest(pub33, SHA256.HashData(message), sig64);

    /// <summary>Verify a 64-byte compact signature over a 32-byte DIGEST (no extra hashing) — Bitcoin sighash.</summary>
    public static bool VerifyDigest(ReadOnlySpan<byte> pub33, ReadOnlySpan<byte> digest32, ReadOnlySpan<byte> sig64)
    {
        try
        {
            if (sig64.Length != 64 || digest32.Length != 32) return false;
            var r = FromBytes(sig64[..32]);
            var s = FromBytes(sig64[32..]);
            if (r <= 0 || r >= N || s <= 0 || s >= N) return false;
            var pub = Decompress(pub33);
            var e = Mod(FromBytes(digest32), N);
            var w = InvMod(s, N);
            var u1 = Mod(e * w, N);
            var u2 = Mod(r * w, N);
            var rp = PointAdd(ScalarMul(u1, G), ScalarMul(u2, pub));
            if (rp.Inf) return false;
            return Mod(rp.X, N) == r;
        }
        catch { return false; }
    }

    /// <summary>
    /// ECDH shared secret = the 32-byte X of (priv · pub). Used by the chat layer to derive a
    /// per-message symmetric key with an EPHEMERAL keypair (no key reuse). Hash this before use.
    /// </summary>
    public static byte[] Ecdh(ReadOnlySpan<byte> priv32, ReadOnlySpan<byte> pub33)
    {
        var d = NormalizeScalar(priv32);
        var pub = Decompress(pub33);
        var shared = ScalarMul(d, pub);
        if (shared.Inf) throw new InvalidOperationException("degenerate ECDH point");
        return To32(shared.X);
    }

    /// <summary>
    /// Scalar multiply an arbitrary point: returns the 33-byte compressed point k·P. Used by the
    /// commutative-encryption mental-poker deal (masking/unmasking cards as curve points). Because
    /// a·(b·P) == b·(a·P), masks applied by different players commute and can be removed in any order.
    /// </summary>
    public static byte[] PointMul(ReadOnlySpan<byte> pub33, ReadOnlySpan<byte> scalar32)
    {
        var k = NormalizeScalar(scalar32);
        var pt = ScalarMul(k, Decompress(pub33));
        if (pt.Inf) throw new InvalidOperationException("degenerate point (k·P = ∞)");
        return Compress(pt);
    }

    /// <summary>
    /// (a + b) mod n as 32 bytes — the scalar add at the heart of Type-42 / hash-chained key derivation:
    /// childPriv = (rootPriv + k) mod n. Throws if the sum is zero (a degenerate, unusable private key).
    /// </summary>
    public static byte[] ScalarAddModN(ReadOnlySpan<byte> a32, ReadOnlySpan<byte> b32)
    {
        var s = Mod(FromBytes(a32) + FromBytes(b32), N);
        if (s.IsZero) throw new ArgumentException("degenerate (zero) derived scalar");
        return To32(s);
    }

    /// <summary>
    /// Compressed point addition A + B — the public-key side of Type-42 derivation: the payer computes the
    /// recipient's child public key as childPub = rootPub + k·G without ever seeing the recipient's root
    /// private key. Throws if the result is the point at infinity (A == −B).
    /// </summary>
    public static byte[] PointAddCompressed(ReadOnlySpan<byte> a33, ReadOnlySpan<byte> b33)
    {
        var sum = PointAdd(Decompress(a33), Decompress(b33));
        if (sum.Inf) throw new InvalidOperationException("degenerate point sum (A + B = ∞)");
        return Compress(sum);
    }

    /// <summary>The modular inverse of a scalar mod n, as 32 bytes — so a mask k can be removed via k⁻¹.</summary>
    public static byte[] ScalarInverse(ReadOnlySpan<byte> scalar32)
    {
        var k = NormalizeScalar(scalar32);
        return To32(InvMod(k, N));
    }

    /// <summary>True iff <paramref name="pub33"/> is a valid 33-byte compressed point ON the curve (not infinity).</summary>
    public static bool IsValidPoint(ReadOnlySpan<byte> pub33)
    {
        try { var p = Decompress(pub33); return !p.Inf; } catch { return false; }
    }

    /// <summary>True iff <paramref name="scalar32"/> is a valid non-zero scalar in [1, n-1] (invertible, no reduction).</summary>
    public static bool IsValidScalar(ReadOnlySpan<byte> scalar32)
    {
        if (scalar32.Length != 32) return false;
        var k = FromBytes(scalar32);
        return k >= 1 && k < N;
    }

    /// <summary>The compressed base point for card index i: (i+1)·G. Distinct, public, and recoverable.</summary>
    public static byte[] CardBasePoint(int i)
    {
        if (i < 0) throw new ArgumentOutOfRangeException(nameof(i));
        return PublicKeyCompressed(To32(i + 1));
    }

    /// <summary>Generate a fresh (private 32, compressed-public 33) keypair from the system CSPRNG.</summary>
    public static (byte[] Priv, byte[] Pub) GenerateKeyPair()
    {
        while (true)
        {
            var priv = RandomNumberGenerator.GetBytes(32);
            var d = Mod(FromBytes(priv), N);
            if (d.IsZero) continue;
            return (priv, PublicKeyCompressed(priv));
        }
    }
}
