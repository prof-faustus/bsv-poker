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

    /// <summary>Connection diagnostics (seed resolution, dial attempts, handshake errors) so "no peers" is debuggable.</summary>
    public event Action<string>? OnLog;
    private void Log(string m) { try { OnLog?.Invoke(m); } catch { } }

    /// <summary>Manually-added peers (host:port) the user pointed us at — tried in addition to the DNS seeds.</summary>
    private readonly List<IPEndPoint> _manual = new();

    /// <summary>Resolve the network's DNS seeds to candidate peer endpoints (empty for regtest).</summary>
    public async Task<List<IPEndPoint>> ResolveSeedsAsync()
    {
        var eps = new List<IPEndPoint>();
        foreach (var seed in _net.DnsSeeds)
        {
            try { var ips = await Dns.GetHostAddressesAsync(seed); foreach (var ip in ips) eps.Add(new IPEndPoint(ip, _net.DefaultPort)); Log($"seed {seed} → {ips.Length} address(es)"); }
            catch (Exception ex) { Log($"seed {seed} failed: {ex.Message}"); }
        }
        return eps;
    }

    /// <summary>Point the node at a specific peer (host:port). Dialed immediately and on every refill round.</summary>
    public void AddManualPeer(string host, int port)
    {
        try { foreach (var ip in Dns.GetHostAddresses(host)) _manual.Add(new IPEndPoint(ip, port)); Log($"manual peer {host}:{port} added"); _ = ConnectAsync(host, port); }
        catch (Exception ex) { Log($"manual peer {host}:{port} failed to resolve: {ex.Message}"); }
    }

    /// <summary>Begin connecting to the live network (seeds → dial up to maxPeers, refilling as peers drop).</summary>
    public async Task StartAsync(int maxPeers = 8)
    {
        if (_started) return; _started = true;
        var seeds = await ResolveSeedsAsync();
        Log($"resolved {seeds.Count} candidate peer(s) from {_net.DnsSeeds.Length} DNS seed(s) on {_net.Network}");
        _ = Task.Run(async () =>
        {
            var rnd = new Random();
            var cooldown = new Dictionary<string, long>();   // endpoint → earliest next-dial tick (avoid hammering/greylisting)
            var inFlight = new HashSet<string>();
            while (!_cts.IsCancellationRequested)
            {
                var candidates = _manual.Concat(seeds).ToList();
                long now = Environment.TickCount64;
                if (_peers.Count < maxPeers && candidates.Count > 0)
                {
                    foreach (var ep in candidates.OrderBy(_ => rnd.Next()))
                    {
                        if (_peers.Count + inFlight.Count >= maxPeers) break;
                        var key = $"{ep.Address}:{ep.Port}";
                        if (_peers.ContainsKey(key) || inFlight.Contains(key)) continue;
                        if (cooldown.TryGetValue(key, out var next) && now < next) continue;  // not yet — back off
                        cooldown[key] = now + 60_000;            // don't re-dial this node for 60s (prevents greylisting)
                        inFlight.Add(key);
                        _ = ConnectAsync(ep.Address.ToString(), ep.Port).ContinueWith(_ => { lock (inFlight) inFlight.Remove(key); });
                    }
                }
                else if (candidates.Count == 0) Log("no candidate peers — DNS seeds returned nothing; add a node manually (Tools → Network).");
                try { await Task.Delay(3000, _cts.Token); } catch { return; }
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
            peer.OnMessage += m => HandleRelay(peer, m);   // receive relayed transactions (announce/chat/etc.) from the network
            // give this fresh peer our SPV filter so it relays our own transactions back to us
            if (_filter is { } f) { try { peer.Send("filterload", f.ToFilterLoad()); peer.Send("mempool", Array.Empty<byte>()); } catch { } }
            if (peer.RemoteVersion is { } v && v.StartHeight > BestHeight) BestHeight = v.StartHeight;
            Log($"connected to {key} (height {peer.RemoteVersion?.StartHeight ?? 0}); peers={_peers.Count}");
            return true;
        }
        catch (Exception ex) { Log($"dial {key} failed: {ex.Message}"); return false; }
    }

    /// <summary>Raised when a transaction is relayed to us by the network (used for automatic peer discovery + inbound messages).</summary>
    public event Action<BsvPoker.Core.Chain.Tx>? OnRelayedTransaction;

    /// <summary>
    /// Raised when a transaction that matched our SPV filter arrives WITH a proof that it was mined: the tx and
    /// the raw <c>merkleblock</c> payload whose partial tree contains it. The wallet verifies the proof against
    /// its own validated headers (<see cref="SpvFunding.VerifyFromMerkleBlock"/>) before crediting anything.
    /// </summary>
    public event Action<BsvPoker.Core.Chain.Tx, byte[]>? OnConfirmedTransaction;

    private volatile BloomFilter? _filter;
    // a merkleblock proves a set of txids were mined; we cache it by matched txid so that when the matching tx
    // arrives moments later (peers send the merkleblock first, then the txs) we can pair proof ↔ transaction.
    private readonly ConcurrentDictionary<string, byte[]> _proofByTxid = new();

    /// <summary>
    /// Load (or replace) the SPV bloom filter from the wallet's own address material and push it to every peer.
    /// After loading we also pull the peers' mempools so a payment that is still unconfirmed shows up at once.
    /// </summary>
    public void SetSpvFilter(BloomFilter filter)
    {
        _filter = filter;
        var payload = filter.ToFilterLoad();
        foreach (var p in _peers.Values) { try { p.Send("filterload", payload); } catch { } }
        RequestMempool();
    }

    /// <summary>Ask peers for the txids in their mempool that match our filter (instant detect of a just-sent payment).</summary>
    public void RequestMempool()
    {
        foreach (var p in _peers.Values) { try { p.Send("mempool", Array.Empty<byte>()); } catch { } }
    }

    /// <summary>
    /// Ask peers for <c>merkleblock</c> proofs (filtered blocks) over the given block hashes — an SPV rescan of
    /// recent history so a payment that confirmed before we connected is still discovered. Hashes are display
    /// (big-endian) form; we request MSG_FILTERED_BLOCK (type 3) so only our matching txs + a proof come back.
    /// </summary>
    public void RequestFilteredBlocks(IEnumerable<string> blockHashesDisplayHex)
    {
        if (_filter == null) return;
        var internalHashes = blockHashesDisplayHex.Select(InternalHash).ToList();
        if (internalHashes.Count == 0) return;
        var gd = BuildGetDataMany(3 /* MSG_FILTERED_BLOCK */, internalHashes);
        foreach (var p in _peers.Values) { try { p.Send("getdata", gd); } catch { } }
    }

    // Handle messages a peer relays: announce inventory we want (tx) → getdata; a relayed tx → parse + surface.
    private void HandleRelay(BsvPeer peer, BsvMessage m)
    {
        try
        {
            if (m.Command == "inv")
            {
                int o = 0; ulong count = BsvVersion.ReadVarInt(m.Payload, ref o);
                var want = new List<byte>();
                ulong txCount = 0;
                for (ulong i = 0; i < count && o + 36 <= m.Payload.Length; i++)
                {
                    uint type = BinaryPrimitives.ReadUInt32LittleEndian(m.Payload.AsSpan(o, 4));
                    if (type == 1) { want.AddRange(m.Payload.AsSpan(o, 36).ToArray()); txCount++; } // MSG_TX
                    o += 36;
                }
                if (txCount > 0)
                {
                    var gd = new List<byte>(); BsvVersion.WriteVarInt(gd, txCount); gd.AddRange(want);
                    peer.Send("getdata", gd.ToArray());
                }
            }
            else if (m.Command == "merkleblock")
            {
                // a proof that some txs were mined: remember it keyed by each matched txid so the tx messages
                // that follow can be paired with their proof. We do NOT trust it yet — the wallet re-verifies
                // the partial tree against a header IT validated before crediting anything.
                var parsed = PartialMerkleTree.ParseMerkleBlock(m.Payload);
                foreach (var (txidInternal, _) in parsed.Matched)
                {
                    var disp = (byte[])txidInternal.Clone(); Array.Reverse(disp);
                    _proofByTxid[Convert.ToHexString(disp).ToLowerInvariant()] = m.Payload;
                }
            }
            else if (m.Command == "tx")
            {
                int o = 0; var tx = BsvPoker.Core.Chain.Deserialize(m.Payload, ref o);
                OnRelayedTransaction?.Invoke(tx);                              // unconfirmed / mempool (shown as pending)
                if (_proofByTxid.TryGetValue(BsvPoker.Core.Chain.Txid(tx), out var proof))
                    OnConfirmedTransaction?.Invoke(tx, proof);                 // we also hold a mined-proof for it
            }
        }
        catch { }
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

    /// <summary>
    /// Persistent header sync: resume from the store's last header (or genesis if empty), download forward in
    /// getheaders batches, validate PoW + linkage across batch boundaries, and append each validated batch to
    /// the store so progress survives a restart. Returns (appended this run, total height in the store). The
    /// store is only ever extended with headers that link onto what it already holds — a peer cannot make us
    /// persist an unlinked or bad-PoW header.
    /// </summary>
    public async Task<(int Appended, int StoreHeight)> SyncHeadersToStoreAsync(HeaderStore store, int maxBatches = 50, int waitMs = 8000)
    {
        var peer = _peers.Values.FirstOrDefault();
        var genesis = InternalHash(_net.GenesisHashHex);
        if (peer == null) return (0, store.Count);
        var prev = store.TipOrGenesis(genesis); // resume locator
        int appended = 0;
        for (int batch = 0; batch < maxBatches; batch++)
        {
            var received = new List<BlockHeader>();
            var tcs = new TaskCompletionSource<bool>();
            void Handler(BsvMessage m) { if (m.Command == "headers") { try { ParseHeaders(m.Payload, received); } catch { } tcs.TrySetResult(true); } }
            peer.OnMessage += Handler;
            try { peer.Send("getheaders", BuildGetHeaders(new[] { prev })); await Task.WhenAny(tcs.Task, Task.Delay(waitMs)); }
            finally { peer.OnMessage -= Handler; }
            if (received.Count == 0) break;
            var batchValid = new List<BlockHeader>();
            foreach (var h in received)
            {
                if (!h.MeetsPow() || !h.PrevHash.AsSpan().SequenceEqual(prev)) break; // stop at first non-linking header
                prev = h.Hash(); batchValid.Add(h);
            }
            if (batchValid.Count == 0) break;
            store.Append(batchValid);
            appended += batchValid.Count;
            if (received.Count < 2000) break; // caught up to the tip
        }
        return (appended, store.Count);
    }

    /// <summary>
    /// Fetch a full block from a connected peer by its display hash (getdata MSG_BLOCK → block). Returns the
    /// raw block bytes, or null on timeout. The caller validates it with <see cref="BsvBlock.Parse"/> (which
    /// checks the merkle root against the header) — this is how a payer obtains the block that confirmed their
    /// funding tx so they can build a merkleblock proof for the recipient, with no server in the path.
    /// </summary>
    public async Task<byte[]?> GetBlockAsync(string blockHashDisplayHex, int waitMs = 20000)
    {
        var peer = _peers.Values.FirstOrDefault();
        if (peer == null) return null;
        var want = InternalHash(blockHashDisplayHex);
        var tcs = new TaskCompletionSource<byte[]?>();
        void Handler(BsvMessage m)
        {
            if (m.Command != "block") return;
            try { if (BlockHeader.Parse(m.Payload.AsSpan(0, 80)).Hash().AsSpan().SequenceEqual(want)) tcs.TrySetResult(m.Payload); }
            catch { }
        }
        peer.OnMessage += Handler;
        try
        {
            peer.Send("getdata", BuildGetData(2 /* MSG_BLOCK */, want));
            await Task.WhenAny(tcs.Task, Task.Delay(waitMs));
            return tcs.Task.IsCompleted ? tcs.Task.Result : null;
        }
        finally { peer.OnMessage -= Handler; }
    }

    private static byte[] BuildGetData(uint invType, byte[] hashInternal)
    {
        var b = new List<byte>();
        BsvVersion.WriteVarInt(b, 1);                 // one inventory vector
        var t = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(t, invType); b.AddRange(t);
        b.AddRange(hashInternal);
        return b.ToArray();
    }

    private static byte[] BuildGetDataMany(uint invType, IReadOnlyList<byte[]> hashesInternal)
    {
        var b = new List<byte>();
        BsvVersion.WriteVarInt(b, (ulong)hashesInternal.Count);
        var t = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(t, invType);
        foreach (var h in hashesInternal) { b.AddRange(t); b.AddRange(h); }
        return b.ToArray();
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
