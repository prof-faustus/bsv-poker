using System.IO;
using System.Text.Json;
using BsvPoker.Core;

namespace BsvPoker.App;

/// <summary>
/// The player's card-NFT vault: the cards dealt to this player are sealed to THEIR key and stored here
/// (per-profile, persisted), so "all cards are NFTs in my wallet". Only this player can open them; an
/// opponent's cards (sealed to the opponent) cannot be read here. Persisted, so closing never loses them.
/// </summary>
public sealed class CardVault
{
    private readonly string _path;
    private readonly byte[] _priv;
    private readonly byte[] _pub;
    private List<string> _sealed = new(); // sealed card-NFT blobs owned by this player

    public CardVault(string dir, byte[] identityPriv, byte[] identityPub)
    {
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "cards.json");
        _priv = identityPriv; _pub = identityPub;
        try { if (File.Exists(_path)) _sealed = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path)) ?? new(); } catch { _sealed = new(); }
    }

    /// <summary>Store a card NFT sealed to my public key (a real on-chain Deal seals to the recipient's pubkey).</summary>
    public string AddCard(int cardIndex, byte[] blind)
    {
        var s = CardNft.SealToPub(cardIndex, blind, _pub);
        _sealed.Add(s); Save();
        return s;
    }

    public void AddSealed(string sealedHex) { _sealed.Add(sealedHex); Save(); }

    /// <summary>The cards I currently own as NFTs (decrypted with my key for display).</summary>
    public IReadOnlyList<(Card Card, string Sealed)> Owned()
    {
        var outp = new List<(Card, string)>();
        foreach (var s in _sealed)
        {
            try { var o = CardNft.Open(s, _priv); outp.Add((Card.FromIndex(o.CardIndex), s)); } catch { }
        }
        return outp;
    }

    public int Count => _sealed.Count;

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_sealed, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, true);
    }
}
