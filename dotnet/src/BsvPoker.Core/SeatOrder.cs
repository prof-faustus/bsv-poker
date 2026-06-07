using BsvPoker.Crypto;

namespace BsvPoker.Core;

/// <summary>
/// Anti-grinding seat / button assignment (audit #4). Seats are NOT assigned by sorted public key (which a
/// player could grind by generating many keys). Instead every admitted player COMMITS to a random nonce
/// before the order is decided, then reveals it; a JOINT seed is derived from ALL revealed nonces, and seat
/// order = players sorted by H(jointSeed ‖ pub). Because the joint seed depends on every player's nonce —
/// revealed only after commitments are locked — no player can predict or bias their seat by choosing keys or
/// their own nonce. A reveal that does not match the prior commitment is rejected.
/// </summary>
public static class SeatOrder
{
    private static readonly byte[] Domain = System.Text.Encoding.ASCII.GetBytes("bsvpoker-seatorder-v1");

    /// <summary>Commitment a player publishes before the order is decided.</summary>
    public static byte[] Commit(byte[] nonce) => Hashes.Sha256(Concat(Domain, nonce));

    /// <summary>Verify a revealed nonce matches the player's prior commitment.</summary>
    public static bool VerifyReveal(byte[] commitment, byte[] nonce) => ConstEq(commitment, Commit(nonce));

    /// <summary>
    /// The joint seed from every player's revealed (pub, nonce). Order-independent (sorted by pubkey), so all
    /// players compute the same seed; it depends on EVERY nonce, so no single player controls it.
    /// </summary>
    public static byte[] JointSeed(IReadOnlyList<(byte[] Pub, byte[] Nonce)> reveals)
    {
        var ms = new List<byte>(Domain);
        foreach (var r in reveals.OrderBy(r => Convert.ToHexString(r.Pub), StringComparer.Ordinal))
        { ms.AddRange(r.Pub); ms.AddRange(r.Nonce); }
        return Hashes.Sha256(ms.ToArray());
    }

    /// <summary>Seat order: player indices sorted by H(jointSeed ‖ pub). Deterministic given the joint seed.</summary>
    public static int[] Assign(IReadOnlyList<byte[]> pubs, byte[] jointSeed)
    {
        return Enumerable.Range(0, pubs.Count)
            .OrderBy(i => Convert.ToHexString(Hashes.Sha256(Concat(jointSeed, pubs[i]))), StringComparer.Ordinal)
            .ToArray();
    }

    private static byte[] Concat(params byte[][] parts) { var ms = new List<byte>(); foreach (var p in parts) ms.AddRange(p); return ms.ToArray(); }
    private static bool ConstEq(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        int d = 0; for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i]; return d == 0;
    }
}
