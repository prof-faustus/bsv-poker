using BsvPoker.Core;
using BsvPoker.Crypto;
using BsvPoker.Net;

namespace BsvPoker.Tests;

public static class NetGameTests
{
    private static bool Until(Func<bool> c, int ms = 30000)
    {
        var dl = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < dl) { if (c()) return true; Thread.Sleep(30); }
        return c();
    }

    // play check/call for whichever game it is the local player's turn, until a condition holds
    private static void PlayUntil(NetGame[] games, Func<bool> done, int ms)
    {
        var dl = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < dl && !done())
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
    }

    public static void All()
    {
        Console.WriteLine("networked poker session (private deal, multi-hand continuity, no server):");

        T.Run("TWO players: private deal; chips conserved across multiple hands (stacks carry, button rotates)", () =>
        {
            using var nodeA = new P2PNode(0, "127.0.0.1");
            using var nodeB = new P2PNode(0, "127.0.0.1");
            nodeA.StartAsync().Wait();
            nodeB.StartAsync(new[] { new P2PNode.PeerAddr("127.0.0.1", nodeA.BoundPort) }).Wait();
            T.True(Until(() => nodeA.PeerCount >= 1 && nodeB.PeerCount >= 1), "nodes connected");

            var pa = Secp256k1.GenerateKeyPair(); var pb = Secp256k1.GenerateKeyPair();
            var g1 = new NetGame(nodeA, "table-1", pa.Pub); var g2 = new NetGame(nodeB, "table-1", pb.Pub);
            var games = new[] { g1, g2 };
            foreach (var g in games) g.Start();

            T.True(Until(() => g1.Hand != null && g2.Hand != null), "first hand dealt");
            foreach (var g in games)
            {
                T.True(g.Hand!.Seats[g.MySeat].Hole.All(x => !x.IsFaceDown), "I know my own holes");
                T.True(g.Hand!.Seats[1 - g.MySeat].Hole.All(x => x.IsFaceDown), "opponent holes hidden during play");
            }
            T.Eq(g1.TableChips, 200L, "session starts with 200 chips");

            // play several hands; the session keeps re-dealing with carried stacks
            PlayUntil(games, () => g1.HandNumber >= 3 && g2.HandNumber >= 3, 60000);
            T.True(g1.HandNumber >= 3 && g2.HandNumber >= 3, "at least 3 hands were played (continuity)");
            T.Eq(g1.TableChips, 200L, "chips conserved across hands on A");
            T.Eq(g2.TableChips, 200L, "chips conserved across hands on B");
            foreach (var g in games) g.Stop();
        });

        T.Run("THREE players: private multiway deal; chips conserved across multiple hands", () =>
        {
            using var a = new P2PNode(0, "127.0.0.1");
            using var b = new P2PNode(0, "127.0.0.1");
            using var c = new P2PNode(0, "127.0.0.1");
            a.StartAsync().Wait();
            b.StartAsync(new[] { new P2PNode.PeerAddr("127.0.0.1", a.BoundPort) }).Wait();
            c.StartAsync(new[] { new P2PNode.PeerAddr("127.0.0.1", a.BoundPort) }).Wait();
            T.True(Until(() => a.PeerCount >= 2 && b.PeerCount >= 1 && c.PeerCount >= 1), "three nodes connected via A");

            const string table = "t-tri~TexasHoldem~p3";
            var ka = Secp256k1.GenerateKeyPair(); var kb = Secp256k1.GenerateKeyPair(); var kc = Secp256k1.GenerateKeyPair();
            var games = new[] { new NetGame(a, table, ka.Pub), new NetGame(b, table, kb.Pub), new NetGame(c, table, kc.Pub) };
            foreach (var g in games) g.Start();

            T.True(Until(() => games.All(g => g.Hand != null)), "first hand dealt to all three");
            foreach (var g in games)
            {
                T.True(g.Hand!.Seats[g.MySeat].Hole.All(x => !x.IsFaceDown), "I know my own holes");
                foreach (var s in g.Hand!.Seats.Where(s => s.Seat != g.MySeat))
                    T.True(s.Hole.All(x => x.IsFaceDown), "every opponent's holes hidden during play");
            }
            T.Eq(games[0].TableChips, 300L, "session starts with 300 chips");

            PlayUntil(games, () => games.All(g => g.HandNumber >= 2), 90000);
            T.True(games.All(g => g.HandNumber >= 2), "at least 2 multiway hands were played");
            foreach (var g in games) T.Eq(g.TableChips, 300L, "chips conserved across hands");
            foreach (var g in games) g.Stop();
        });
    }
}
