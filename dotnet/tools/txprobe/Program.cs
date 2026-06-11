using System.Diagnostics;
using BsvPoker.Core;
using BsvPoker.Net;
using BsvPoker.Net.Bsv;

// Round-trip a Bitcoin tx through TxLink over loopback — the EXACT transport a chat message uses to reach a bot
// in the same/another process. Confirms delivery happens and how fast (must be ~instant on the same machine).
var net = NetworkParams.For(BsvNetwork.Mainnet);

using var receiver = new TxLink(net, 0);
var tcs = new TaskCompletionSource<Chain.Tx>();
receiver.OnTransaction += tx => tcs.TrySetResult(tx);
receiver.Start();
Console.WriteLine($"receiver listening on 127.0.0.1:{receiver.Port}");

// a minimal, well-formed tx (1-sat P2PKH output) — like a chat/bet message
var tx = new Chain.Tx(2,
    new() { new(new string('0', 64), 0, Array.Empty<byte>(), 0xffffffff) },
    new() { new(1, Chain.P2pkhLock(new byte[20])) }, 0);
var raw = Chain.Serialize(tx);

var sw = Stopwatch.StartNew();
bool sent = await TxLink.SendTxAsync(net, "127.0.0.1", receiver.Port, raw);
bool delivered = await Task.WhenAny(tcs.Task, Task.Delay(3000)) == tcs.Task;
sw.Stop();

Console.WriteLine($"sent={sent}  delivered={delivered}  latency={sw.ElapsedMilliseconds}ms");
if (delivered) Console.WriteLine($"received txid={Chain.Txid(tcs.Task.Result)} — IP-to-IP transport OK");
else Console.WriteLine("NOT DELIVERED — the IP-to-IP transport is the chat bug");
