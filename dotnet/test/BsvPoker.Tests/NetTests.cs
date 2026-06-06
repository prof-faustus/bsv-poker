using System.Net.Sockets;
using System.Text;
using BsvPoker.Crypto;
using BsvPoker.Net;

namespace BsvPoker.Tests;

public static class NetTests
{
    private static bool Until(Func<bool> cond, int ms = 4000)
    {
        var dl = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < dl) { if (cond()) return true; Thread.Sleep(20); }
        return cond();
    }

    public static void All()
    {
        Console.WriteLine("P2P gossip transport (no server):");

        T.Run("a frame gossips A—B—C to the far peer exactly once", () =>
        {
            using var a = new P2PNode(0, "127.0.0.1");
            using var b = new P2PNode(0, "127.0.0.1");
            using var c = new P2PNode(0, "127.0.0.1");
            a.StartAsync().Wait();
            b.StartAsync(new[] { new P2PNode.PeerAddr("127.0.0.1", a.BoundPort) }).Wait();
            c.StartAsync(new[] { new P2PNode.PeerAddr("127.0.0.1", b.BoundPort) }).Wait();
            T.True(Until(() => a.PeerCount >= 1 && b.PeerCount >= 2 && c.PeerCount >= 1), "mesh A-B-C formed");
            var atC = new List<string>();
            c.Subscribe("tbl", t => { lock (atC) atC.Add(t); });
            Thread.Sleep(100);
            a.PublishAsync("tbl", Encoding.UTF8.GetBytes("hello")).Wait();
            T.True(Until(() => atC.Count > 0), "C received");
            Thread.Sleep(100);
            lock (atC) { T.Eq(atC.Count, 1, "exactly once (dedup)"); T.Eq(atC[0], "hello"); }
        });

        T.Run("a hosted table is discovered across the mesh (serverless directory)", () =>
        {
            using var a = new P2PNode(0, "127.0.0.1");
            using var b = new P2PNode(0, "127.0.0.1");
            a.StartAsync().Wait();
            b.StartAsync(new[] { new P2PNode.PeerAddr("127.0.0.1", a.BoundPort) }).Wait();
            T.True(Until(() => a.PeerCount >= 1 && b.PeerCount >= 1), "connected");
            var hk = Secp256k1.GenerateKeyPair(); a.SetIdentity(hk.Priv, hk.Pub); // announcements must be signed
            a.CreateTableAsync("t1", "Friday").Wait();
            T.True(Until(() => b.ListTables().Any(x => x.id == "t1")), "B discovered A's signed table");
        });

        T.Run("transport: an unsigned table announcement is rejected; a signed one propagates (audit 3.2)", () =>
        {
            using var a = new P2PNode(0, "127.0.0.1");
            using var b = new P2PNode(0, "127.0.0.1");
            a.StartAsync().Wait();
            b.StartAsync(new[] { new P2PNode.PeerAddr("127.0.0.1", a.BoundPort) }).Wait();
            T.True(Until(() => a.PeerCount >= 1 && b.PeerCount >= 1), "connected");
            // no identity set on A → its announcement is unsigned → rejected everywhere (even by A itself)
            a.CreateTableAsync("noid", "Unsigned").Wait();
            Thread.Sleep(300);
            T.False(b.ListTables().Any(x => x.id == "noid"), "unsigned table not propagated");
            T.True(a.DroppedFrames > 0, "the unsigned announcement is dropped on verification");
            // now give A an identity → a signed table propagates
            var k = Secp256k1.GenerateKeyPair(); a.SetIdentity(k.Priv, k.Pub);
            a.CreateTableAsync("good", "Signed").Wait();
            T.True(Until(() => b.ListTables().Any(x => x.id == "good")), "signed table propagates");
        });

        T.Run("transport hardening: an oversize frame is dropped (byte cap) and the link keeps serving valid frames", () =>
        {
            using var node = new P2PNode(0, "127.0.0.1");
            node.StartAsync().Wait();
            string? got = null;
            node.Subscribe("topic-x", t => got = t);
            using var c = new TcpClient();
            c.Connect("127.0.0.1", node.BoundPort);
            var s = c.GetStream();
            // a single frame far larger than the 1 MiB cap, terminated by a newline
            var big = new byte[(1 << 20) + 1000]; Array.Fill(big, (byte)'a');
            s.Write(big); s.WriteByte((byte)'\n');
            // then a perfectly valid frame on the same connection (must resync past the dropped one)
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello-valid"));
            var frame = $"{{\"t\":\"topic-x\",\"d\":\"{payload}\",\"id\":\"{Guid.NewGuid():N}\"}}\n";
            s.Write(Encoding.UTF8.GetBytes(frame)); s.Flush();
            T.True(Until(() => node.DroppedFrames > 0), "the oversize frame was dropped");
            T.True(Until(() => got == "hello-valid"), "a valid frame after the oversize one still delivers (resync)");
        });

        T.Run("transport: loopback by default, LAN is an explicit opt-in, and the rebind keeps accepting", () =>
        {
            using var n = new P2PNode(0, "127.0.0.1");
            n.StartAsync().Wait();
            T.False(n.LanEnabled, "listens on loopback only by default");
            n.EnableLan();
            T.True(n.LanEnabled, "LAN opt-in enables all-interfaces listening");
            using var p = new P2PNode(0, "127.0.0.1");
            p.StartAsync(new[] { new P2PNode.PeerAddr("127.0.0.1", n.BoundPort) }).Wait();
            T.True(Until(() => n.PeerCount >= 1 && p.PeerCount >= 1), "a peer still connects after the LAN rebind");
        });
    }
}
