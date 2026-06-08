namespace BsvPoker.Net.Bsv;

/// <summary>The BSV network to operate on. One code path; only this parameter set and the peer set differ.</summary>
public enum BsvNetwork { Mainnet, Testnet, STN, Regtest }

/// <summary>
/// Real Bitcoin SV network parameters (from bitcoin-sv/src/chainparams.cpp): the P2P message-start magic,
/// default port, Base58 version bytes, and DNS seeds. These let the client be a genuine peer on the chosen
/// BSV network — the SAME logic for regtest, testnet, and mainnet.
/// </summary>
public sealed record NetworkParams(
    BsvNetwork Network,
    byte[] Magic,            // pchMessageStart, 4 bytes
    int DefaultPort,
    byte AddressVersion,     // Base58 P2PKH version
    byte ScriptVersion,      // Base58 P2SH version
    byte WifVersion,         // Base58 WIF version
    string[] DnsSeeds,
    string GenesisHashHex)   // display (big-endian) hash of the genesis block
{
    public static NetworkParams For(BsvNetwork n) => n switch
    {
        // Reliable operator nodes first (they answer P2P on 8333 and don't greylist normal clients — proven), then
        // the public DNS seeds. gorillapool/taal/bitails run well-maintained mainnet nodes.
        BsvNetwork.Mainnet => new(n, new byte[] { 0xe3, 0xe1, 0xf3, 0xe8 }, 8333, 0x00, 0x05, 0x80,
            new[] { "electrumx.gorillapool.io", "sv.satoshi.io", "sv2.satoshi.io", "esv.bitails.io", "bsv.aftrek.org",
                    "seed.bitcoinsv.io", "seed.satoshisvision.network", "seed.bitcoinseed.directory" },
            "000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f"),
        BsvNetwork.Testnet => new(n, new byte[] { 0xf4, 0xe5, 0xf3, 0xf4 }, 18333, 0x6f, 0xc4, 0xef,
            new[] { "testnet-seed.bitcoinsv.io", "testnet-seed.bitcoincloud.net" },
            "000000000933ea01ad0ee984209779baaec3ced90fa3f408719526f8d77f4943"),
        BsvNetwork.STN => new(n, new byte[] { 0xfb, 0xce, 0xc4, 0xf9 }, 9333, 0x6f, 0xc4, 0xef,
            new[] { "stn-seed.bitcoinsv.io" },
            "000000000933ea01ad0ee984209779baaec3ced90fa3f408719526f8d77f4943"),
        BsvNetwork.Regtest => new(n, new byte[] { 0xda, 0xb5, 0xbf, 0xfa }, 18444, 0x6f, 0xc4, 0xef,
            Array.Empty<string>(),
            "0f9188f13cb7b2c71f2a335e3a4fc328bf5beb436012afca590b1a11466e2206"),
        _ => throw new ArgumentOutOfRangeException(nameof(n)),
    };
}
