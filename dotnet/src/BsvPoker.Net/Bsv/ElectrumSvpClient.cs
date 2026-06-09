using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using BsvPoker.Core;
using BsvPoker.Crypto;

namespace BsvPoker.Net.Bsv;

/// <summary>
/// A minimal ElectrumSVP client — the BACKUP funding/discovery path, using the SAME public servers ElectrumSVP
/// uses (gorillapool, satoshi.io, bitails, aftrek). These are run by the network, not us. We use them only to
/// LOOK UP an address's unspent coins and to fetch the merkle proof + block header for each; every coin is then
/// SPV-verified locally (the proof must fold to the header's merkle root and the header must meet proof-of-work),
/// so a malicious server cannot make us credit money that was not really mined. No trust beyond verifiable PoW.
/// </summary>
public sealed class ElectrumSvpClient : IDisposable
{
    public sealed record Utxo(string TxHashDisplay, uint Vout, long Value, int Height);
    public sealed record VerifiedUtxo(string TxidDisplay, uint Vout, long Value, int Height);

    // ElectrumSVP servers from ElectrumSVP (data/servers.json + network.py). SSL port 50002.
    public static readonly (string Host, int Port)[] Mainnet =
    {
        ("electrumx.gorillapool.io", 50002), ("sv.satoshi.io", 50002), ("sv2.satoshi.io", 50002),
        ("esv.bitails.io", 50002), ("bsv.aftrek.org", 50002),
    };
    public static readonly (string Host, int Port)[] Testnet =
    {
        ("electrontest.cascharia.com", 51002), ("tsv.usebsv.com", 51002),
    };

    public static (string Host, int Port)[] ServersFor(BsvNetwork net) => net switch
    {
        BsvNetwork.Testnet => Testnet, BsvNetwork.Mainnet => Mainnet, _ => Array.Empty<(string, int)>(),
    };

    private TcpClient? _tcp;
    private SslStream? _ssl;
    private StreamReader? _reader;
    private int _id;
    public string? Host { get; private set; }

    /// <summary>Connect to the first reachable server over TLS (ElectrumSVP uses self-signed certs, so we accept any).</summary>
    public async Task<bool> ConnectAnyAsync(IEnumerable<(string Host, int Port)> servers, int timeoutMs = 7000, Action<string>? log = null)
    {
        foreach (var (host, port) in servers)
        {
            try
            {
                var tcp = new TcpClient();
                await tcp.ConnectAsync(host, port).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
                var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true); // ElectrumSVP self-signed: verify PoW, not the cert
                await ssl.AuthenticateAsClientAsync(host).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
                _tcp = tcp; _ssl = ssl; _reader = new StreamReader(ssl, Encoding.UTF8); Host = host;
                await CallAsync("server.version", timeoutMs, "bsvpoker", "1.4");
                log?.Invoke($"ElectrumSVP connected: {host}:{port}");
                return true;
            }
            catch (Exception ex) { log?.Invoke($"ElectrumSVP {host}:{port} failed: {ex.Message}"); }
        }
        return false;
    }

    private async Task<JsonElement> CallAsync(string method, int timeoutMs, params object[] prms)
    {
        if (_ssl == null || _reader == null) throw new InvalidOperationException("not connected");
        int id = ++_id;
        var req = JsonSerializer.Serialize(new { id, method, @params = prms });
        var bytes = Encoding.UTF8.GetBytes(req + "\n");
        await _ssl.WriteAsync(bytes).AsTask().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        await _ssl.FlushAsync().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        // read lines until we get our id (servers may interleave notifications)
        while (true)
        {
            var line = await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            if (line == null) throw new IOException("server closed");
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.GetInt32() == id)
            {
                if (root.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
                    throw new Exception("electrumx error: " + err.ToString());
                return root.TryGetProperty("result", out var res) ? res.Clone() : default;
            }
        }
    }

    /// <summary>The Electrum "script hash": sha256(scriptPubKey), byte-reversed, hex — how addresses are queried.</summary>
    public static string ScriptHashOf(byte[] scriptPubKey)
    {
        var h = Hashes.Sha256(scriptPubKey);
        Array.Reverse(h);
        return Convert.ToHexString(h).ToLowerInvariant();
    }

    /// <summary>Broadcast a raw transaction to the network via the server. Returns the txid, or throws with the
    /// server's rejection reason (so a bad/underfunded tx tells you why).</summary>
    public async Task<string> BroadcastAsync(string rawTxHex, int timeoutMs = 12000)
    {
        var res = await CallAsync("blockchain.transaction.broadcast", timeoutMs, rawTxHex);
        return res.GetString() ?? "";
    }

    /// <summary>Fetch a transaction's raw bytes by txid (light — no full-block download).</summary>
    public async Task<byte[]> GetTransactionAsync(string txidDisplay, int timeoutMs = 9000)
    {
        var res = await CallAsync("blockchain.transaction.get", timeoutMs, txidDisplay, false);
        return Convert.FromHexString(res.GetString() ?? "");
    }

    /// <summary>The confirmed height of a txid via the address history of its first output (so we can fetch its
    /// merkle proof). Returns 0 if unknown/unconfirmed.</summary>
    public async Task<int> HeightOfTxAsync(string txidDisplay, byte[] anOutputScript, int timeoutMs = 9000)
    {
        var sh = ScriptHashOf(anOutputScript);
        var res = await CallAsync("blockchain.scripthash.get_history", timeoutMs, sh);
        if (res.ValueKind == JsonValueKind.Array)
            foreach (var h in res.EnumerateArray())
                if ((h.GetProperty("tx_hash").GetString() ?? "") == txidDisplay)
                    return h.TryGetProperty("height", out var ht) ? ht.GetInt32() : 0;
        return 0;
    }

    public async Task<List<Utxo>> ListUnspentAsync(string scriptHash, int timeoutMs = 7000)
    {
        var res = await CallAsync("blockchain.scripthash.listunspent", timeoutMs, scriptHash);
        var outp = new List<Utxo>();
        if (res.ValueKind == JsonValueKind.Array)
            foreach (var u in res.EnumerateArray())
                outp.Add(new Utxo(u.GetProperty("tx_hash").GetString() ?? "", (uint)u.GetProperty("tx_pos").GetInt32(),
                    u.GetProperty("value").GetInt64(), u.TryGetProperty("height", out var h) ? h.GetInt32() : 0));
        return outp;
    }

    /// <summary>Fetch the merkle proof for a tx and the block header at its height, and SPV-verify locally:
    /// PoW on the header + the merkle branch folding to the header's root. Returns true if the coin is proven.</summary>
    public async Task<bool> VerifyUtxoAsync(string txHashDisplay, int height, int timeoutMs = 9000)
    {
        var m = await CallAsync("blockchain.transaction.get_merkle", timeoutMs, txHashDisplay, height);
        int pos = m.GetProperty("pos").GetInt32();
        var branch = new List<byte[]>();
        foreach (var b in m.GetProperty("merkle").EnumerateArray()) { var x = Convert.FromHexString(b.GetString()!); Array.Reverse(x); branch.Add(x); }
        var hdrHex = (await CallAsync("blockchain.block.header", timeoutMs, height)).GetString();
        if (string.IsNullOrEmpty(hdrHex)) return false;
        var header = BlockHeader.Parse(Convert.FromHexString(hdrHex));
        if (!header.MeetsPow()) return false;                                  // real proof-of-work — server cannot forge this
        var leaf = Convert.FromHexString(txHashDisplay); Array.Reverse(leaf);  // txid → internal byte order
        return MerkleProof.Verify(leaf, pos, branch.ToArray(), header.MerkleRoot);
    }

    public void Dispose() { try { _ssl?.Dispose(); } catch { } try { _tcp?.Dispose(); } catch { } }
}
