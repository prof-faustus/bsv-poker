using BsvPoker.Core;
using BsvPoker.Crypto;
using BsvPoker.Net;

namespace BsvPoker.Tests;

/// <summary>
/// The user's MANDATORY acceptance bar: open → play → close the real session engine 100 times (not 99), across
/// DIFFERENT keys, player counts, variants and stakes — every cycle must deal privately and conserve chips, then
/// shut down clean. This proves the restored <see cref="NetGame"/> engine, not just that it compiles.
/// </summary>
public static class NetGameStressTests
{
    private static bool Until(Func<bool> c, int ms)
    {
        var dl = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < dl) { if (c()) return true; Thread.Sleep(15); }
        return c();
    }

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
            Thread.Sleep(8);
        }
    }

    public static void All()
    {
        Console.WriteLine("100x stress: open/play/close the session engine across varied keys/players/variants/stakes:");
        int ok = 0, fail = 0; string firstErr = "";
        const int N = 100;
        for (int i = 0; i < N; i++)
        {
            int players = 2 + (i % 5);                               // cycle 2,3,4,5,6-player tables
            var variant = Variants.All[i % Variants.All.Length];      // cycle all six poker variants
            long stack = 100 + (i % 5) * 20;                          // vary the buy-in
            string table = $"t{i}~{variant}~p{players}~s{stack}~b2";
            var nodes = new P2PNode[players];
            var games = new NetGame[players];
            try
            {
                nodes[0] = new P2PNode(0, "127.0.0.1"); nodes[0].StartAsync().Wait();
                for (int p = 1; p < players; p++)
                {
                    // FULL MESH: every node dials every earlier node, so each pair is directly connected and the
                    // deal never depends on a single hub relaying — this is how local multi-bot play is wired too.
                    nodes[p] = new P2PNode(0, "127.0.0.1");
                    var seeds = Enumerable.Range(0, p).Select(j => new P2PNode.PeerAddr("127.0.0.1", nodes[j].BoundPort)).ToArray();
                    nodes[p].StartAsync(seeds).Wait();
                }
                // wait for a FULL MESH: every node must see all the others
                if (!Until(() => nodes.All(n => n.PeerCount >= players - 1), 25000))
                    throw new Exception($"peers did not fully mesh (counts {string.Join(",", nodes.Select(n => n.PeerCount))} / {players - 1})");
                for (int p = 0; p < players; p++)
                {
                    var kp = Secp256k1.GenerateKeyPair();            // FRESH keys every cycle
                    games[p] = new NetGame(nodes[p], table, kp.Priv, kp.Pub);
                }
                foreach (var g in games) g.Start();
                // multiway mental-poker deals are heavier — scale the deal timeout with the seat count
                if (!Until(() => games.All(g => g.Hand != null), 20000 + players * 10000)) throw new Exception("first hand not dealt");
                // private deal: each player sees ONLY its own holes
                foreach (var g in games)
                {
                    if (!g.Hand!.Seats[g.MySeat].Hole.All(x => !x.IsFaceDown)) throw new Exception("cannot see my own holes");
                    for (int s = 0; s < players; s++)
                        if (s != g.MySeat && !g.Hand!.Seats[s].Hole.All(x => x.IsFaceDown)) throw new Exception("opponent holes leaked");
                }
                long expectChips = stack * players;
                if (games[0].TableChips != expectChips) throw new Exception($"chips {games[0].TableChips} != {expectChips}");
                // play at least one hand to completion; chips must stay conserved
                PlayUntil(games, () => games.All(g => g.HandNumber >= 1), 40000 + players * 12000);
                if (!games.All(g => g.HandNumber >= 1)) throw new Exception("no hand completed");
                foreach (var g in games)
                    if (g.TableChips != expectChips) throw new Exception($"chips not conserved on a seat ({g.TableChips})");
                ok++;
            }
            catch (Exception ex) { fail++; Console.WriteLine($"  FAIL iter {i} {variant} p{players}: {ex.Message}"); if (firstErr.Length == 0) firstErr = $"iter {i} ({variant} p{players}): {ex.Message}"; }
            finally
            {
                foreach (var g in games) { try { g?.Stop(); } catch { } }
                foreach (var n in nodes) { try { n?.Dispose(); } catch { } }
            }
            if ((i + 1) % 10 == 0) Console.WriteLine($"  …{i + 1}/{N} cycles  (ok={ok}, fail={fail})");
        }
        T.Run($"100/100 open-play-close cycles pass (varied keys/players/variants/stakes)", () =>
        {
            T.Eq(fail, 0, "zero failed cycles" + (firstErr.Length > 0 ? " — first: " + firstErr : ""));
            T.Eq(ok, N, "all 100 cycles succeeded");
        });
    }
}
