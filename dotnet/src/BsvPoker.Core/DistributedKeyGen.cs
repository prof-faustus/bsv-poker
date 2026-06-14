using System.Security.Cryptography;
using System.Text;
using BsvPoker.Crypto;

namespace BsvPoker.Core;

/// <summary>
/// Distributed (networked) dealerless key generation — the REAL-peer protocol behind the threshold crypto
/// that <see cref="ThresholdSharing"/>/<see cref="ThresholdEcdsa"/> model as a local simulation. Each of the n
/// live players runs <see cref="Deal"/>: it picks a SECRET degree-t polynomial, broadcasts its Feldman
/// commitments (a_γ·G), and sends every other player j the share f_i(j) SEALED to j's public key (ephemeral
/// ECDH → HKDF → AES-256-GCM, the same construction as the card NFTs — only j can open it). Then each player
/// runs <see cref="Finalize"/>: it opens the share each dealer sealed to it, VERIFIES it against that dealer's
/// commitments (Feldman — a lying dealer is named and blamed), and sums the verified shares into its joint
/// key share. Everyone derives the SAME joint public key Σ a_0·G without any dealer and without ever learning
/// the joint secret. This is transport-agnostic (pure messages), so the P2P mesh — or any channel — can drive
/// it; it is unit-proven here by exchanging the messages in memory.
/// </summary>
public static class DistributedKeyGen
{
    private static readonly byte[] Info = Encoding.ASCII.GetBytes("bsvpoker-dkg-share-v1");
    private static readonly byte[] ShareAadDomain = Encoding.ASCII.GetBytes("bsvpoker-dkg-share-aad-v2");

    /// <summary>The authenticated context bound into each sealed share: the SESSION id, the DEALER's pubkey, the
    /// recipient index, and the recipient's pubkey. Binding all of this as AAD means a sealed share cannot be
    /// replayed into a different DKG session, re-attributed to a different dealer, or redirected to a different
    /// recipient slot — the AEAD tag fails to open if any of it differs.</summary>
    public static byte[] ShareContext(byte[] sessionId, byte[] dealerPub33, int recipientIndex, byte[] recipientPub33)
    {
        var ms = new List<byte>(ShareAadDomain);
        ms.AddRange(sessionId ?? Array.Empty<byte>());
        ms.AddRange(dealerPub33);
        ms.AddRange(BitConverter.GetBytes(recipientIndex));
        ms.AddRange(recipientPub33);
        return Hashes.Sha256(ms.ToArray());   // fixed-length, domain-separated AAD
    }

    /// <summary>One dealer's broadcast: its Feldman commitments and, per recipient index, the share f_i(j)
    /// SEALED to recipient j's public key (only j can open it). The dealer's own pubkey is included so peers
    /// can authenticate the broadcast at the transport layer.</summary>
    public sealed record Round1(int Index, byte[] DealerPub, byte[][] Commitments, Dictionary<int, string> SealedShares);

    /// <summary>Round 1: produce this party's secret polynomial and its broadcast (commitments + a sealed
    /// share for each peer in <paramref name="peerPubs"/>, keyed by peer index 1..n, including itself).</summary>
    public static (ThresholdSharing.Polynomial Poly, Round1 Msg) Deal(int myIndex, byte[] myPub, int t, IReadOnlyDictionary<int, byte[]> peerPubs, byte[] sessionId)
    {
        if (t < 1) throw new ArgumentException("degree t >= 1");
        var poly = ThresholdSharing.Polynomial.Random(t + 1);
        var commitments = poly.Commitments();
        var sealedShares = new Dictionary<int, string>();
        // each share is sealed to recipient j AND bound to (session, this dealer, j) so it cannot be replayed.
        foreach (var (j, pubj) in peerPubs) sealedShares[j] = SealScalar(poly.Eval(j), pubj, ShareContext(sessionId, myPub, j, pubj));
        return (poly, new Round1(myIndex, myPub, commitments, sealedShares));
    }

    /// <summary>Round 2: open and Feldman-VERIFY the share every dealer sealed to me, then sum the verified
    /// shares into my joint key share and sum the dealers' free-term commitments into the joint public key.
    /// Returns Blame = the index of the FIRST dealer whose share is missing, unopenable, or fails Feldman
    /// (provably caught); on success Blame is null and (JointShare, JointPublicKey) are set.</summary>
    public static (byte[] JointShare, byte[] JointPublicKey, int? Blame) Finalize(int myIndex, byte[] myPriv, IReadOnlyList<Round1> all, byte[] sessionId)
    {
        var myPub = Secp256k1.PublicKeyCompressed(myPriv);
        byte[]? share = null, pub = null;
        foreach (var r in all)
        {
            if (r.Commitments == null || r.Commitments.Length < 1) return (Array.Empty<byte>(), Array.Empty<byte>(), r.Index);
            if (!r.SealedShares.TryGetValue(myIndex, out var sealedHex)) return (Array.Empty<byte>(), Array.Empty<byte>(), r.Index);
            byte[] fij;
            // the AAD must match what the dealer bound: (session, that dealer's pubkey, my index, my pubkey).
            try { fij = OpenScalar(sealedHex, myPriv, ShareContext(sessionId, r.DealerPub, myIndex, myPub)); }
            catch { return (Array.Empty<byte>(), Array.Empty<byte>(), r.Index); }
            // FELDMAN: the sealed share must be consistent with the dealer's public commitments, else blame.
            if (!ThresholdSharing.VerifyShare(myIndex, fij, r.Commitments))
                return (Array.Empty<byte>(), Array.Empty<byte>(), r.Index);
            share = share == null ? fij : Secp256k1.ScalarFieldAddModN(share, fij);
            pub = pub == null ? r.Commitments[0] : Secp256k1.PointAddCompressed(pub, r.Commitments[0]);
        }
        if (share == null || pub == null) return (Array.Empty<byte>(), Array.Empty<byte>(), -1);
        return (share, pub, null);
    }

    /// <summary>Seal a 32-byte scalar to a recipient's public key (ephemeral ECDH → HKDF → AES-256-GCM).
    /// Blob = ephemeralPub(33) ‖ nonce(16) ‖ aead. Only the recipient's private key can open it.</summary>
    public static string SealScalar(byte[] scalar32, byte[] recipientPub33, byte[] aad)
    {
        if (scalar32.Length != 32) throw new ArgumentException("scalar must be 32 bytes");
        if (recipientPub33.Length != 33) throw new ArgumentException("recipient pubkey must be 33-byte compressed");
        var eph = Secp256k1.GenerateKeyPair();
        var shared = Secp256k1.Ecdh(eph.Priv, recipientPub33);
        var nonce = RandomNumberGenerator.GetBytes(16);
        var key = Aead.Hkdf(Concat(shared, eph.Pub), nonce, Info);
        var aead = Aead.Seal(key, scalar32, aad);   // caller-supplied AAD binds the context (e.g. session/dealer)
        return Convert.ToHexString(Concat(Concat(eph.Pub, nonce), aead)).ToLowerInvariant();
    }

    /// <summary>Open a scalar sealed by <see cref="SealScalar"/> with the recipient's private key and the SAME
    /// <paramref name="aad"/> the sealer bound (throws on wrong key / tamper / context mismatch).</summary>
    public static byte[] OpenScalar(string sealedHex, byte[] recipientPriv32, byte[] aad)
    {
        var blob = Convert.FromHexString(sealedHex);
        if (blob.Length < 33 + 16 + 16) throw new ArgumentException("sealed blob too short");
        var ephPub = blob[..33];
        var nonce = blob[33..49];
        var aead = blob[49..];
        var shared = Secp256k1.Ecdh(recipientPriv32, ephPub);
        var key = Aead.Hkdf(Concat(shared, ephPub), nonce, Info);
        return Aead.Open(key, aead, aad);
    }

    private static byte[] Concat(byte[] a, byte[] b) { var o = new byte[a.Length + b.Length]; a.CopyTo(o, 0); b.CopyTo(o, a.Length); return o; }
}
