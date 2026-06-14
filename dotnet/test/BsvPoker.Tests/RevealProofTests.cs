using BsvPoker.Core;
using BsvPoker.Crypto;

namespace BsvPoker.Tests;

/// <summary>
/// "Prove what it is" (audit-critical): a card-scalar reveal is bound to a HIDING commitment published at
/// remask. Two properties are tested:
///  • BINDING — the SUBSTITUTION ATTACK: because the card base points Mᵢ=(i+1)·G have public ratios, a cheater
///    can forge a scalar that unmasks their real card to a DIFFERENT valid card, fooling the "is it a real
///    card?" check. The commitment defeats it: the forged scalar cannot open the commitment.
///  • HIDING — the READ-THE-HIDDEN-CARD attack: a plain C = d·G commitment would LEAK an unrevealed card,
///    because an observer who strips the public deck to X = d·M can test (j+1)·C == X for every candidate j.
///    The hiding commitment (SHA-256(d ‖ r)) makes that impossible, so hole cards stay private.
/// </summary>
public static class RevealProofTests
{
    // big-endian 32-byte scalar for a small positive integer (matches Secp256k1's card-point encoding)
    private static byte[] Enc(int v)
    {
        var b = new byte[32];
        b[31] = (byte)(v & 0xff); b[30] = (byte)((v >> 8) & 0xff);
        b[29] = (byte)((v >> 16) & 0xff); b[28] = (byte)((v >> 24) & 0xff);
        return b;
    }

    public static void All()
    {
        Console.WriteLine("reveal proof — prove-what-it-is (hiding + binding commitment):");

        T.Run("an honest reveal (scalar, nonce) opens its commitment; a wrong scalar or wrong nonce does not", () =>
        {
            var d = MentalPokerEC.NewScalar();
            var (c, r) = RevealProof.Commit(d);
            T.True(RevealProof.Verify(d, r, c), "honest (scalar, nonce) opens the commitment");
            var other = MentalPokerEC.NewScalar();
            T.False(RevealProof.Verify(other, r, c), "a different scalar does NOT open the commitment");
            T.False(RevealProof.Verify(d, MentalPokerEC.NewScalar(), c), "the right scalar with the WRONG nonce does NOT open it");
        });

        T.Run("BINDING — SUBSTITUTION ATTACK: a forged scalar fakes a different VALID card but cannot open the commitment", () =>
        {
            const int n = 52, m = 3, mPrime = 17;     // true card index 3, the card the cheater wants to show: 17
            var d = MentalPokerEC.NewScalar();
            var (commit, r) = RevealProof.Commit(d);   // published at remask (before any open)

            var P = Secp256k1.PointMul(Secp256k1.CardBasePoint(m), d);   // the final masked point the cheater holds: P = d·M_m
            T.Eq(MentalPokerEC.Identify(MentalPokerEC.Unmask(P, new[] { d }), n), m, "honest open yields the true card");
            T.True(RevealProof.Verify(d, r, commit), "honest open passes the commitment check");

            // forged scalar d' = d·(m+1)·(m'+1)⁻¹  ⇒  stripping d' turns P into M_{m'} (a DIFFERENT real card)
            var dPrime = Secp256k1.ScalarMulModN(Secp256k1.ScalarMulModN(d, Enc(m + 1)), Secp256k1.ScalarInverse(Enc(mPrime + 1)));
            T.Eq(MentalPokerEC.Identify(MentalPokerEC.Unmask(P, new[] { dPrime }), n), mPrime, "the forgery DOES unmask to a valid, different card (Identify alone is fooled)");
            // …but no nonce lets the forged scalar open the commitment (binding by collision resistance).
            T.False(RevealProof.Verify(dPrime, r, commit), "the commitment REJECTS the substituted reveal (binding holds)");
        });

        T.Run("HIDING — read-the-hidden-card attack: works against a plain d·G commitment, defeated by the hiding one", () =>
        {
            const int n = 52, hidden = 11;             // a card meant to stay secret (an opponent's hole card)
            var d = MentalPokerEC.NewScalar();         // the recipient's secret scalar for this position (NOT revealed)

            // The observer strips the public deck down to X = d·M_hidden (everyone else's scalars are public),
            // but does NOT know d. The question: can the published commitment let them read the card?
            var X = Secp256k1.PointMul(Secp256k1.CardBasePoint(hidden), d);

            // (a) AGAINST A PLAIN d·G COMMITMENT — the attack SUCCEEDS: X = (hidden+1)·(d·G), so the observer
            //     tests (j+1)·C == X for each candidate and finds the card. This is the leak we removed.
            var cDg = Secp256k1.PublicKeyCompressed(d);   // C = d·G (the OLD, vulnerable commitment)
            int leakedFromDg = -1;
            for (int j = 0; j < n; j++)
                if (Secp256k1.PointMul(cDg, Enc(j + 1)).AsSpan().SequenceEqual(X)) { leakedFromDg = j; break; }
            T.Eq(leakedFromDg, hidden, "a d·G commitment LEAKS the hidden card (this is why it was replaced)");

            // (b) AGAINST THE HIDING COMMITMENT — the attack FAILS: it is SHA-256(d ‖ r), unrelated to any curve
            //     point, so there is no (j+1)·C relation to exploit and no candidate is confirmable without d.
            var (cHide, _) = RevealProof.Commit(d);
            T.Eq(cHide.Length, 32, "the hiding commitment is a hash, not a curve point");
            T.True(!cHide.AsSpan().SequenceEqual(cDg), "the hiding commitment is not d·G");

            // sanity: the legitimate recipient (who has d) still reads the card
            T.Eq(MentalPokerEC.Identify(MentalPokerEC.Unmask(X, new[] { d }), n), hidden, "the recipient with d still reads the card");
        });

        T.Run("fail-closed: malformed scalar / nonce / commitment is rejected, never throws", () =>
        {
            var d = MentalPokerEC.NewScalar();
            var (c, r) = RevealProof.Commit(d);
            T.False(RevealProof.Verify(new byte[32], r, c), "an all-zero (invalid) scalar is rejected");
            T.False(RevealProof.Verify(d, r, new byte[33]), "a wrong-length commitment is rejected");
            T.False(RevealProof.Verify(new byte[5], r, c), "a wrong-length scalar is rejected");
            T.False(RevealProof.Verify(d, new byte[5], c), "a wrong-length nonce is rejected");
            T.False(RevealProof.Verify(d, r, new byte[10]), "a short commitment is rejected");
        });
    }
}
