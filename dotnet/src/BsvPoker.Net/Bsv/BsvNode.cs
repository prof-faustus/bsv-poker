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

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var p in _peers.Values) { try { p.Dispose(); } catch { } }
        _peers.Clear();
    }
}
