using System.Text;
using System.Text.Json;
using BsvPoker.Core;
using BsvPoker.Crypto;
using BsvPoker.Net;

namespace BsvPoker.Tests;

/// <summary>
/// DKG over the LIVE P2P mesh: the <see cref="DistributedKeyGen"/> protocol object, driven over real
/// <see cref="P2PNode"/> sockets (not in-memory). Each seated player broadcasts its Round-1 message
/// (commitments + per-peer sealed shares) over the gossip transport; every player collects the others',
/// opens + Feldman-verifies the shares dealt to it, and finalizes a joint key share. Proves the dealerless
/// key ceremony works across the actual network — the bridge from the unit-proven protocol to live play.
/// (Peer discovery / seat assignment is the gossip layer's job, tested elsewhere; here the seat→pubkey map
/// is fixed so the test targets the DKG-over-the-wire path.)
/// </summary>
public static class MeshDkgTests
{
    private static bool Until(Func<bool> c, int ms = 20000)
    {
        var dl = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < dl) { if (c()) return true; Thread.Sleep(20); }
        return c();
    }

    private static string Encode(DistributedKeyGen.Round1 r)
    {
        return JsonSerializer.Serialize(new
        {
            idx = r.Index,
            pub = Convert.ToHexString(r.DealerPub).ToLowerInvariant(),
            commits = r.Commitments.Select(c => Convert.ToHexString(c).ToLowerInvariant()).ToArray(),
            shares = r.SealedShares.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)
        });
    }

    private static DistributedKeyGen.Round1 Decode(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        int idx = root.GetProperty("idx").GetInt32();
        var pub = Convert.FromHexString(root.GetProperty("pub").GetString()!);
        var commits = root.GetProperty("commits").EnumerateArray().Select(e => Convert.FromHexString(e.GetString()!)).ToArray();
        var shares = new Dictionary<int, string>();
        foreach (var p in root.GetProperty("shares").EnumerateObject()) shares[int.Parse(p.Name)] = p.Value.GetString()!;
        return new DistributedKeyGen.Round1(idx, pub, commits, shares);
    }

    public static void All()
    {
        Console.WriteLine("DKG over the live P2P mesh (dealerless key ceremony across real sockets):");

        T.Run("3 players run the DKG over real P2PNodes: all agree on the joint key, no dealer, no blame", () =>
        {
            const int n = 3, t = 1, basePort = 0;
            const string topic = "dkg~table~p3";
            var nodes = new P2PNode[n];
            var kp = new (byte[] Priv, byte[] Pub)[n];
            for (int i = 0; i < n; i++) kp[i] = Secp256k1.GenerateKeyPair();

            nodes[0] = new P2PNode(basePort, "127.0.0.1"); nodes[0].StartAsync().Wait();
            for (int i = 1; i < n; i++)
            {
                nodes[i] = new P2PNode(basePort, "127.0.0.1");
                var seeds = Enumerable.Range(0, i).Select(j => new P2PNode.PeerAddr("127.0.0.1", nodes[j].BoundPort)).ToArray();
                nodes[i].StartAsync(seeds).Wait();
            }
            try
            {
                T.True(Until(() => nodes.All(x => x.PeerCount >= n - 1)), "mesh formed");

                // fixed seat→pubkey map (seat assignment is the gossip layer's concern, tested separately)
                var peerPubs = Enumerable.Range(1, n).ToDictionary(i => i, i => kp[i - 1].Pub);

                // each player deals and broadcasts its Round-1 over its own node; collect everyone's by index
                var collected = new System.Collections.Concurrent.ConcurrentDictionary<int, DistributedKeyGen.Round1>[n];
                var unsub = new Action[n];
                var myDeal = new (ThresholdSharing.Polynomial Poly, DistributedKeyGen.Round1 Msg)[n];
                for (int i = 0; i < n; i++)
                {
                    collected[i] = new();
                    int me = i;
                    unsub[i] = nodes[i].Subscribe(topic, text => { try { var r = Decode(text); collected[me].TryAdd(r.Index, r); } catch { } });
                }
                var sid = Encoding.ASCII.GetBytes("meshdkg-" + topic);
                for (int i = 0; i < n; i++)
                {
                    myDeal[i] = DistributedKeyGen.Deal(i + 1, kp[i].Pub, t, peerPubs, sid);
                    collected[i].TryAdd(i + 1, myDeal[i].Msg);   // include own
                }
                // broadcast (a few times, so a dropped frame is re-delivered — the transport sheds under load)
                for (int round = 0; round < 4; round++)
                {
                    for (int i = 0; i < n; i++) nodes[i].PublishAsync(topic, Encoding.UTF8.GetBytes(Encode(myDeal[i].Msg))).Wait();
                    if (collected.All(c => c.Count == n)) break;
                    Thread.Sleep(150);
                }
                T.True(Until(() => collected.All(c => c.Count == n)), "every player received all three Round-1 messages over the mesh");

                // each player finalizes from what it received over the wire
                var fin = new (byte[] Share, byte[] Pub, int? Blame)[n];
                for (int i = 0; i < n; i++)
                {
                    var all = Enumerable.Range(1, n).Select(j => collected[i][j]).ToList();
                    fin[i] = DistributedKeyGen.Finalize(i + 1, kp[i].Priv, all, sid);
                    T.True(fin[i].Blame == null, $"player {i + 1} finalized without blame");
                }
                var pub0 = fin[0].Pub;
                T.True(fin.All(f => f.Pub.SequenceEqual(pub0)), "all players agree on the joint public key over the mesh");

                // the mesh-DKG'd shares reconstruct the secret whose key is the agreed joint key
                var secret = ThresholdSharing.Reconstruct(new[] { (1, fin[0].Share), (2, fin[1].Share) });
                T.True(Secp256k1.PublicKeyCompressed(secret).SequenceEqual(pub0), "t+1 mesh-DKG'd shares reconstruct a, a·G == joint key");

                for (int i = 0; i < n; i++) unsub[i]();
            }
            finally { foreach (var x in nodes) { try { x?.Dispose(); } catch { } } }
        });
    }
}
