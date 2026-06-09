using System.Diagnostics; using BsvPoker.Net.Bsv; using BsvPoker.Core; using BsvPoker.Crypto;
var sw = Stopwatch.StartNew();
string addr = "1BH5Uf3tbfNSBjCmVJYWB5nTCRXVVBQXuR";
var h160 = Base58.CheckDecode(addr)[1..];
var script = Chain.P2pkhLock(h160);
var sh = ElectrumSvpClient.ScriptHashOf(script);
using var cli = new ElectrumSvpClient();
bool ok = await cli.ConnectAnyAsync(ElectrumSvpClient.ServersFor(BsvNetwork.Mainnet), 7000, m => Console.WriteLine("  "+m));
Console.WriteLine($"connected={ok} host={cli.Host} ({sw.Elapsed.TotalSeconds:F1}s)");
if (ok) {
  var us = await cli.ListUnspentAsync(sh);
  long total = us.Sum(u => u.Value);
  Console.WriteLine($"UTXOs for {addr}: {us.Count}, total {total} sat  ({sw.Elapsed.TotalSeconds:F1}s)");
  foreach (var u in us) Console.WriteLine($"  {u.Value} sat  {u.TxHashDisplay}:{u.Vout} (height {u.Height})");
}
