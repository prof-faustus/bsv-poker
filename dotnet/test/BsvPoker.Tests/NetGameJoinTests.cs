using BsvPoker.Core;
using BsvPoker.Crypto;
using BsvPoker.Net;

namespace BsvPoker.Tests;

/// <summary>
/// REGRESSION: a host who is ALREADY hosting (its game beacons "hello" before the other player's game subscribes
/// to the table) must still pair up — the game must START with two players. This guards the dedup bug where the
/// host's stable-id hello was marked "seen" on the joiner's node before there was a subscriber, so every re-flood
/// was discarded and the joiner never learned the host existed (host stuck "Dealing", joiner stuck "Waiting 1/2").
/// Presence ("who's online") is verified too: each node sees the other's signed @handle + reachable endpoint.
/// </summary>
public static class NetGameJoinTests
{
    private static bool Until(Func<bool> c, int ms)
    {
        var dl = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < dl) { if (c()) return true; Thread.Sleep(25); }
        return c();
    }

    private static void Drive(NetGame g)
    {
        var h = g.Hand;
        if (h == null || h.Complete || g.MySeat < 0 || h.ToAct != g.MySeat) return;
        var la = h.Legal();
        if (la.CanCheck) g.Act(ActionKind.Check, 0);
        else if (la.CanCall) g.Act(ActionKind.Call, la.CallAmount);
        else g.Act(ActionKind.Fold, 0);
    }

    public static void All()
    {
        Console.WriteLine("networked join: a host already hosting still pairs up and the game STARTS (hello-dedup regression):");

        T.Run("host-then-join (host beacons first): two players are seated, dealt, and play to a result", () =>
        {
            var ka = Secp256k1.GenerateKeyPair();
            var kb = Secp256k1.GenerateKeyPair();
            var a = new P2PNode(0, "127.0.0.1"); a.SetIdentity(ka.Priv, ka.Pub); a.StartAsync().Wait();
            var b = new P2PNode(0, "127.0.0.1"); b.SetIdentity(kb.Priv, kb.Pub); b.StartAsync().Wait();
            PeerDiscovery? da = null, db = null;
            NetGame? gA = null, gB = null;
            try
            {
                da = new PeerDiscovery(a, "127.0.0.1"); da.Start();
                db = new PeerDiscovery(b, "127.0.0.1"); db.Start();
                da.SetOnChainSeeds(new[] { ("127.0.0.1", b.BoundPort) });
                db.SetOnChainSeeds(new[] { ("127.0.0.1", a.BoundPort) });
                T.True(Until(() => a.PeerCount >= 1 && b.PeerCount >= 1, 8000), "the two nodes connect");

                const string table = "t-join~TexasHoldem~p2~s100~b2";
                // HOST hosts and joins its own table first, then beacons hello alone for a while.
                a.CreateTableAsync(table, "Join table").Wait();
                gA = new NetGame(a, table, ka.Priv, ka.Pub); gA.Start();
                Thread.Sleep(2000);   // the exact ordering that exposed the bug: host's hello is 'seen' on B first

                // the JOINER must SEE the table in its lobby, then join it (like a double-click)
                T.True(Until(() => b.ListTables().Any(t => t.id == table), 8000), "joiner sees the host's table in the lobby");
                gB = new NetGame(b, table, kb.Priv, kb.Pub); gB.Start();

                // THE FIX: both must reach a dealt hand with a real seat (not host=Dealing / joiner=Waiting forever)
                T.True(Until(() => gA.Hand != null && gB.Hand != null && gA.MySeat >= 0 && gB.MySeat >= 0, 30000),
                    "the game STARTS — both players seated and dealt (via the anti-grind commit-reveal seating)");
                // ANTI-GRINDING: both peers independently derive the SAME seat order from the joint nonce seed
                // (not from sorted pubkey) — consensus on a fair order no single player could bias.
                T.True(gA.SeatPubs.SequenceEqual(gB.SeatPubs), "both peers agree on the fair (joint-randomness) seat order");

                // and it actually PLAYS: drive checks/calls until at least one hand completes
                bool played = Until(() => { Drive(gA); Drive(gB); return gA.HandNumber >= 1; }, 30000);
                T.True(played, "a hand plays to completion (chips settle, next hand begins)");
                T.True(!gA.Aborted && !gB.Aborted && !gA.CheatDetected && !gB.CheatDetected, "no abort and no false cheat detection");
            }
            finally { try { gA?.Stop(); } catch { } try { gB?.Stop(); } catch { } try { da?.Dispose(); } catch { } try { db?.Dispose(); } catch { } a.Dispose(); b.Dispose(); }
        });

        T.Run("who's online directory: each node lists the other's signed @handle and reachable endpoint", () =>
        {
            var ka = Secp256k1.GenerateKeyPair();
            var kb = Secp256k1.GenerateKeyPair();
            string aHex = Convert.ToHexString(ka.Pub).ToLowerInvariant();
            string bHex = Convert.ToHexString(kb.Pub).ToLowerInvariant();
            var a = new P2PNode(0, "127.0.0.1"); a.SetIdentity(ka.Priv, ka.Pub); a.StartAsync().Wait();
            var b = new P2PNode(0, "127.0.0.1"); b.SetIdentity(kb.Priv, kb.Pub); b.StartAsync().Wait();
            PeerDiscovery? da = null, db = null;
            try
            {
                da = new PeerDiscovery(a, "127.0.0.1"); da.Start();
                db = new PeerDiscovery(b, "127.0.0.1"); db.Start();
                da.SetOnChainSeeds(new[] { ("127.0.0.1", b.BoundPort) });
                db.SetOnChainSeeds(new[] { ("127.0.0.1", a.BoundPort) });
                T.True(Until(() => a.PeerCount >= 1 && b.PeerCount >= 1, 8000), "the two nodes connect");

                T.True(Until(() =>
                {
                    a.HeartbeatAsync(aHex, "127.0.0.1:5001", "CsTominaga").Wait();
                    b.HeartbeatAsync(bHex, "127.0.0.1:5002", "Bob").Wait();
                    return a.ListPresence().Any(p => p.playerId == bHex) && b.ListPresence().Any(p => p.playerId == aHex);
                }, 8000), "each node sees the other in its presence directory");

                var aSeesB = a.ListPresence().First(p => p.playerId == bHex);
                var bSeesA = b.ListPresence().First(p => p.playerId == aHex);
                T.Eq(aSeesB.handle, "Bob", "A sees B's attested @handle");
                T.Eq(bSeesA.handle, "CsTominaga", "B sees A's attested @handle");
                T.Eq(aSeesB.addr, "127.0.0.1:5002", "A has B's reachable endpoint (instant delivery)");
                T.Eq(bSeesA.addr, "127.0.0.1:5001", "B has A's reachable endpoint (instant delivery)");
            }
            finally { try { da?.Dispose(); } catch { } try { db?.Dispose(); } catch { } a.Dispose(); b.Dispose(); }
        });

        T.Run("presence handle cannot be spoofed: a beacon signed by the wrong key is rejected", () =>
        {
            var victim = Secp256k1.GenerateKeyPair();
            var attacker = Secp256k1.GenerateKeyPair();
            string victimHex = Convert.ToHexString(victim.Pub).ToLowerInvariant();
            // the attacker's node signs (with ITS key) a presence claiming to BE the victim — must be dropped.
            var a = new P2PNode(0, "127.0.0.1"); a.SetIdentity(attacker.Priv, attacker.Pub); a.StartAsync().Wait();
            var b = new P2PNode(0, "127.0.0.1"); b.SetIdentity(victim.Priv, victim.Pub); b.StartAsync().Wait();
            PeerDiscovery? da = null, db = null;
            try
            {
                da = new PeerDiscovery(a, "127.0.0.1"); da.Start();
                db = new PeerDiscovery(b, "127.0.0.1"); db.Start();
                da.SetOnChainSeeds(new[] { ("127.0.0.1", b.BoundPort) });
                db.SetOnChainSeeds(new[] { ("127.0.0.1", a.BoundPort) });
                T.True(Until(() => a.PeerCount >= 1 && b.PeerCount >= 1, 8000), "the two nodes connect");
                // attacker beacons presence for victimHex (but signs with the attacker key, since that's a's identity)
                a.HeartbeatAsync(victimHex, "127.0.0.1:6666", "Imposter").Wait();
                Thread.Sleep(500);
                T.True(b.ListPresence().All(p => p.playerId != victimHex || p.addr != "127.0.0.1:6666"),
                    "a presence beacon not signed by the claimed identity key is rejected");
            }
            finally { try { da?.Dispose(); } catch { } try { db?.Dispose(); } catch { } a.Dispose(); b.Dispose(); }
        });
    }
}
