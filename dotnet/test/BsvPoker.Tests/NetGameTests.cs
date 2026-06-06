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
            var g1 = new NetGame(nodeA, "table-1", pa.Priv, pa.Pub); var g2 = new NetGame(nodeB, "table-1", pb.Priv, pb.Pub);
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
            T.True(g1.HandLog.Count >= 1, "completed hands are logged");
            T.True(g1.Standings.Contains("(you)"), "standings mark the local player");
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
            var games = new[] { new NetGame(a, table, ka.Priv, ka.Pub), new NetGame(b, table, kb.Priv, kb.Pub), new NetGame(c, table, kc.Priv, kc.Pub) };
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

        T.Run("HOSTILE: forged/unsigned game messages are rejected and cannot corrupt the session", () =>
        {
            using var nodeA = new P2PNode(0, "127.0.0.1");
            using var nodeB = new P2PNode(0, "127.0.0.1");
            using var atk = new P2PNode(0, "127.0.0.1");
            nodeA.StartAsync().Wait();
            nodeB.StartAsync(new[] { new P2PNode.PeerAddr("127.0.0.1", nodeA.BoundPort) }).Wait();
            atk.StartAsync(new[] { new P2PNode.PeerAddr("127.0.0.1", nodeA.BoundPort) }).Wait();
            T.True(Until(() => nodeA.PeerCount >= 2 && nodeB.PeerCount >= 1), "victims + attacker connected");

            const string table = "t-forge~TexasHoldem~p2";
            var pa = Secp256k1.GenerateKeyPair(); var pb = Secp256k1.GenerateKeyPair(); var pe = Secp256k1.GenerateKeyPair();
            var g1 = new NetGame(nodeA, table, pa.Priv, pa.Pub); var g2 = new NetGame(nodeB, table, pb.Priv, pb.Pub);
            g1.Start(); g2.Start();
            T.True(Until(() => g1.Hand != null && g2.Hand != null), "victims dealt a hand");

            // attacker floods the table with forged "act" frames: unsigned, and signed by a non-seat key
            var atkPubHex = Convert.ToHexString(pe.Pub).ToLowerInvariant();
            var unsigned = $"{{\"t\":\"act\",\"h\":0,\"seat\":0,\"seq\":0,\"kind\":\"Fold\",\"amount\":0}}";
            var badSig = $"{{\"t\":\"act\",\"h\":0,\"seat\":0,\"seq\":0,\"kind\":\"Fold\",\"amount\":0,\"pub\":\"{atkPubHex}\",\"sig\":\"{new string('0', 128)}\"}}";
            for (int i = 0; i < 6; i++)
            {
                atk.PublishAsync(table, System.Text.Encoding.UTF8.GetBytes(unsigned)).Wait();
                atk.PublishAsync(table, System.Text.Encoding.UTF8.GetBytes(badSig)).Wait();
                Thread.Sleep(40);
            }
            T.True(Until(() => g1.Rejected > 0 && g2.Rejected > 0, 6000), "both victims rejected the forgeries");
            // the forged folds were NOT applied and the session is intact
            T.False(g1.Eliminated || g2.Eliminated, "no victim was knocked out by a forgery");
            T.Eq(g1.TableChips, 200L, "chips intact after the attack");
            g1.Stop(); g2.Stop();
        });

        T.Run("accountable abort: a stalled deal times out instead of hanging forever", () =>
        {
            using var nA = new P2PNode(0, "127.0.0.1");
            using var nB = new P2PNode(0, "127.0.0.1");
            nA.StartAsync().Wait();
            nB.StartAsync(new[] { new P2PNode.PeerAddr("127.0.0.1", nA.BoundPort) }).Wait();
            T.True(Until(() => nA.PeerCount >= 1 && nB.PeerCount >= 1), "connected");
            var pa = Secp256k1.GenerateKeyPair(); var pb = Secp256k1.GenerateKeyPair();
            var g1 = new NetGame(nA, "t-stall~TexasHoldem~p2", pa.Priv, pa.Pub) { AbortMs = 2000 };
            var g2 = new NetGame(nB, "t-stall~TexasHoldem~p2", pb.Priv, pb.Pub) { AbortMs = 2000 };
            g1.Start(); g2.Start();
            T.True(Until(() => g1.MySeat >= 0, 8000), "g1 seated and began dealing");
            g2.Stop(); // the opponent vanishes mid-deal
            T.True(Until(() => g1.Aborted, 12000), "g1 aborts the stalled deal rather than hanging");
            g1.Stop();
        });

        T.Run("configurable stakes: the table id sets buy-in and blinds", () =>
        {
            using var nodeA = new P2PNode(0, "127.0.0.1");
            using var nodeB = new P2PNode(0, "127.0.0.1");
            nodeA.StartAsync().Wait();
            nodeB.StartAsync(new[] { new P2PNode.PeerAddr("127.0.0.1", nodeA.BoundPort) }).Wait();
            T.True(Until(() => nodeA.PeerCount >= 1 && nodeB.PeerCount >= 1), "nodes connected");

            const string table = "t-hi~TexasHoldem~p2~s500~b10";
            var pa = Secp256k1.GenerateKeyPair(); var pb = Secp256k1.GenerateKeyPair();
            var g1 = new NetGame(nodeA, table, pa.Priv, pa.Pub); var g2 = new NetGame(nodeB, table, pb.Priv, pb.Pub);
            g1.Start(); g2.Start();

            T.True(Until(() => g1.Hand != null && g2.Hand != null), "dealt at the custom stakes");
            T.Eq(g1.TableChips, 1000L, "buy-in 500 × 2 players = 1000 chips");
            T.Eq(g1.Hand!.BigBlind, 10L, "big blind from the table id");
            T.Eq(g1.Hand!.SmallBlind, 5L, "small blind = half the big blind");
            g1.Stop(); g2.Stop();
        });
    }
}
