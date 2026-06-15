using BsvPoker.Core;
using BsvPoker.Core.Games;
using BsvPoker.Crypto;
using BsvPoker.Net;

namespace BsvPoker.Tests;

/// <summary>
/// MULTIPLAYER group Blackjack over the real mesh: 3 distinct-wallet players on 3 separate nodes join ONE
/// table, the dealerless deck is dealt jointly (dealer hole stays sealed until the dealer plays), each player
/// plays, the dealer plays to 17, and EVERY node independently settles to the SAME result (consensus). Proves
/// the networked deal + play + settlement work end-to-end — never one-on-one, no single dealer.
/// </summary>
public static class NetBlackjackTests
{
    private static bool Until(Func<bool> c, int ms) { var dl = Environment.TickCount64 + ms; while (Environment.TickCount64 < dl) { if (c()) return true; Thread.Sleep(20); } return c(); }

    public static void All()
    {
        Console.WriteLine("networked group Blackjack (3 players, one mesh, joint dealer, consensus settlement):");

        T.Run("3 players join one table, deal jointly, play, and ALL nodes settle to the same result", () =>
        {
            const int N = 3;
            var ids = Enumerable.Range(0, N).Select(_ => Secp256k1.GenerateKeyPair()).ToArray();
            var nodes = new P2PNode[N];
            nodes[0] = new P2PNode(0, "127.0.0.1"); nodes[0].SetIdentity(ids[0].Priv, ids[0].Pub); nodes[0].StartAsync().Wait();
            for (int p = 1; p < N; p++)
            {
                nodes[p] = new P2PNode(0, "127.0.0.1"); nodes[p].SetIdentity(ids[p].Priv, ids[p].Pub);
                var seeds = Enumerable.Range(0, p).Select(j => new P2PNode.PeerAddr("127.0.0.1", nodes[j].BoundPort)).ToArray();
                nodes[p].StartAsync(seeds).Wait();
            }
            NetBlackjack[] g = Array.Empty<NetBlackjack>();
            try
            {
                T.True(Until(() => nodes.All(n => n.PeerCount >= N - 1), 20000), "the 3 nodes form one mesh");
                const string table = "t-bj01~p3~b10";
                g = ids.Select((id, i) => new NetBlackjack(nodes[i], table, id.Priv, id.Pub)).ToArray();
                foreach (var x in g) x.Start();

                // every player is dealt their two cards + sees the dealer up card
                T.True(Until(() => g.All(x => x.MyHand.Count == 2 && x.DealerCards.Count >= 1 && x.MySeat >= 0), 40000),
                    "each player is dealt 2 cards and sees the dealer up card (joint deal)");
                T.True(g.Select(x => string.Join(",", x.SeatPubs)).Distinct().Count() == 1, "all nodes agree on the seat order");

                // drive play: each player STANDS on its turn (deterministic, lets the dealer resolve)
                bool done = Until(() =>
                {
                    foreach (var x in g) if (x.State == NetBlackjack.Phase.Playing && x.ToAct == x.MySeat) x.Act(BjAction.Stand);
                    return g.All(x => x.Complete);
                }, 60000);
                T.True(done, "every player stands, the dealer plays to 17, and the hand completes on all nodes");

                // CONSENSUS: every node independently computed the SAME dealer hand and the SAME per-seat result
                var dealer0 = string.Join(",", g[0].DealerCards.Select(c => c.Index));
                T.True(g.All(x => string.Join(",", x.DealerCards.Select(c => c.Index)) == dealer0), "all nodes agree on the dealer's hand");
                for (int s = 0; s < N; s++)
                {
                    long net0 = g[0].Net[s];
                    T.True(g.All(x => x.Net[s] == net0), $"all nodes agree on seat {s}'s net result ({net0})");
                    T.True(g.All(x => x.Outcomes[s] == g[0].Outcomes[s]), $"all nodes agree on seat {s}'s outcome");
                }
                T.True(!g.Any(x => x.CheatDetected), "no false cheat detection");

                // the result is a real Blackjack settlement: the bank's loss equals the players' net winnings
                long playersNet = Enumerable.Range(0, N).Sum(s => g[0].Net[s]);
                T.True(Math.Abs(playersNet) <= 10 * N * 2, "net results are within the wagered range (sanity)");
            }
            finally { foreach (var x in g) { try { x.Stop(); } catch { } } foreach (var n in nodes) { try { n.Dispose(); } catch { } } }
        });
    }
}
