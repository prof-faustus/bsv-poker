using BsvPoker.Core;
using BsvPoker.Crypto;
using System.Security.Cryptography;

namespace BsvPoker.Tests;

/// <summary>
/// Anti-grinding seat assignment (audit #4): seats come from commit-reveal JOINT randomness, not sorted
/// pubkeys. All players derive the same order; the order depends on every nonce (so it can't be ground by
/// key choice); and a reveal that doesn't match its commitment is rejected.
/// </summary>
public static class SeatOrderTests
{
    public static void All()
    {
        Console.WriteLine("anti-grinding seat order (commit-reveal joint randomness):");
        var pubs = Enumerable.Range(0, 5).Select(_ => Secp256k1.GenerateKeyPair().Pub).ToList();
        var nonces = pubs.Select(_ => RandomNumberGenerator.GetBytes(32)).ToList();
        var reveals = pubs.Zip(nonces, (p, n) => (p, n)).ToList();

        T.Run("all players compute the SAME seat order from the joint seed", () =>
        {
            var seed = SeatOrder.JointSeed(reveals);
            var seed2 = SeatOrder.JointSeed(reveals.AsEnumerable().Reverse().ToList()); // order-independent
            T.Eq(T.Hex(seed), T.Hex(seed2), "joint seed is order-independent");
            var order = SeatOrder.Assign(pubs, seed);
            T.Eq(order.Distinct().Count(), pubs.Count, "every seat assigned exactly once (a permutation)");
        });

        T.Run("changing ANY player's nonce changes the joint seed (no single party controls it)", () =>
        {
            var seedA = SeatOrder.JointSeed(reveals);
            var altered = reveals.ToList(); altered[2] = (altered[2].p, RandomNumberGenerator.GetBytes(32));
            var seedB = SeatOrder.JointSeed(altered);
            T.False(T.Hex(seedA) == T.Hex(seedB), "one changed nonce changes the whole seed");
        });

        T.Run("a player cannot fix their seat: same pub lands in different seats under different joint seeds", () =>
        {
            var target = pubs[0];
            int seatUnderSeed(byte[] seed) => Array.IndexOf(SeatOrder.Assign(pubs, seed), 0);
            var seats = new HashSet<int>();
            for (int i = 0; i < 8; i++)
            {
                var rs = pubs.Select(_ => RandomNumberGenerator.GetBytes(32)).ToList();
                seats.Add(seatUnderSeed(SeatOrder.JointSeed(pubs.Zip(rs, (p, n) => (p, n)).ToList())));
            }
            T.True(seats.Count > 1, "the same pubkey lands in different seats as the joint randomness varies (cannot grind a seat)");
        });

        T.Run("a reveal that doesn't match its commitment is rejected", () =>
        {
            var nonce = RandomNumberGenerator.GetBytes(32);
            var commit = SeatOrder.Commit(nonce);
            T.True(SeatOrder.VerifyReveal(commit, nonce), "honest reveal accepted");
            T.False(SeatOrder.VerifyReveal(commit, RandomNumberGenerator.GetBytes(32)), "wrong nonce rejected");
        });
    }
}
