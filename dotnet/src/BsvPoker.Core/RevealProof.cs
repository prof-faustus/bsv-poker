using System.Security.Cryptography;
using System.Text;
using BsvPoker.Crypto;

namespace BsvPoker.Core;

/// <summary>
/// "PROVE WHAT IT IS" — bind every card-scalar reveal to a commitment published during the deal, WITHOUT
/// leaking the hidden card.
///
/// In the commutative-encryption deal each position carries a product of per-card scalars; a player reveals
/// its scalar d (plus the blinding nonce) for a position so the recipient can strip it and verify it. The
/// commitment is a HIDING, BINDING hash commitment: C = SHA-256(domain ‖ d ‖ r) with a fresh random 32-byte
/// nonce r, broadcast at REMASK time (before any card is opened). At reveal the verifier is given (d, r) and
/// checks SHA-256(domain ‖ d ‖ r) == C.
///
/// WHY IT MUST BE HIDING (the leak a plain C = d·G commitment causes): the card base points are Mᵢ = (i+1)·G
/// with PUBLICLY KNOWN ratios. During the deal the masked deck position is public and every OTHER player's
/// scalar for it is broadcast, so any observer can strip those and obtain X = d·M_m for a position whose card
/// m is meant to stay hidden (e.g. an opponent's hole card, whose own scalar d is not yet revealed). If the
/// commitment were C = d·G, then X = d·(m+1)·G = (m+1)·C, so the observer could test (j+1)·C == X for every
/// candidate j and READ the hidden card — a total privacy break. A hash commitment with a random nonce
/// reveals nothing about d (hiding), so that test is impossible; the card stays information-theoretically
/// hidden until its own scalar is revealed.
///
/// WHY IT IS STILL BINDING (the substitution attack it stops): a dishonest player holding the true scalar d
/// for a final point P = d·M_m can compute a FORGED scalar d' = d·(m+1)·(m'+1)⁻¹ (mod n) so that revealing d'
/// makes P unmask to a DIFFERENT but perfectly valid card M_{m'} — fooling the "is it a real card?" check
/// (Identify). The commitment defeats this: to open the published C the player must produce (d', r') with
/// SHA-256(domain ‖ d' ‖ r') == C; by collision resistance no d' ≠ d works, so the substituted reveal is
/// provably rejected. The nonce was fixed at remask, before the player learned its card, so it cannot be
/// chosen to suit a substitution.
/// </summary>
public static class RevealProof
{
    private static readonly byte[] Domain = Encoding.ASCII.GetBytes("bsvpoker-revealproof-v2");

    /// <summary>Commit to a per-card scalar with a fresh random nonce: returns (C, nonce). Publish C at remask;
    /// keep the nonce and disclose it together with the scalar at reveal time.</summary>
    public static (byte[] Commitment, byte[] Nonce) Commit(ReadOnlySpan<byte> scalar)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        return (CommitWith(scalar, nonce), nonce);
    }

    /// <summary>The commitment for an explicit (scalar, nonce): C = SHA-256(domain ‖ scalar ‖ nonce).</summary>
    public static byte[] CommitWith(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> nonce)
    {
        if (scalar.Length != 32 || nonce.Length != 32) throw new ArgumentException("scalar and nonce must be 32 bytes");
        var buf = new byte[Domain.Length + 64];
        Domain.CopyTo(buf.AsSpan(0));
        scalar.CopyTo(buf.AsSpan(Domain.Length));
        nonce.CopyTo(buf.AsSpan(Domain.Length + 32));
        return Hashes.Sha256(buf);
    }

    /// <summary>True iff (revealedScalar, nonce) opens the commitment. Fail-closed on any malformed input.</summary>
    public static bool Verify(byte[] revealedScalar, byte[] nonce, byte[] commitment)
    {
        try
        {
            if (revealedScalar is not { Length: 32 } || nonce is not { Length: 32 } || commitment is not { Length: 32 }) return false;
            if (!Secp256k1.IsValidScalar(revealedScalar)) return false;
            return CryptographicOperations.FixedTimeEquals(CommitWith(revealedScalar, nonce), commitment);
        }
        catch { return false; }
    }
}
