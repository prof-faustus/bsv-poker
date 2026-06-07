using System.Buffers.Binary;
using BsvPoker.Crypto;

namespace BsvPoker.Core;

/// <summary>
/// Prior-state-hash binding of game actions (audit #5). A hand keeps a rolling transcript STATE hash; every
/// action is signed over {handId, sequence, priorStateHash, action} and is only accepted if its
/// priorStateHash equals the verifier's current transcript state. This makes two same-sequence but divergent
/// transcripts cryptographically distinguishable: an action computed against a different history will not
/// match the current state and is rejected. After acceptance the state advances deterministically, so all
/// honest peers stay on one canonical transcript.
/// </summary>
public sealed class HandTranscript
{
    private static readonly byte[] Domain = System.Text.Encoding.ASCII.GetBytes("bsvpoker-transcript-v1");
    private byte[] _state;

    public HandTranscript(byte[] handId) { _state = Hashes.Sha256(Concat(Domain, handId)); }

    /// <summary>The current transcript state hash (what the next action must bind to).</summary>
    public byte[] State => (byte[])_state.Clone();

    /// <summary>The digest a player signs for an action taken at <paramref name="priorState"/>.</summary>
    public static byte[] ActionDigest(byte[] handId, int seq, byte[] priorState, byte[] action)
        => Hashes.Sha256(Concat(Domain, handId, Int(seq), priorState, action));

    /// <summary>
    /// Apply an action bound to <paramref name="priorState"/>. Returns false (and does NOT advance) if the
    /// action's prior state does not match the current transcript — i.e. it was computed on a divergent history.
    /// </summary>
    public bool TryApply(int seq, byte[] priorState, byte[] action)
    {
        if (priorState == null || !priorState.AsSpan().SequenceEqual(_state)) return false;
        _state = Hashes.Sha256(Concat(Domain, _state, Int(seq), action));
        return true;
    }

    /// <summary>Verify a signed action: the signature is valid AND it is bound to the current transcript state.</summary>
    public bool VerifyAndApply(byte[] handId, int seq, byte[] priorState, byte[] action, byte[] signerPub33, byte[] sig64)
    {
        var digest = ActionDigest(handId, seq, priorState, action);
        if (!Secp256k1.VerifyDigest(signerPub33, digest, sig64)) return false;
        return TryApply(seq, priorState, action);
    }

    /// <summary>Sign an action bound to the given prior state (the local player's signed move).</summary>
    public static byte[] SignAction(byte[] handId, int seq, byte[] priorState, byte[] action, byte[] signerPriv32)
        => Secp256k1.SignDigest(signerPriv32, ActionDigest(handId, seq, priorState, action));

    private static byte[] Int(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); return b; }
    private static byte[] Concat(params byte[][] parts) { var ms = new List<byte>(); foreach (var p in parts) ms.AddRange(p); return ms.ToArray(); }
}
