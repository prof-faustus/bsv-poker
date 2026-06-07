using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace BsvPoker.Net.Bsv;

/// <summary>
/// A live BSV network node: resolves the chosen network's DNS seeds, dials real peers, completes the
/// version/verack handshake, and maintains a set of connected peers — so the client genuinely participates
/// on mainnet/testnet/regtest (same code path; only <see cref="NetworkParams"/> differ). It tracks the
/// best advertised height and relays its own transactions to the network. No central server.
/// </summary>
public sealed class BsvNode : IDisposable
{
    private readonly NetworkParams _net;
    private readonly ConcurrentDictionary<string, BsvPeer> _peers = new();
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _started;

    public BsvNode(NetworkParams net) { _net = net; }
    public NetworkParams Network => _net;
    public int PeerCount => _peers.Count;
    public int BestHeight { get; private set; }

    /// <summary>Resolve the network's DNS seeds to candidate peer endpoints (empty for regtest).</summary>
    public async Task<List<IPEndPoint>> ResolveSeedsAsync()
    {
        var eps = new List<IPEndPoint>();
        foreach (var seed in _net.DnsSeeds)
        {
            try { foreach (var ip in await Dns.GetHostAddressesAsync(seed)) eps.Add(new IPEndPoint(ip, _net.DefaultPort)); }
            catch { /* a dead seed is fine; others remain */ }
        }
        return eps;
    }

    /// <summary>Begin connecting to the live network (seeds → dial up to maxPeers, refilling as peers drop).</summary>
    public async Task StartAsync(int maxPeers = 8)
    {
        if (_started) return; _started = true;
        var seeds = await ResolveSeedsAsync();
        _ = Task.Run(async () =>
        {
            var rnd = new Random();
            while (!_cts.IsCancellationRequested)
            {
                if (_peers.Count < maxPeers && seeds.Count > 0)
                    foreach (var ep in seeds.OrderBy(_ => rnd.Next()).Take(maxPeers - _peers.Count))
                        _ = ConnectAsync(ep.Address.ToString(), ep.Port);
                try { await Task.Delay(5000, _cts.Token); } catch { return; }
            }
        });
    }

    /// <summary>Dial one peer, handshake, and track it. Returns true on a completed handshake.</summary>
    public async Task<bool> ConnectAsync(string host, int port)
    {
        var key = $"{host}:{port}";
        if (_peers.ContainsKey(key)) return true;
        try
        {
            var c = new TcpClient();
            await c.ConnectAsync(host, port).WaitAsync(TimeSpan.FromSeconds(6), _cts.Token);
            var peer = new BsvPeer(_net, c);
            await peer.HandshakeAsync(startHeight: BestHeight, ct: _cts.Token);
            _peers[key] = peer;
            if (peer.RemoteVersion is { } v && v.StartHeight > BestHeight) BestHeight = v.StartHeight;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Broadcast a raw transaction to every connected peer (the client relays onto the network itself).</summary>
    public void Broadcast(byte[] rawTx)
    {
        foreach (var p in _peers.Values) { try { p.Send("tx", rawTx); } catch { } }
    }

    /// <summary>
    /// Download the first batch of block headers from a connected peer starting at genesis, and validate
    /// them (proof-of-work + parent linkage). Returns (validated, received). This is real header sync from
    /// the live network — not a fixture.
    /// </summary>
    public async Task<(int Valid, int Received)> DownloadHeadersFromGenesisAsync(int waitMs = 8000)
    {
        var peer = _peers.Values.FirstOrDefault();
        if (peer == null) return (0, 0);
        var received = new List<BlockHeader>();
        var tcs = new TaskCompletionSource<bool>();
        void Handler(BsvMessage m) { if (m.Command == "headers") { try { ParseHeaders(m.Payload, received); } catch { } tcs.TrySetResult(true); } }
        peer.OnMessage += Handler;
        try
        {
            var gen = InternalHash(_net.GenesisHashHex);
            peer.Send("getheaders", BuildGetHeaders(new[] { gen }));
            await Task.WhenAny(tcs.Task, Task.Delay(waitMs));
            return (ValidateBatch(received, gen), received.Count);
        }
        finally { peer.OnMessage -= Handler; }
    }

    /// <summary>
    /// Sync the header chain forward from genesis over multiple getheaders batches, advancing the locator to
    /// the last header each round and validating PoW + linkage across batch boundaries. Stops after
    /// <paramref name="maxBatches"/> or when caught up (a short batch). Returns (total validated, tip height
    /// from genesis). Real multi-batch sync from the live network.
    /// </summary>
    public async Task<(int Total, int TipHeight)> SyncHeadersAsync(int maxBatches = 10, int waitMs = 8000)
    {
        var peer = _peers.Values.FirstOrDefault();
        if (peer == null) return (0, 0);
        var prev = InternalHash(_net.GenesisHashHex);
        int total = 0;
        for (int batch = 0; batch < maxBatches; batch++)
        {
            var received = new List<BlockHeader>();
            var tcs = new TaskCompletionSource<bool>();
            void Handler(BsvMessage m) { if (m.Command == "headers") { try { ParseHeaders(m.Payload, received); } catch { } tcs.TrySetResult(true); } }
            peer.OnMessage += Handler;
            try { peer.Send("getheaders", BuildGetHeaders(new[] { prev })); await Task.WhenAny(tcs.Task, Task.Delay(waitMs)); }
            finally { peer.OnMessage -= Handler; }
            if (received.Count == 0) break;
            foreach (var h in received)
            {
                if (!h.MeetsPow() || !h.PrevHash.AsSpan().SequenceEqual(prev)) return (total, total); // chain broke → stop
                prev = h.Hash(); total++;
            }
            if (received.Count < 2000) break; // caught up to the tip
        }
        return (total, total);
    }

    private static byte[] InternalHash(string displayHex) { var b = Convert.FromHexString(displayHex); Array.Reverse(b); return b; }

    private static byte[] BuildGetHeaders(byte[][] locator)
    {
        var b = new List<byte>();
        var v = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(v, (uint)BsvVersion.ProtocolVersion); b.AddRange(v);
        BsvVersion.WriteVarInt(b, (ulong)locator.Length);
        foreach (var h in locator) b.AddRange(h);
        b.AddRange(new byte[32]); // hash stop = 0 → "send as many as you can"
        return b.ToArray();
    }

    private static void ParseHeaders(byte[] payload, List<BlockHeader> outp)
    {
        int o = 0; ulong count = BsvVersion.ReadVarInt(payload, ref o);
        for (ulong i = 0; i < count; i++)
        {
            if (o + 80 > payload.Length) break;
            outp.Add(BlockHeader.Parse(payload.AsSpan(o, 80))); o += 80;
            _ = BsvVersion.ReadVarInt(payload, ref o); // per-header tx count (0 in a headers message)
        }
    }

    private static int ValidateBatch(List<BlockHeader> hs, byte[] genInternal)
    {
        int valid = 0; var prev = genInternal;
        foreach (var h in hs)
        {
            if (!h.MeetsPow()) break;
            if (!h.PrevHash.AsSpan().SequenceEqual(prev)) break;
            prev = h.Hash(); valid++;
        }
        return valid;
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var p in _peers.Values) { try { p.Dispose(); } catch { } }
        _peers.Clear();
    }
}
