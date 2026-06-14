using BsvPoker.Core;
using BsvPoker.Crypto;
using BsvPoker.Net;

namespace BsvPoker.Tests;

/// <summary>
/// FIVE DIFFERENT PLAYERS, FIVE DIFFERENT WALLETS, ONE GAME — the real test the user demanded: not "open the app
/// once", but five independent identities (each its own seed = its own wallet/identity, its own node) joining the
/// SAME table over the live P2P mesh, every player seeing ONLY their own hole cards, and the hand playing through
/// to completion with chips conserved across all five. This is the genuine multi-player path (NetGame over real
/// sockets), driven headlessly so it can be verified every run.
/// </summary>
public static class MultiPlayerGameTests
{
    private static bool Until(Func<bool> c, int ms)
    {
        var dl = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < dl) { if (c()) return true; Thread.Sleep(20); }
        return c();
    }

    public static void All()
    {
        Console.WriteLine("multi-player / multi-wallet / one game (each a distinct identity, real mesh, full hand):");
        RunTable(5);
        RunTable(6);   // full 6-max table — the largest common poker table, over the real mesh
    }

    private static void RunTable(int players)
    {
        T.Run($"{players} distinct-wallet players join one table, each sees only their own cards, the hand completes, chips conserved", () =>
        {
            int N = players;
            // DISTINCT WALLETS: each player has its own seed (a different wallet), and its identity key is
            // derived from that seed — exactly like five separate poker.exe instances, each with its own wallet.
            var seeds = Enumerable.Range(1, N).Select(i => { var s = new byte[32]; s[31] = (byte)i; s[0] = (byte)(i * 37); return s; }).ToArray();
            var ids = seeds.Select(s => { var k = WalletKeys.Account(s, 0, 0); return (Priv: k.Priv, Pub: k.Pub); }).ToArray();
            // every identity key is DISTINCT (N different players, not one)
            T.Eq(ids.Select(i => Convert.ToHexString(i.Pub)).Distinct().Count(), N, $"{N} DISTINCT player identities");

            // a full mesh of five nodes (each player's own node), like five running instances on the network
            var nodes = new P2PNode[N];
            nodes[0] = new P2PNode(0, "127.0.0.1"); nodes[0].StartAsync().Wait();
            for (int p = 1; p < N; p++)
            {
                nodes[p] = new P2PNode(0, "127.0.0.1");
                var seedsToDial = Enumerable.Range(0, p).Select(j => new P2PNode.PeerAddr("127.0.0.1", nodes[j].BoundPort)).ToArray();
                nodes[p].StartAsync(seedsToDial).Wait();
            }
            try
            {
                T.True(Until(() => nodes.All(n => n.PeerCount >= N - 1), 20000), $"all {N} nodes connect into one mesh");

                // all players JOIN THE SAME TABLE (an N-seat Texas Hold'em table), each running its own NetGame
                string table = $"t-mp{N}~TexasHoldem~p{N}~s100~b2";
                var games = new NetGame[N];
                for (int p = 0; p < N; p++) games[p] = new NetGame(nodes[p], table, ids[p].Priv, ids[p].Pub);
                foreach (var g in games) g.Start();

                // the dealerless deal completes for everyone (each player holds a real hand)
                T.True(Until(() => games.All(g => g.Hand != null), 30000), "the hand is dealt to all five players");

                // PRIVACY: each player sees ONLY their own hole cards; every opponent's holes are face-down
                foreach (var g in games)
                {
                    T.True(g.Hand!.Seats[g.MySeat].Hole.All(c => !c.IsFaceDown), $"seat {g.MySeat} sees its OWN cards");
                    for (int s = 0; s < N; s++)
                        if (s != g.MySeat)
                            T.True(g.Hand!.Seats[s].Hole.All(c => c.IsFaceDown), "an opponent's hole cards are hidden");
                }

                // chips on the table are the sum of all five buy-ins
                long expect = 100 * N;
                T.Eq(games[0].TableChips, expect, "the table holds all five players' chips");

                // play the hand to completion: each player acts (check/call/fold) on its turn
                var dl = Environment.TickCount64 + 120000;
                while (Environment.TickCount64 < dl && !games.All(g => g.HandNumber >= 1 || g.Eliminated || g.State == NetGame.Phase.Done))
                {
                    foreach (var g in games)
                    {
                        var h = g.Hand;
                        if (h == null || h.Complete || h.ToAct != g.MySeat) continue;
                        var la = h.Legal();
                        if (la.CanCheck) g.Act(ActionKind.Check, 0);
                        else if (la.CanCall) g.Act(ActionKind.Call, 0);
                        else g.Act(ActionKind.Fold, 0);
                    }
                    Thread.Sleep(10);
                }
                T.True(games.All(g => g.HandNumber >= 1 || g.Eliminated || g.State == NetGame.Phase.Done), "the hand completed for all five players");

                // CHIPS CONSERVED across all five players (every seat agrees on the same conserved total)
                foreach (var g in games) T.Eq(g.TableChips, expect, $"chips conserved on every player's view ({g.TableChips})");

                foreach (var g in games) g.Stop();
            }
            finally { foreach (var n in nodes) { try { n.Dispose(); } catch { } } }
        });
    }
}
