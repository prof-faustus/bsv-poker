using BsvPoker.Core;
using BsvPoker.Crypto;
using System.Text;

namespace BsvPoker.Tests;

/// <summary>
/// Prior-state-hash binding (audit #5): honest peers advance one canonical transcript; a same-sequence
/// action computed against a DIVERGENT history is cryptographically rejected; and a forged signature is
/// rejected. This prevents same-sequence divergent transcripts from being accepted.
/// </summary>
public static class HandTranscriptTests
{
    public static void All()
    {
        Console.WriteLine("prior-state-hash binding (transcript integrity):");
        var handId = new byte[16]; for (int i = 0; i < 16; i++) handId[i] = (byte)i;
        var p = Secp256k1.GenerateKeyPair();
        byte[] Act(string s) => Encoding.ASCII.GetBytes(s);

        T.Run("two honest peers advance to the SAME state applying the same actions", () =>
        {
            var alice = new HandTranscript(handId);
            var bob = new HandTranscript(handId);
            T.Eq(T.Hex(alice.State), T.Hex(bob.State), "same genesis state");
            var prior = alice.State;
            T.True(alice.TryApply(0, prior, Act("bet 100")), "alice applies");
            T.True(bob.TryApply(0, prior, Act("bet 100")), "bob applies the same");
            T.Eq(T.Hex(alice.State), T.Hex(bob.State), "states stay identical");
        });

        T.Run("an action bound to a WRONG prior state is rejected (divergent transcript)", () =>
        {
            var t = new HandTranscript(handId);
            t.TryApply(0, t.State, Act("check"));               // advance once
            var wrongPrior = new byte[32];                       // not the current state
            T.False(t.TryApply(1, wrongPrior, Act("bet 50")), "action on a divergent history rejected");
        });

        T.Run("a signed action verifies + applies only when bound to the current state", () =>
        {
            var t = new HandTranscript(handId);
            var prior = t.State;
            var action = Act("raise 200");
            var sig = HandTranscript.SignAction(handId, 0, prior, action, p.Priv);
            T.True(t.VerifyAndApply(handId, 0, prior, action, p.Pub, sig), "valid signed action accepted");
            // replaying the same action now fails (state has advanced — prior no longer matches)
            T.False(t.VerifyAndApply(handId, 0, prior, action, p.Pub, sig), "stale action (old prior) rejected");
        });

        T.Run("a forged signature is rejected", () =>
        {
            var t = new HandTranscript(handId);
            var prior = t.State; var action = Act("fold");
            var attacker = Secp256k1.GenerateKeyPair();
            var sig = HandTranscript.SignAction(handId, 0, prior, action, attacker.Priv);
            T.False(t.VerifyAndApply(handId, 0, prior, action, p.Pub, sig), "signature not by the claimed signer → rejected");
        });
    }
}
