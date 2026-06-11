using BsvPoker.Core;
using BsvPoker.Crypto;
using BsvPoker.Net;
using BsvPoker.Net.Bsv;

// Verify the wallet's coin-recovery path end-to-end: connect to the SAME ElectrumSVP mainnet servers the wallet
// uses and ask for an address's UTXOs and history. If this returns the coins, the wallet can recover them; if it
// returns nothing or cannot connect, THAT is why the balance shows 0.
var addr = args.Length > 0 ? args[0] : "17PdCPoNC8BG5AYQYikHrJ2QnBj9CfzK9y";
var h160 = Base58.CheckDecode(addr)[1..];
var script = Chain.P2pkhLock(h160);
var sh = ElectrumSvpClient.ScriptHashOf(script);
Console.WriteLine($"address={addr}");
Console.WriteLine($"scripthash={sh}");

var servers = ElectrumSvpClient.ServersFor(BsvNetwork.Mainnet).ToList();
Console.WriteLine($"servers configured: {servers.Count}");
using var cli = new ElectrumSvpClient();
bool conn = await cli.ConnectAnyAsync(servers, 12000, s => Console.WriteLine("  [conn] " + s));
Console.WriteLine($"CONNECTED={conn}");
if (!conn) { Console.WriteLine("=> RECOVERY CANNOT WORK: no SPV server reachable on this network/machine."); return; }

try
{
    var us = await cli.ListUnspentAsync(sh);
    Console.WriteLine($"LISTUNSPENT count={us.Count}");
    foreach (var u in us) Console.WriteLine($"  {u.TxHashDisplay}:{u.Vout} = {u.Value} sat  height={u.Height}");
    long total = us.Sum(u => u.Value);
    Console.WriteLine($"=> server reports {total} sat spendable for this address ({us.Count(u => u.Height > 0)} confirmed, {us.Count(u => u.Height <= 0)} mempool)");
}
catch (Exception ex) { Console.WriteLine("listunspent ERROR: " + ex.Message); }

try
{
    var hist = await cli.GetHistoryAsync(sh);
    Console.WriteLine($"GET_HISTORY count={hist.Count}");
    foreach (var h in hist) Console.WriteLine($"  {h.Txid} height={h.Height}");
}
catch (Exception ex) { Console.WriteLine("get_history ERROR: " + ex.Message); }

// CRUCIAL: my recovery fetches the raw tx for a MEMPOOL coin via transaction.get. If that throws for an
// unconfirmed tx, the coin is skipped and the balance stays 0 — exactly the symptom.
try
{
    var us2 = await cli.ListUnspentAsync(sh);
    foreach (var u in us2)
    {
        try { var raw = await cli.GetTransactionAsync(u.TxHashDisplay); Console.WriteLine($"GET_TX {u.TxHashDisplay} (height {u.Height}): OK {raw.Length} bytes"); }
        catch (Exception ex) { Console.WriteLine($"GET_TX {u.TxHashDisplay} (height {u.Height}): FAILED — {ex.Message}"); }
    }
}
catch (Exception ex) { Console.WriteLine("recheck ERROR: " + ex.Message); }
