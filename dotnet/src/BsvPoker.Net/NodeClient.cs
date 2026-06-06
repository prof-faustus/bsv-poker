using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BsvPoker.Net;

/// <summary>
/// The CLIENT side of on-chain broadcast: submits a fully-signed transaction to a configured BSV node's
/// JSON-RPC endpoint (the node itself is a separate project) and returns the txid. The SAME code path
/// serves regtest, testnet, and mainnet — only the endpoint URL and credentials differ, never the logic.
///
/// The transactions it broadcasts (P2PKH spends, the 2-of-2 escrow, cooperative settlement, and the
/// nLockTime recovery) are built and strictly verified in-process by <see cref="BsvPoker.Core.Chain"/>;
/// this class is purely the transport to a node. It does no signing and holds no keys.
/// </summary>
public sealed class NodeClient
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string? _user, _pass;

    /// <param name="endpoint">The node JSON-RPC URL, e.g. http://127.0.0.1:18332 (regtest) or your testnet/mainnet node.</param>
    /// <param name="handler">Optional message handler (used by tests to mock the node); a real HttpClient otherwise.</param>
    public NodeClient(string endpoint, string? user = null, string? pass = null, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("endpoint required");
        _endpoint = endpoint; _user = user; _pass = pass;
        _http = handler != null ? new HttpClient(handler) : new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>Broadcast a signed transaction (raw bytes). Returns the txid, or throws if the node rejects it.</summary>
    public async Task<string> BroadcastAsync(byte[] rawTx, CancellationToken ct = default)
    {
        if (rawTx == null || rawTx.Length == 0) throw new ArgumentException("empty transaction");
        var hex = Convert.ToHexString(rawTx).ToLowerInvariant();
        var body = JsonSerializer.Serialize(new { jsonrpc = "1.0", id = "bsvpoker", method = "sendrawtransaction", @params = new[] { hex } });
        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (_user != null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_user}:{_pass}")));
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch { throw new InvalidOperationException($"node returned non-JSON (HTTP {(int)resp.StatusCode}): {Trim(text)}"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
                throw new InvalidOperationException("node rejected the transaction: " + err.GetRawText());
            if (root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String)
                return res.GetString()!;
            throw new InvalidOperationException($"unexpected node response (HTTP {(int)resp.StatusCode}): {Trim(text)}");
        }
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] + "…" : s;
}
