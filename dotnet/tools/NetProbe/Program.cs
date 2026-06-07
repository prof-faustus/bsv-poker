using BsvPoker.Net.Bsv;
// HONEST live-network probe: connect + multi-batch header sync from genesis, validated.
foreach (var (which, batches) in new[] { (BsvNetwork.Testnet, 12), (BsvNetwork.Mainnet, 8) })
{
    var node = new BsvNode(NetworkParams.For(which));
    var seeds = await node.ResolveSeedsAsync();
    foreach (var ep in seeds.Take(12)) if (await node.ConnectAsync(ep.Address.ToString(), ep.Port)) break;
    Console.WriteLine($"[{which}] peers={node.PeerCount}, advertised tip={node.BestHeight}");
    if (node.PeerCount > 0)
    {
        var (total, height) = await node.SyncHeadersAsync(maxBatches: batches, waitMs: 9000);
        Console.WriteLine($"[{which}] multi-batch sync: {total} headers downloaded & VALIDATED (PoW+linkage) from genesis");
    }
    node.Dispose();
}
