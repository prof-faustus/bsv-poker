using System.Net;
using System.Net.Sockets;
using BsvPoker.Core;

namespace BsvPoker.Net.Bsv;

/// <summary>
/// The ONLY allowed player-to-player transport: it carries nothing but Bitcoin transactions. To send a
/// message/action, a player builds a Bitcoin TRANSACTION and pushes it IP-to-IP straight to the other
/// player as a Bitcoin-wire <c>tx</c> message (instant delivery, no waiting for a block) — and the SAME
/// transaction is broadcast to the mining nodes so it is stored on-chain. There is no other kind of packet:
/// every byte on this socket is a Bitcoin transaction (with the standard version/verack handshake). The
/// receiver parses the incoming tx and interprets it (chat, bet, deal, …) via its typed output.
/// </summary>
public sealed class TxLink : IDisposable
{
    private readonly NetworkParams _net;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ulong _nonce = (ulong)Random.Shared.NextInt64() ^ (ulong)Environment.TickCount64;

    /// <summary>Raised when a peer pushes us a Bitcoin transaction directly (IP-to-IP).</summary>
    public event Action<Chain.Tx>? OnTransaction;
    public int Port { get; private set; }

    /// <param name="bindAddress">loopback by default; pass IPAddress.Any to accept real IP-to-IP from other machines.</param>
    public TxLink(NetworkParams net, int port = 0, IPAddress? bindAddress = null)
    {
        _net = net;
        _listener = new TcpListener(bindAddress ?? IPAddress.Loopback, port);
    }

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using var c = client;
            c.NoDelay = true;
            var s = c.GetStream();
            var acc = new List<byte>();
            var buf = new byte[64 * 1024];
            while (!_cts.IsCancellationRequested)
            {
                int n;
                try { n = await s.ReadAsync(buf, _cts.Token); } catch { break; }
                if (n <= 0) break;
                acc.AddRange(buf.AsSpan(0, n).ToArray());
                while (true)
                {
                    var status = BsvMessage.TryDecode(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(acc), _net.Magic, out var msg, out int consumed);
                    if (status == BsvMessage.DecodeStatus.NeedMore) break;
                    if (status != BsvMessage.DecodeStatus.Ok) { acc.Clear(); break; } // only Bitcoin frames are accepted
                    acc.RemoveRange(0, consumed);
                    switch (msg!.Command)
                    {
                        case "version":
                            Write(s, new BsvMessage("version", BsvVersion.Build(0, _nonce)).Encode(_net.Magic));
                            Write(s, new BsvMessage("verack", Array.Empty<byte>()).Encode(_net.Magic));
                            break;
                        case "tx":
                            try { int o = 0; var tx = Chain.Deserialize(msg.Payload, ref o); OnTransaction?.Invoke(tx); } catch { }
                            break;
                    }
                }
            }
        }
        catch { }
    }

    private static void Write(NetworkStream s, byte[] b) { try { s.Write(b, 0, b.Length); } catch { } }

    /// <summary>
    /// Push a raw Bitcoin transaction directly to a peer at host:port (the Bitcoin-wire <c>tx</c> message,
    /// after the version/verack handshake). This is the IP-to-IP delivery; broadcasting the same tx to the
    /// mining nodes (via <see cref="BsvNode.Broadcast"/>) is the caller's parallel step.
    /// </summary>
    public static async Task<bool> SendTxAsync(NetworkParams net, string host, int port, byte[] rawTx, int timeoutMs = 6000)
    {
        try
        {
            using var c = new TcpClient();
            await c.ConnectAsync(host, port).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            var peer = new BsvPeer(net, c);
            await peer.HandshakeAsync(startHeight: 0, timeoutMs: timeoutMs);
            peer.Send("tx", rawTx);
            await Task.Delay(20); // brief flush before close — INSTANT delivery (was 150ms; loopback round-trip ~50ms)
            peer.Dispose();
            return true;
        }
        catch { return false; }
    }

    public void Dispose() { _cts.Cancel(); try { _listener.Stop(); } catch { } }
}
