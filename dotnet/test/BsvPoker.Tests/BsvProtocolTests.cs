using System.Net;
using System.Net.Sockets;
using BsvPoker.Net.Bsv;

namespace BsvPoker.Tests;

/// <summary>
/// The BSV peer-node wire protocol: the message envelope codec (with checksum) and the version/verack
/// handshake between two real peers. This is the foundation of the client being a node on the network.
/// </summary>
public static class BsvProtocolTests
{
    public static void All()
    {
        Console.WriteLine("BSV peer node — P2P wire protocol:");

        T.Run("network params: real BSV mainnet magic, port, and address versions", () =>
        {
            var m = NetworkParams.For(BsvNetwork.Mainnet);
            T.Eq(T.Hex(m.Magic).ToUpperInvariant(), "E3E1F3E8", "mainnet pchMessageStart");
            T.Eq(m.DefaultPort, 8333, "mainnet port");
            T.Eq((int)m.AddressVersion, 0x00); T.Eq((int)m.WifVersion, 0x80);
            var r = NetworkParams.For(BsvNetwork.Regtest);
            T.Eq(T.Hex(r.Magic).ToUpperInvariant(), "DAB5BFFA", "regtest magic");
        });

        T.Run("message envelope encodes and decodes (command, payload, checksum) round-trip", () =>
        {
            var magic = NetworkParams.For(BsvNetwork.Regtest).Magic;
            var msg = new BsvMessage("ping", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            var bytes = msg.Encode(magic);
            var status = BsvMessage.TryDecode(bytes, magic, out var got, out int consumed);
            T.Eq(status.ToString(), "Ok"); T.Eq(consumed, bytes.Length);
            T.Eq(got!.Command, "ping"); T.Eq(T.Hex(got.Payload), T.Hex(msg.Payload));
        });

        T.Run("decode rejects a wrong magic, a bad checksum, and reports NeedMore on truncation", () =>
        {
            var magic = NetworkParams.For(BsvNetwork.Regtest).Magic;
            var bytes = new BsvMessage("tx", new byte[] { 9, 9, 9 }).Encode(magic);
            T.Eq(BsvMessage.TryDecode(bytes, NetworkParams.For(BsvNetwork.Mainnet).Magic, out _, out _).ToString(), "BadMagic");
            var tampered = (byte[])bytes.Clone(); tampered[21] ^= 0xFF; // corrupt a checksum byte
            T.Eq(BsvMessage.TryDecode(tampered, magic, out _, out _).ToString(), "BadChecksum");
            T.Eq(BsvMessage.TryDecode(bytes.AsSpan(0, 10), magic, out _, out _).ToString(), "NeedMore");
        });

        T.Run("version round-trips: user-agent and start height survive build → parse", () =>
        {
            var payload = BsvVersion.Build(startHeight: 12345, nonce: 0xABCDEF);
            var info = BsvVersion.Parse(payload);
            T.Eq(info.Version, BsvVersion.ProtocolVersion);
            T.Eq(info.StartHeight, 12345);
            T.True(info.UserAgent.Contains("BsvPoker"), "user-agent preserved");
        });

        T.Run("mainnet/testnet have DNS seeds configured (the node can find the live network)", () =>
        {
            T.True(NetworkParams.For(BsvNetwork.Mainnet).DnsSeeds.Length > 0, "mainnet seeds");
            T.True(NetworkParams.For(BsvNetwork.Testnet).DnsSeeds.Length > 0, "testnet seeds");
        });

        T.Run("BsvNode dials a peer, completes the handshake, and tracks the chain tip", () =>
        {
            var net = NetworkParams.For(BsvNetwork.Regtest);
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accept = listener.AcceptTcpClientAsync();
            using var node = new BsvNode(net);
            var connect = node.ConnectAsync("127.0.0.1", port);
            using var remote = new BsvPeer(net, accept.GetAwaiter().GetResult());
            var remoteHs = remote.HandshakeAsync(startHeight: 654321);
            T.True(connect.GetAwaiter().GetResult(), "BsvNode connected + handshaked");
            Task.WaitAll(new[] { remoteHs }, 10000);
            T.True(node.PeerCount >= 1, "peer tracked");
            T.Eq(node.BestHeight, 654321, "best height learned from the peer");
            listener.Stop();
        });

        T.Run("two peers complete the version/verack handshake over the real wire protocol", () =>
        {
            var net = NetworkParams.For(BsvNetwork.Regtest);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var acceptTask = listener.AcceptTcpClientAsync();
            var client = new TcpClient(); client.Connect(IPAddress.Loopback, port);
            using var inbound = new BsvPeer(net, acceptTask.GetAwaiter().GetResult());
            using var outbound = new BsvPeer(net, client);
            var a = outbound.HandshakeAsync(startHeight: 1);
            var b = inbound.HandshakeAsync(startHeight: 2);
            T.True(Task.WaitAll(new[] { a, b }, 15000), "both handshakes finished in time");
            T.True(outbound.Handshaked && inbound.Handshaked, "both peers reached version+verack");
            T.True(outbound.RemoteVersion!.UserAgent.Contains("BsvPoker"), "outbound saw the peer's version");
            T.Eq(outbound.RemoteVersion!.StartHeight, 2, "outbound learned the peer's start height");
            listener.Stop();
        });
    }
}
