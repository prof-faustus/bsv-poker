using BsvPoker.Net.Bsv;
// HONEST live-network probe: try to actually reach the real BSV network.
foreach (var which in new[] { BsvNetwork.Testnet, BsvNetwork.Mainnet })
{
    var node = new BsvNode(NetworkParams.For(which));
    var seeds = await node.ResolveSeedsAsync();
    Console.WriteLine($"[{which}] DNS seeds resolved -> {seeds.Count} endpoints");
    int tried = 0;
    foreach (var ep in seeds.Take(10))
    {
        tried++;
        if (await node.ConnectAsync(ep.Address.ToString(), ep.Port)) break;
    }
    Console.WriteLine($"[{which}] tried {tried}, connected peers={node.PeerCount}, best height={node.BestHeight}");
    node.Dispose();
}
