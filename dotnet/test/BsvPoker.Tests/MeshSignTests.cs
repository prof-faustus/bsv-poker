using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using BsvPoker.Core;
using BsvPoker.Crypto;
using BsvPoker.Net;

namespace BsvPoker.Tests;

/// <summary>
/// FULL threshold-signature ceremony over the LIVE P2P mesh — the end-to-end capstone of the dealerless
/// threshold stack across real sockets. n players run three DKGs (key a, ephemeral k, blinding b) over the
/// gossip transport, then exchange one product-share (k_i·b_i) and one signature-share each, and any party
/// assembles a STANDARD secp256k1 signature that verifies against the DKG'd key. No dealer, no key
/// reconstruction, no in-memory shortcut — every secret is shared, every message crosses a socket.
/// </summary>
public static class MeshSignTests
{
    private static bool Until(Func<bool> c, int ms = 25000)
    {
        var dl = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < dl) { if (c()) return true; Thread.Sleep(20); }
        return c();
    }

    // Broadcast each node's payload on `topic` and collect every node's payload (by index) at every node.
    // Re-broadcasts a few times so a shed frame is re-delivered. Returns per-node maps once all are complete.
    private static ConcurrentDictionary<int, string>[] CollectAll(P2PNode[] nodes, string topic, string[] payloadByNode, int n)
    {
        var maps = new ConcurrentDictionary<int, string>[n];
        var unsub = new Action[n];
        for (int i = 0; i < n; i++)
        {
            maps[i] = new();
            int me = i;
            unsub[i] = nodes[i].Subscribe(topic, text =>
            {
                try { using var d = JsonDocument.Parse(text); maps[me].TryAdd(d.RootElement.GetProperty("idx").GetInt32(), text); } catch { }
            });
            maps[i].TryAdd(i + 1, payloadByNode[i]); // include own
        }
        for (int round = 0; round < 8; round++)
        {
            for (int i = 0; i < n; i++) nodes[i].PublishAsync(topic, Encoding.UTF8.GetBytes(payloadByNode[i])).Wait();
            if (maps.All(m => m.Count == n)) break;
            Thread.Sleep(150);
        }
        Until(() => maps.All(m => m.Count == n));
        for (int i = 0; i < n; i++) unsub[i]();
        return maps;
    }

    private static string EncRound1(DistributedKeyGen.Round1 r) => JsonSerializer.Serialize(new
    {
        idx = r.Index,
        pub = Convert.ToHexString(r.DealerPub).ToLowerInvariant(),
        commits = r.Commitments.Select(c => Convert.ToHexString(c).ToLowerInvariant()).ToArray(),
        shares = r.SealedShares.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)
    });

    private static DistributedKeyGen.Round1 DecRound1(string json)
    {
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        var commits = root.GetProperty("commits").EnumerateArray().Select(e => Convert.FromHexString(e.GetString()!)).ToArray();
        var shares = new Dictionary<int, string>();
        foreach (var p in root.GetProperty("shares").EnumerateObject()) shares[int.Parse(p.Name)] = p.Value.GetString()!;
        return new DistributedKeyGen.Round1(root.GetProperty("idx").GetInt32(), Convert.FromHexString(root.GetProperty("pub").GetString()!), commits, shares);
    }

    private static string EncScalar(int idx, byte[] y) => JsonSerializer.Serialize(new { idx, y = Convert.ToHexString(y).ToLowerInvariant() });
    private static byte[] DecScalar(string json) { using var d = JsonDocument.Parse(json); return Convert.FromHexString(d.RootElement.GetProperty("y").GetString()!); }

    // One DKG over the mesh: returns each node's joint key share (by node index 0..n-1) and the agreed joint pubkey.
    private static (byte[][] shares, byte[] pub) MeshDkg(P2PNode[] nodes, (byte[] Priv, byte[] Pub)[] kp, IReadOnlyDictionary<int, byte[]> peerPubs, string topic, int n, int t)
    {
        var deals = new (ThresholdSharing.Polynomial Poly, DistributedKeyGen.Round1 Msg)[n];
        var payloads = new string[n];
        var sid = System.Text.Encoding.ASCII.GetBytes("mesh-" + topic);
        for (int i = 0; i < n; i++) { deals[i] = DistributedKeyGen.Deal(i + 1, kp[i].Pub, t, peerPubs, sid); payloads[i] = EncRound1(deals[i].Msg); }
        var maps = CollectAll(nodes, topic, payloads, n);
        var shares = new byte[n][]; byte[] pub = Array.Empty<byte>();
        for (int i = 0; i < n; i++)
        {
            var all = Enumerable.Range(1, n).Select(j => DecRound1(maps[i][j])).ToList();
            var f = DistributedKeyGen.Finalize(i + 1, kp[i].Priv, all, sid);
            if (f.Blame != null) throw new Exception($"node {i} blamed {f.Blame}");
            shares[i] = f.JointShare; pub = f.JointPublicKey;
        }
        return (shares, pub);
    }

    public static void All()
    {
        Console.WriteLine("full threshold-signature ceremony over the live P2P mesh (dealerless, end-to-end):");

        T.Run("3 players DKG a key, ephemeral and blinding over sockets, then jointly sign — verifies against the key", () =>
        {
            const int n = 3, t = 1;
            var nodes = new P2PNode[n];
            var kp = new (byte[] Priv, byte[] Pub)[n];
            for (int i = 0; i < n; i++) kp[i] = Secp256k1.GenerateKeyPair();
            nodes[0] = new P2PNode(0, "127.0.0.1"); nodes[0].StartAsync().Wait();
            for (int i = 1; i < n; i++)
            {
                nodes[i] = new P2PNode(0, "127.0.0.1");
                var seeds = Enumerable.Range(0, i).Select(j => new P2PNode.PeerAddr("127.0.0.1", nodes[j].BoundPort)).ToArray();
                nodes[i].StartAsync(seeds).Wait();
            }
            try
            {
                T.True(Until(() => nodes.All(x => x.PeerCount >= n - 1)), "mesh formed");
                var peerPubs = Enumerable.Range(1, n).ToDictionary(i => i, i => kp[i - 1].Pub);

                var (aShares, aPub) = MeshDkg(nodes, kp, peerPubs, "sign~a", n, t);

                byte[]? sig = null;
                var digest = Hashes.Sha256(Encoding.UTF8.GetBytes("dealerless threshold spend over the wire"));
                for (int attempt = 0; attempt < 6 && sig == null; attempt++)
                {
                    var (kShares, kPub) = MeshDkg(nodes, kp, peerPubs, $"sign~k{attempt}", n, t);
                    var (bShares, _) = MeshDkg(nodes, kp, peerPubs, $"sign~b{attempt}", n, t);

                    var r = Secp256k1.ScalarMulModN(kPub.AsSpan(1, 32).ToArray(), Enumerable.Repeat((byte)0, 31).Append((byte)1).ToArray());
                    if (r.All(z => z == 0)) continue;

                    // product-share round over the mesh: each node broadcasts k_i·b_i; collect all → u = k·b
                    var prodPayloads = Enumerable.Range(0, n).Select(i => EncScalar(i + 1, Secp256k1.ScalarMulModN(kShares[i], bShares[i]))).ToArray();
                    var prodMaps = CollectAll(nodes, $"sign~u{attempt}", prodPayloads, n);
                    var prodShares = Enumerable.Range(1, n).Select(j => (j, DecScalar(prodMaps[0][j]))).ToArray();
                    var u = ThresholdSharing.Reconstruct(prodShares);
                    if (u.All(z => z == 0)) continue;
                    var uInv = Secp256k1.ScalarInverse(u);

                    // signature-share round: each node broadcasts s_i = (e + r·a_i)·(u⁻¹·b_i); collect → s
                    var e = Secp256k1.ScalarMulModN(digest, Enumerable.Repeat((byte)0, 31).Append((byte)1).ToArray());
                    var sPayloads = Enumerable.Range(0, n).Select(i =>
                    {
                        var kInv = Secp256k1.ScalarMulModN(uInv, bShares[i]);
                        var era = Secp256k1.ScalarFieldAddModN(e, Secp256k1.ScalarMulModN(r, aShares[i]));
                        return EncScalar(i + 1, Secp256k1.ScalarMulModN(era, kInv));
                    }).ToArray();
                    var sMaps = CollectAll(nodes, $"sign~s{attempt}", sPayloads, n);
                    var sShares = Enumerable.Range(1, n).Select(j => (j, DecScalar(sMaps[0][j]))).ToArray();
                    var s = ThresholdSharing.Reconstruct(sShares);
                    if (s.All(z => z == 0)) continue;
                    s = Secp256k1.LowS(s);
                    sig = new byte[64]; r.CopyTo(sig, 0); s.CopyTo(sig, 32);
                }
                T.True(sig != null, "assembled a signature over the mesh");
                T.True(Secp256k1.VerifyDigest(aPub, digest, sig!), "the mesh-assembled threshold signature verifies against the DKG'd key");
            }
            finally { foreach (var x in nodes) { try { x?.Dispose(); } catch { } } }
        });
    }
}
