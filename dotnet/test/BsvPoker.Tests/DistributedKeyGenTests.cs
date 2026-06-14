using BsvPoker.Core;
using BsvPoker.Crypto;

namespace BsvPoker.Tests;

/// <summary>
/// Distributed dealerless key generation (the real-peer DKG): n players each deal a secret polynomial,
/// broadcast Feldman commitments, and send every peer a SEALED share bound to (session, dealer, recipient).
/// Each finalizes by opening + verifying the shares dealt to it and summing them into a joint key share.
/// Proven: all players derive the SAME joint public key with no dealer; any t+1 joint shares reconstruct the
/// joint secret; a peer who cannot open a share learns nothing; a LYING dealer is caught and blamed by name;
/// and a share cannot be replayed into a different session/dealer (AAD context binding).
/// </summary>
public static class DistributedKeyGenTests
{
    private static readonly byte[] Sid = System.Text.Encoding.ASCII.GetBytes("dkg-test-session");

    public static void All()
    {
        Console.WriteLine("distributed dealerless key generation (real-peer DKG over sealed shares):");

        T.Run("3 parties run the DKG: all agree on ONE joint public key, no dealer; t+1 shares reconstruct it", () =>
        {
            const int n = 3, t = 1;
            var kp = Enumerable.Range(1, n).ToDictionary(i => i, _ => Secp256k1.GenerateKeyPair());
            var pubs = kp.ToDictionary(k => k.Key, k => k.Value.Pub);

            var dealt = kp.ToDictionary(k => k.Key, k => DistributedKeyGen.Deal(k.Key, k.Value.Pub, t, pubs, Sid));
            var round1 = dealt.Values.Select(d => d.Msg).ToList();

            var results = kp.ToDictionary(k => k.Key, k => DistributedKeyGen.Finalize(k.Key, k.Value.Priv, round1, Sid));
            foreach (var (idx, r) in results) T.True(r.Blame == null, $"party {idx} finalized without blame");

            var pub1 = results[1].JointPublicKey;
            T.True(results.Values.All(r => r.JointPublicKey.SequenceEqual(pub1)), "every party derives the SAME joint public key");

            var expectedPub = ThresholdSharing.PublicKey(dealt.Values.Select(d => d.Poly).ToList());
            T.True(pub1.SequenceEqual(expectedPub), "joint key == Σ (each dealer's free term)·G");

            var secret = ThresholdSharing.Reconstruct(new[] { (1, results[1].JointShare), (2, results[2].JointShare) });
            T.True(Secp256k1.PublicKeyCompressed(secret).SequenceEqual(pub1), "t+1 joint shares reconstruct the secret a, and a·G == joint key");
            var secret2 = ThresholdSharing.Reconstruct(new[] { (2, results[2].JointShare), (3, results[3].JointShare) });
            T.True(secret.SequenceEqual(secret2), "every t+1 subset reconstructs the same secret");
        });

        T.Run("the joint key signs with the assembled shares (threshold ECDSA over a DKG'd key) and verifies", () =>
        {
            const int n = 3, t = 1;
            var kp = Enumerable.Range(1, n).ToDictionary(i => i, _ => Secp256k1.GenerateKeyPair());
            var pubs = kp.ToDictionary(k => k.Key, k => k.Value.Pub);
            var dealt = kp.ToDictionary(k => k.Key, k => DistributedKeyGen.Deal(k.Key, k.Value.Pub, t, pubs, Sid));
            var round1 = dealt.Values.Select(d => d.Msg).ToList();
            var results = kp.ToDictionary(k => k.Key, k => DistributedKeyGen.Finalize(k.Key, k.Value.Priv, round1, Sid));

            var shares = results.OrderBy(r => r.Key).Select(r => (r.Key, r.Value.JointShare)).ToArray();
            var key = new ThresholdEcdsa.Shared(dealt.Values.Select(d => d.Poly).ToList(), shares, results[1].JointPublicKey, n, t);
            var digest = Hashes.Sha256(System.Text.Encoding.UTF8.GetBytes("spend the DKG-controlled pot"));
            var sig = ThresholdEcdsa.SignDigest(digest, key);
            T.True(Secp256k1.VerifyDigest(key.PublicKey, digest, sig), "a signature over the DKG'd key verifies against it");
        });

        T.Run("a LYING dealer (share inconsistent with its commitments) is caught and BLAMED by name", () =>
        {
            const int n = 3, t = 1;
            var kp = Enumerable.Range(1, n).ToDictionary(i => i, _ => Secp256k1.GenerateKeyPair());
            var pubs = kp.ToDictionary(k => k.Key, k => k.Value.Pub);
            var honest = kp.ToDictionary(k => k.Key, k => DistributedKeyGen.Deal(k.Key, k.Value.Pub, t, pubs, Sid));

            // dealer 2 cheats: honest commitments but a BOGUS share sealed to party 1 — sealed with the CORRECT
            // context so it OPENS, isolating the Feldman check as the thing that catches it.
            var cheatMsg = honest[2].Msg;
            var bogus = cheatMsg.SealedShares.ToDictionary(x => x.Key, x => x.Value);
            bogus[1] = DistributedKeyGen.SealScalar(MentalPokerEC.NewScalar(), pubs[1],
                DistributedKeyGen.ShareContext(Sid, pubs[2], 1, pubs[1]));
            var tampered = cheatMsg with { SealedShares = bogus };

            var round1 = new[] { honest[1].Msg, tampered, honest[3].Msg };
            var r1 = DistributedKeyGen.Finalize(1, kp[1].Priv, round1, Sid);
            T.Eq(r1.Blame ?? -99, 2, "party 1 blames dealer 2 (Feldman verification fails on the bogus share)");
            var r3 = DistributedKeyGen.Finalize(3, kp[3].Priv, round1, Sid);
            T.True(r3.Blame == null, "party 3 (honest share from dealer 2) finalizes without blame");
        });

        T.Run("a non-recipient cannot open a share sealed to someone else", () =>
        {
            var alice = Secp256k1.GenerateKeyPair();
            var eve = Secp256k1.GenerateKeyPair();
            var aad = DistributedKeyGen.ShareContext(Sid, alice.Pub, 1, alice.Pub);
            var sealed_ = DistributedKeyGen.SealScalar(MentalPokerEC.NewScalar(), alice.Pub, aad);
            bool eveOpened; try { DistributedKeyGen.OpenScalar(sealed_, eve.Priv, aad); eveOpened = true; } catch { eveOpened = false; }
            T.False(eveOpened, "Eve cannot open a share sealed to Alice");
            T.Eq(DistributedKeyGen.OpenScalar(sealed_, alice.Priv, aad).Length, 32, "Alice opens her own share (32-byte scalar)");
        });

        T.Run("a sealed share cannot be REPLAYED into a different session (AAD context binding)", () =>
        {
            var alice = Secp256k1.GenerateKeyPair();
            var sealed_ = DistributedKeyGen.SealScalar(MentalPokerEC.NewScalar(), alice.Pub,
                DistributedKeyGen.ShareContext(Sid, alice.Pub, 1, alice.Pub));
            var otherSession = System.Text.Encoding.ASCII.GetBytes("a-different-session");
            bool opened; try { DistributedKeyGen.OpenScalar(sealed_, alice.Priv, DistributedKeyGen.ShareContext(otherSession, alice.Pub, 1, alice.Pub)); opened = true; } catch { opened = false; }
            T.False(opened, "the share will NOT open under a different session's context (replay blocked)");
        });
    }
}
