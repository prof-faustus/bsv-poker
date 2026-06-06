using System.Net;
using System.Net.Http;
using System.Text;
using BsvPoker.Core;
using BsvPoker.Crypto;
using BsvPoker.Net;

namespace BsvPoker.Tests;

/// <summary>
/// The node-broadcast client: submits a fully-signed transaction to a BSV node's JSON-RPC endpoint and
/// returns the txid. Tested against a mock node (a fake HttpMessageHandler) so no live node is needed.
/// </summary>
public static class NodeClientTests
{
    private sealed class MockHandler : HttpMessageHandler
    {
        private readonly Func<string, (HttpStatusCode, string)> _respond;
        public string? LastBody;
        public MockHandler(Func<string, (HttpStatusCode, string)> respond) => _respond = respond;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            var (code, json) = _respond(LastBody);
            return new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    public static void All()
    {
        Console.WriteLine("node-client broadcast (submit signed txs to a BSV node):");

        T.Run("broadcasts a signed tx via JSON-RPC sendrawtransaction and returns the txid", () =>
        {
            var h = new MockHandler(_ => (HttpStatusCode.OK, "{\"result\":\"deadbeefcafe\",\"error\":null,\"id\":\"bsvpoker\"}"));
            var client = new NodeClient("http://127.0.0.1:18332", "user", "pass", h);
            var seed = T.Seed(5); var pub = Secp256k1.PublicKeyCompressed(seed);
            var tx = new Chain.Tx(2, new() { new("aa".PadRight(64, 'b'), 0, Array.Empty<byte>(), 0xffffffff) }, new() { new(1000, Chain.P2pkhLockForPub(pub)) }, 0);
            var raw = Chain.Serialize(Chain.SignP2pkhInput(tx, 0, seed, pub, 2000));
            var txid = client.BroadcastAsync(raw).Result;
            T.Eq(txid, "deadbeefcafe", "returns the node's txid");
            T.True(h.LastBody!.Contains("sendrawtransaction"), "calls sendrawtransaction");
            T.True(h.LastBody!.Contains(Convert.ToHexString(raw).ToLowerInvariant()), "submits the exact raw tx hex");
        });

        T.Run("a node rejection (JSON-RPC error) throws rather than silently succeeding", () =>
        {
            var h = new MockHandler(_ => (HttpStatusCode.OK, "{\"result\":null,\"error\":{\"code\":-26,\"message\":\"min relay fee not met\"}}"));
            var client = new NodeClient("http://127.0.0.1:18332", handler: h);
            T.Throws(() => { _ = client.BroadcastAsync(new byte[] { 1, 2, 3 }).Result; }, "rejection surfaces as an exception");
        });

        T.Run("non-JSON / HTML error pages from a misconfigured endpoint throw cleanly", () =>
        {
            var h = new MockHandler(_ => (HttpStatusCode.InternalServerError, "<html>500</html>"));
            var client = new NodeClient("http://127.0.0.1:18332", handler: h);
            T.Throws(() => { _ = client.BroadcastAsync(new byte[] { 1, 2, 3 }).Result; }, "a non-JSON response is not mistaken for success");
        });
    }
}
