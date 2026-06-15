using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BsvPoker.Crypto;

namespace BsvPoker.Net;

/// <summary>
/// Pure peer-to-peer gossip transport (NO server). Every node is an equal peer (listener + dialer): a
/// published frame is delivered to the node's own subscribers (echo) AND flooded to every connected
/// peer, which re-floods with per-frame dedup, so it reaches the whole mesh over any connected graph.
/// A serverless DIRECTORY (gossiped table announces) powers the lobby; presence powers discovery.
/// Hardened: connection cap, per-peer inbound rate limit, anti-eviction directory. Frames are
/// newline-delimited JSON {t,d,id} (d = base64). Stdlib only.
/// </summary>
public sealed class P2PNode : IDisposable, IGameTransport
{
    private const int MaxFrameBytes = 1 << 20;        // hard per-frame BYTE cap (byte-accurate, not chars)
    private const int MaxTopicBytes = 256;            // a topic string may not exceed this many UTF-8 bytes
    private const int MaxPayloadBytes = 1 << 20;      // decoded payload byte cap
    private const int MaxSeen = 100_000;
    private const int MaxDirectory = 10_000;
    private const int MaxPeers = 64;
    private const double RateCapacityBytes = 8 << 20;     // token bucket measured in BYTES (8 MiB burst)
    private const double RateRefillBytesPerSec = 4 << 20; // refilled at 4 MiB/s
    private static readonly TimeSpan EntryTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Reannounce = TimeSpan.FromSeconds(5);
    private const string DirTopic = " bsvp/dir";
    private const string DirQuery = " bsvp/dir?";
    private const string PresenceTopic = " bsvp/presence";

    private const int MaxInboundQueue = 8192;         // per-node frames awaiting processing before we shed load
    private const int MaxPeerOutQueue = 4096;          // per-peer frames awaiting the wire before we shed load

    // CONNECTION LIVENESS (robust cross-device play). A TCP connection through a NAT/firewall, or to a laptop
    // that sleeps or has its cable pulled, can go HALF-OPEN: the socket stays "connected" but no bytes ever
    // arrive again. The read loop blocks forever, the peer slot is never freed, and the dual-path redundancy
    // silently has a dead path. So: (1) OS-level TCP keepalive probes the link; (2) every KeepAliveInterval we
    // write a bare newline to each peer — a no-op frame the reader discards (acc.Length==0), but a WRITE that
    // faults promptly on a dead socket and keeps NAT mappings warm; (3) a reaper drops any peer with no inbound
    // bytes for PeerIdleTimeout, freeing the slot so discovery reconnects. A live peer is refreshed by its own
    // keepalive traffic, so it is never reaped.
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PeerIdleTimeout = TimeSpan.FromSeconds(45);   // > KeepAliveInterval ⇒ live peers survive
    private static readonly byte[] KeepAliveBytes = { (byte)'\n' };
    private Timer? _keepAliveTimer;

    private readonly int _port;
    private readonly string _bindHost;
    private TcpListener? _listener;
    private volatile bool _closed;
    private readonly ConcurrentDictionary<Guid, Peer> _peers = new();
    private readonly ConcurrentDictionary<string, List<Action<string>>> _subs = new();
    private readonly ConcurrentDictionary<string, byte> _seen = new();
    private readonly ConcurrentQueue<string> _seenOrder = new();
    private readonly ConcurrentDictionary<string, bool> _dialing = new();
    private readonly ConcurrentDictionary<string, (string Name, int Members, DateTime Exp)> _directory = new();
    private readonly ConcurrentDictionary<string, (string Addr, string Handle, DateTime Exp)> _presence = new();
    private readonly ConcurrentDictionary<string, TableAnnounce> _ownTables = new();
    private readonly ConcurrentDictionary<string, PresenceAnnounce> _ownPresence = new();
    private Timer? _reannounceTimer;

    // INBOUND PIPELINE: socket read loops must NEVER block on subscriber work (the mental-poker deal does
    // heavy EC crypto inside its callback). A read loop that stalls stops draining its socket, the OS recv
    // buffer fills, and a peer's blocking Write wedges — a back-pressure convoy that, once formed, never
    // recovers and only worsens with player count. So the read loops do nothing but split frames and hand
    // them to this bounded queue; a single dedicated consumer thread does all dedup/deliver/flood work.
    private readonly BlockingCollection<(string line, Guid from, RateState rate, int cost)> _inbound
        = new(new ConcurrentQueue<(string, Guid, RateState, int)>(), MaxInboundQueue);
    private Thread? _consumer;

    public sealed record TableAnnounce(string id, string name, int members);
    // playerId = the player's IDENTITY pubkey (hex); addr = their live endpoint (host:port); handle = their
    // self-attested @handle (signed alongside, so it cannot be spoofed). This is the "who's online" directory.
    public sealed record PresenceAnnounce(string playerId, string addr, string handle = "");
    public sealed record PeerAddr(string Host, int Port);
    private sealed record Frame(string t, string d, string id);
    private sealed class RateState { public double Tokens; public long Last; }

    // A connected peer owns its OWN outbound queue and writer thread. Flood enqueues (non-blocking) and moves
    // on, so a single slow/wedged peer can never block sends to the others — there is no global write lock.
    private sealed class Peer
    {
        public required TcpClient Sock;
        public required RateState Rate;
        public required BlockingCollection<byte[]> Out;
        public long LastSeen;   // Environment.TickCount64 of the last inbound bytes — drives the idle reaper
        public string? DialKey; // the "host:port" we dialled to create this peer (null for inbound); held in _dialing for the connection's lifetime so we never re-dial a peer we are already connected to
    }

    // SECURITY: default to LOOPBACK. The node listens only on 127.0.0.1 until the user explicitly opts in
    // to LAN/online play via EnableLan(). Outbound dials work regardless, so a player can still join others.
    public P2PNode(int port, string bindHost = "127.0.0.1") { _port = port; _bindHost = bindHost; }
    public bool LanEnabled { get; private set; }

    // Identity used to SIGN directory/presence announcements (audit 3.2). Set by the app before announcing;
    // presence is signed by the player's key so no one can announce presence as another player, and table
    // announcements carry the creator's key+signature. Unsigned/invalid announcements are dropped.
    private byte[]? _idPriv;
    private string? _idPubHex;
    public void SetIdentity(byte[] priv, byte[] pub) { _idPriv = priv; _idPubHex = Convert.ToHexString(pub).ToLowerInvariant(); }
    private string SignHex(string canonical) => _idPriv == null ? "" : Convert.ToHexString(Secp256k1.SignDigest(_idPriv, Hashes.Sha256d(Encoding.UTF8.GetBytes(canonical)))).ToLowerInvariant();
    private static bool VerifyHex(string pubHex, string canonical, string sigHex)
    {
        try { return pubHex.Length == 66 && Secp256k1.VerifyDigest(Convert.FromHexString(pubHex), Hashes.Sha256d(Encoding.UTF8.GetBytes(canonical)), Convert.FromHexString(sigHex)); }
        catch { return false; }
    }
    private static string TableCanon(string id, string name, int members) => $"tbl|{id}|{name}|{members}";
    private static string PresenceCanon(string playerId, string addr, string handle) => $"pres|{playerId}|{addr}|{handle}";

    private string TableJson(TableAnnounce a) => JsonSerializer.Serialize(new { a.id, a.name, a.members, pub = _idPubHex ?? "", sig = SignHex(TableCanon(a.id, a.name, a.members)) });
    private string PresenceJson(PresenceAnnounce p) => JsonSerializer.Serialize(new { p.playerId, p.addr, p.handle, sig = SignHex(PresenceCanon(p.playerId, p.addr, p.handle)) });

    public int BoundPort { get; private set; }
    public int PeerCount => _peers.Count;
    public long DroppedFrames { get; private set; }
    public string? LastDrop { get; private set; }
    private void Drop(string why) { DroppedFrames++; LastDrop = why; }

    public Task StartAsync(IReadOnlyList<PeerAddr>? peers = null)
    {
        var addr = _bindHost == "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(_bindHost);
        if (_bindHost == "0.0.0.0") LanEnabled = true;
        // Try the requested (well-known) port first so same-network peers can find us at a known port; if it is
        // already in use (e.g. a second instance on this machine), fall back to an ephemeral port automatically.
        try { _listener = new TcpListener(addr, _port); _listener.Start(); }
        catch { _listener = new TcpListener(addr, 0); _listener.Start(); }
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        StartConsumer();
        _keepAliveTimer = new Timer(_ => { try { SendKeepAlives(); ReapIdle((long)PeerIdleTimeout.TotalMilliseconds); } catch { } }, null, KeepAliveInterval, KeepAliveInterval);
        _ = AcceptLoop(_listener);
        Subscribe(DirTopic, OnDirAnnounce);
        Subscribe(PresenceTopic, OnPresenceAnnounce);
        Subscribe(DirQuery, _ => RepublishOwn());
        if (peers != null) foreach (var p in peers) Dial(p);
        _ = PublishAsync(DirQuery, Array.Empty<byte>());
        return Task.CompletedTask;
    }

    private async Task AcceptLoop(TcpListener l)
    {
        while (!_closed)
        {
            TcpClient s;
            try { s = await l.AcceptTcpClientAsync(); }
            catch { if (_closed || !ReferenceEquals(l, _listener)) return; else { await Task.Delay(50); continue; } }
            Adopt(s);
        }
    }

    /// <summary>
    /// Opt in to LAN/online play: rebind the listener from loopback to all interfaces (same port) so other
    /// machines can connect inbound. No-op if already enabled. Existing peer connections are unaffected.
    /// </summary>
    public void EnableLan()
    {
        if (_closed || LanEnabled) return;
        try
        {
            var old = _listener;
            var lan = new TcpListener(IPAddress.Any, BoundPort);
            lan.Start();
            _listener = lan;                 // the old accept loop exits (its listener != _listener)
            _ = AcceptLoop(lan);
            try { old?.Stop(); } catch { }
            LanEnabled = true;
        }
        catch { /* port unavailable for all-interfaces bind; stay loopback-only */ }
    }

    public void Dial(PeerAddr a)
    {
        var key = $"{a.Host}:{a.Port}";
        if (!_dialing.TryAdd(key, true)) return;
        _ = Task.Run(async () =>
        {
            while (!_closed)
            {
                try
                {
                    var s = new TcpClient();
                    await s.ConnectAsync(a.Host, a.Port);
                    // Keep `key` reserved in _dialing for as long as this connection lives (Adopt frees it when the
                    // socket closes) so a once-per-second discovery tick cannot pile up duplicate sockets to a peer
                    // we are already connected to.
                    Adopt(s, key);
                    _ = PublishAsync(DirQuery, Array.Empty<byte>());
                    return;
                }
                catch { await Task.Delay(200); }
            }
        });
    }

    private void Adopt(TcpClient sock, string? dialKey = null)
    {
        if (_peers.Count >= MaxPeers) { try { sock.Dispose(); } catch { } if (dialKey != null) _dialing.TryRemove(dialKey, out _); return; }
        var id = Guid.NewGuid();
        var rate = new RateState { Tokens = RateCapacityBytes, Last = Environment.TickCount64 };
        var outq = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>(), MaxPeerOutQueue);
        var peer = new Peer { Sock = sock, Rate = rate, Out = outq, LastSeen = Environment.TickCount64, DialKey = dialKey };
        _peers[id] = peer;
        sock.NoDelay = true;
        EnableTcpKeepAlive(sock);

        // WRITER: one dedicated thread per peer drains its outbound queue with blocking writes. A wedged
        // peer can only back up (then shed) its OWN queue — it cannot stall this node's other sends.
        new Thread(() =>
        {
            try
            {
                var stream = sock.GetStream();
                foreach (var payload in outq.GetConsumingEnumerable())
                {
                    if (_closed) break;
                    stream.Write(payload, 0, payload.Length);
                }
            }
            catch { }
            finally { try { sock.Dispose(); } catch { } _peers.TryRemove(id, out _); if (dialKey != null) _dialing.TryRemove(dialKey, out _); }
        }) { IsBackground = true, Name = "p2p-writer" }.Start();

        // READER: do NOTHING but byte-accurate framing + hand each frame to the shared inbound queue. No
        // dedup, no deliver, no flood here — those happen on the consumer thread so heavy subscriber work
        // can never stall this socket's draining.
        _ = Task.Run(async () =>
        {
            var acc = new MemoryStream();
            var bytes = new byte[8192];
            bool skipping = false;
            try
            {
                var stream = sock.GetStream();
                while (!_closed)
                {
                    int n = await stream.ReadAsync(bytes);
                    if (n <= 0) break;
                    peer.LastSeen = Environment.TickCount64;   // any inbound bytes (incl. keepalive newlines) prove the peer is live
                    for (int i = 0; i < n; i++)
                    {
                        byte ch = bytes[i];
                        if (ch == (byte)'\n')
                        {
                            if (skipping) { skipping = false; acc.SetLength(0); continue; }
                            if (acc.Length > 0) { Enqueue(Encoding.UTF8.GetString(acc.ToArray()), id, rate, (int)acc.Length); acc.SetLength(0); }
                        }
                        else if (skipping) { /* discard until newline */ }
                        else
                        {
                            acc.WriteByte(ch);
                            if (acc.Length > MaxFrameBytes) { Drop("oversize frame"); skipping = true; acc.SetLength(0); }
                        }
                    }
                }
            }
            catch { }
            finally { try { outq.CompleteAdding(); } catch { } _peers.TryRemove(id, out _); try { sock.Dispose(); } catch { } if (dialKey != null) _dialing.TryRemove(dialKey, out _); }
        });
    }

    // Non-blocking handoff from a socket read loop to the consumer. Under genuine overload we SHED (drop the
    // frame) rather than block the read loop — gossip is loss-tolerant (frames are re-broadcast), and a
    // never-blocking reader is what keeps the mesh from convoying.
    private void Enqueue(string line, Guid from, RateState rate, int cost)
    {
        if (_closed) return;
        if (!_inbound.TryAdd((line, from, rate, cost))) Drop("inbound overflow");
    }

    private void StartConsumer()
    {
        if (_consumer != null) return;
        _consumer = new Thread(() =>
        {
            try { foreach (var item in _inbound.GetConsumingEnumerable()) { if (_closed) break; OnFrame(item.line, item.from, item.rate, item.cost); } }
            catch { }
        }) { IsBackground = true, Name = "p2p-consumer" };
        _consumer.Start();
    }

    // token bucket measured in BYTES, so a few large frames cost as much as many small ones (byte-cost
    // rate limiting, not frame-count) — an attacker cannot bypass the limit with big frames.
    private bool Allow(RateState r, int costBytes)
    {
        long now = Environment.TickCount64;
        r.Tokens = Math.Min(RateCapacityBytes, r.Tokens + (now - r.Last) / 1000.0 * RateRefillBytesPerSec);
        r.Last = now;
        if (r.Tokens < costBytes) return false;
        r.Tokens -= costBytes; return true;
    }

    private void OnFrame(string line, Guid from, RateState rate, int costBytes)
    {
        if (!Allow(rate, costBytes)) { Drop("rate limit"); return; }
        Frame? f;
        try { f = JsonSerializer.Deserialize<Frame>(line); if (f?.t == null || f.d == null || f.id == null) { Drop("malformed frame"); return; } } catch { Drop("parse error"); return; }
        if (Encoding.UTF8.GetByteCount(f.t) > MaxTopicBytes) { Drop("topic too long"); return; }
        if (MarkSeen(f.id)) return;
        DeliverLocal(f);
        Flood(line, from);
    }

    private bool MarkSeen(string id)
    {
        if (!_seen.TryAdd(id, 1)) return true;
        _seenOrder.Enqueue(id);
        while (_seenOrder.Count > MaxSeen && _seenOrder.TryDequeue(out var old)) _seen.TryRemove(old, out _);
        return false;
    }

    private void DeliverLocal(Frame f)
    {
        if (!_subs.TryGetValue(f.t, out var set)) return;
        string text;
        try { var raw = Convert.FromBase64String(f.d); if (raw.Length > MaxPayloadBytes) { Drop("payload too large"); return; } text = Encoding.UTF8.GetString(raw); }
        catch { Drop("bad base64"); return; }
        Action<string>[] cbs; lock (set) cbs = set.ToArray();
        foreach (var cb in cbs) { try { cb(text); } catch { } }
    }

    private void Flood(string line, Guid? except = null)
    {
        var payload = Encoding.UTF8.GetBytes(line + "\n");
        foreach (var kv in _peers)
        {
            if (except is { } ex && kv.Key == ex) continue;
            // Non-blocking enqueue to the peer's own writer. If that peer is wedged and its queue is full we
            // shed the frame for THAT peer only — the rest of the mesh is unaffected (no global lock).
            try { if (!kv.Value.Out.TryAdd(payload)) Drop("peer out overflow"); } catch { }
        }
    }

    private void OnDirAnnounce(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            string id = r.GetProperty("id").GetString() ?? "", name = r.GetProperty("name").GetString() ?? "";
            int members = r.TryGetProperty("members", out var m) ? m.GetInt32() : 0;
            string pub = r.TryGetProperty("pub", out var pe) ? pe.GetString() ?? "" : "";
            string sig = r.TryGetProperty("sig", out var se) ? se.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(id)) { Drop("table: empty id"); return; }
            if (!VerifyHex(pub, TableCanon(id, name, members), sig)) { Drop("table: bad/missing signature"); return; }
            // members < 0 is a CLOSE signal: the host left/ended the table — drop it from the directory NOW so it
            // never lingers as a ghost "open" table waiting on the TTL.
            if (members < 0) { _directory.TryRemove(id, out _); return; }
            if (!_directory.ContainsKey(id) && _directory.Count >= MaxDirectory) { var oldest = _directory.Keys.FirstOrDefault(); if (oldest != null) _directory.TryRemove(oldest, out _); }
            _directory[id] = (name, members, DateTime.UtcNow + EntryTtl);
        }
        catch { Drop("table: parse error"); }
    }

    private void OnPresenceAnnounce(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            string playerId = r.GetProperty("playerId").GetString() ?? "", addr = r.GetProperty("addr").GetString() ?? "";
            string handle = r.TryGetProperty("handle", out var he) ? he.GetString() ?? "" : "";
            string sig = r.TryGetProperty("sig", out var se) ? se.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(playerId) || string.IsNullOrEmpty(addr)) { Drop("presence: empty"); return; }
            // presence MUST be signed by the player's own key (playerId is that pubkey) — no spoofing others, and
            // the handle is covered by the signature so a player cannot claim another player's @handle either.
            if (!VerifyHex(playerId, PresenceCanon(playerId, addr, handle), sig)) { Drop("presence: bad/missing signature"); return; }
            if (!_presence.ContainsKey(playerId) && _presence.Count >= MaxDirectory) { var oldest = _presence.Keys.FirstOrDefault(); if (oldest != null) _presence.TryRemove(oldest, out _); }
            _presence[playerId] = (addr, handle, DateTime.UtcNow + EntryTtl);
        }
        catch { Drop("presence: parse error"); }
    }

    private void RepublishOwn()
    {
        foreach (var a in _ownTables.Values) _ = PublishAsync(DirTopic, Encoding.UTF8.GetBytes(TableJson(a)));
        foreach (var p in _ownPresence.Values) _ = PublishAsync(PresenceTopic, Encoding.UTF8.GetBytes(PresenceJson(p)));
    }

    private void EnsureReannounce() { if (_reannounceTimer == null && !_closed) _reannounceTimer = new Timer(_ => RepublishOwn(), null, Reannounce, Reannounce); }

    public Task HeartbeatAsync(string playerId, string addr, string handle = "")
    {
        var a = new PresenceAnnounce(playerId, addr, handle);
        _ownPresence[playerId] = a; OnPresenceAnnounce(PresenceJson(a)); EnsureReannounce();
        return PublishAsync(PresenceTopic, Encoding.UTF8.GetBytes(PresenceJson(a)));
    }

    public IReadOnlyList<PresenceAnnounce> ListPresence()
    {
        var now = DateTime.UtcNow; var outp = new List<PresenceAnnounce>();
        foreach (var kv in _presence.ToArray()) { if (kv.Value.Exp <= now) { _presence.TryRemove(kv.Key, out _); continue; } outp.Add(new PresenceAnnounce(kv.Key, kv.Value.Addr, kv.Value.Handle)); }
        return outp;
    }

    public Task<TableAnnounce> CreateTableAsync(string id, string name)
    {
        var a = new TableAnnounce(id, name, 1);
        _ownTables[id] = a; OnDirAnnounce(TableJson(a)); EnsureReannounce();
        _ = PublishAsync(DirTopic, Encoding.UTF8.GetBytes(TableJson(a)));
        return Task.FromResult(a);
    }

    /// <summary>The ids of the tables THIS node is hosting (created here).</summary>
    public IReadOnlyCollection<string> OwnTableIds => _ownTables.Keys.ToArray();

    /// <summary>END a table this node hosts: stop re-announcing it, drop it locally, and tell every peer to drop
    /// it immediately (a members=-1 close signal) so it never lingers as a ghost "open" table. Called when the
    /// host leaves the table or closes the app.</summary>
    public Task CloseTable(string id)
    {
        _ownTables.TryRemove(id, out _);          // stop re-announcing it
        _directory.TryRemove(id, out _);          // drop it from our own view
        var close = new TableAnnounce(id, "", -1);
        return PublishAsync(DirTopic, Encoding.UTF8.GetBytes(TableJson(close)));   // peers drop it now
    }

    /// <summary>END every table this node hosts (e.g. on app close).</summary>
    public void CloseAllOwnTables() { foreach (var id in _ownTables.Keys.ToArray()) { try { _ = CloseTable(id); } catch { } } }

    public IReadOnlyList<TableAnnounce> ListTables()
    {
        var now = DateTime.UtcNow; var outp = new List<TableAnnounce>();
        foreach (var kv in _directory.ToArray()) { if (kv.Value.Exp <= now) { _directory.TryRemove(kv.Key, out _); continue; } outp.Add(new TableAnnounce(kv.Key, kv.Value.Name, kv.Value.Members)); }
        return outp;
    }

    public Action Subscribe(string tableId, Action<string> onEvent)
    {
        var set = _subs.GetOrAdd(tableId, _ => new List<Action<string>>());
        lock (set) set.Add(onEvent);
        return () => { lock (set) set.Remove(onEvent); };
    }

    /// <summary>
    /// Publish a frame. If <paramref name="id"/> is given it is the frame's dedup key: re-publishing the SAME
    /// logical message (same id) is delivered to local subscribers only ONCE, but is always re-flooded so a
    /// peer that dropped the first copy can still catch up — and every peer that already saw it discards the
    /// re-flood at the cheap seen-check, BEFORE the expensive signature verify. Callers that want every call
    /// treated as new (announcements, queries) pass no id and get a random one.
    /// </summary>
    public Task<int> PublishAsync(string tableId, byte[] payload, string? id = null)
    {
        var fid = id ?? RandomId();
        var f = new Frame(tableId, Convert.ToBase64String(payload), fid);
        bool already = MarkSeen(fid);
        if (!already) DeliverLocal(f);                 // local echo only the first time this frame is published
        Flood(JsonSerializer.Serialize(f));            // always reflood: retries reach peers that shed the first copy
        return Task.FromResult(_peers.Count);
    }

    private static string RandomId() { Span<byte> b = stackalloc byte[12]; RandomNumberGenerator.Fill(b); return Convert.ToHexString(b).ToLowerInvariant(); }

    // Ask the OS to probe the link with TCP keepalive (so the kernel itself tears down a dead connection). The
    // tuning options are not available on every platform, so each is best-effort.
    private static void EnableTcpKeepAlive(TcpClient sock)
    {
        try { sock.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch { }
        try { sock.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15); } catch { }
        try { sock.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5); } catch { }
        try { sock.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3); } catch { }
    }

    /// <summary>Write a bare-newline keepalive to every peer. The receiving reader discards it (an empty frame),
    /// but the WRITE faults promptly on a dead socket — which removes that peer — and keeps NAT mappings warm on
    /// live ones. Called automatically on a timer; exposed for tests/diagnostics.</summary>
    public void SendKeepAlives()
    {
        if (_closed) return;
        foreach (var kv in _peers) { try { kv.Value.Out.TryAdd(KeepAliveBytes); } catch { } }
    }

    /// <summary>Drop every peer that has produced NO inbound bytes for longer than <paramref name="idleMs"/>
    /// (a half-open connection the read loop would otherwise block on forever). Disposing the socket unblocks the
    /// reader, whose finally-block also runs; but we free the peer's dial key HERE too, synchronously with peer
    /// removal, so discovery can re-dial the peer on its next tick without waiting on the reader thread to wake.
    /// Returns the number reaped.</summary>
    public int ReapIdle(long idleMs)
    {
        long now = Environment.TickCount64; int reaped = 0;
        foreach (var kv in _peers.ToArray())
        {
            if (now - kv.Value.LastSeen <= idleMs) continue;
            if (_peers.TryRemove(kv.Key, out var p))
            {
                reaped++;
                if (p.DialKey != null) _dialing.TryRemove(p.DialKey, out _);   // re-dialable immediately (reader finally is an idempotent backstop)
                try { p.Out.CompleteAdding(); } catch { }
                try { p.Sock.Dispose(); } catch { }
            }
        }
        return reaped;
    }

    /// <summary>The age (ms since last inbound bytes) of every connected peer — for liveness diagnostics/tests.</summary>
    public IReadOnlyList<long> PeerIdleAgesMs() { long now = Environment.TickCount64; return _peers.Values.Select(p => now - p.LastSeen).ToList(); }

    /// <summary>TEST/DIAGNOSTIC seam: backdate every peer's last-seen by <paramref name="ageMs"/> so a subsequent
    /// <see cref="ReapIdle"/> can deterministically exercise half-open pruning without waiting on real timeouts.</summary>
    public void ForcePeersStale(long ageMs) { long t = Environment.TickCount64 - ageMs; foreach (var p in _peers.Values) p.LastSeen = t; }

    public void Dispose()
    {
        _closed = true; _reannounceTimer?.Dispose(); _keepAliveTimer?.Dispose();
        try { _listener?.Stop(); } catch { }
        try { _inbound.CompleteAdding(); } catch { }   // let the consumer thread fall out of its loop
        foreach (var kv in _peers) { try { kv.Value.Out.CompleteAdding(); } catch { } try { kv.Value.Sock.Dispose(); } catch { } }
        _peers.Clear();
    }
}
