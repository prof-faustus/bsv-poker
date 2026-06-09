using System.IO;
using BsvPoker.Core;

namespace BsvPoker.App;

/// <summary>
/// The encrypted wallet file at rest — the gate that makes "open a wallet" require a password. The 32-byte
/// seed is stored ONLY as ciphertext (AES-256-GCM under a scrypt/PBKDF2 key derived from the password, via
/// <see cref="WalletExtras.EncryptSeed"/>); opening REQUIRES the correct password and a wrong password is
/// rejected (returns null) — there is no path to the keys without it. Ported from the estates/ElectrumSV
/// wallet-open model: no wallet is ever accessible without unlocking it.
/// </summary>
public static class WalletStore
{
    /// <summary>Default wallet path: %APPDATA%/BsvPoker/wallet.dat.</summary>
    public static string DefaultPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BsvPoker");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "wallet.dat");
    }

    public static bool Exists(string path) => File.Exists(path);

    /// <summary>Create the encrypted wallet file: the seed is written ONLY as password-encrypted ciphertext.
    /// REFUSES to overwrite an existing wallet unless <paramref name="overwrite"/> is set — funds protection: an
    /// existing wallet/seed is never silently clobbered.</summary>
    public static void Create(string path, byte[] seed32, string password, bool overwrite = false)
    {
        if (seed32 is null || seed32.Length != 32) throw new ArgumentException("seed must be 32 bytes");
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("a password is required");
        if (File.Exists(path) && !overwrite) throw new InvalidOperationException("a wallet already exists here — refusing to overwrite it (open or restore instead, so existing funds are never lost)");
        var enc = WalletExtras.EncryptSeed(Convert.ToHexString(seed32), password);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, enc);
        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
    }

    /// <summary>Open the wallet: returns the 32-byte seed on the RIGHT password, or null on a wrong password
    /// or a corrupt file. Never throws on a bad password — the caller re-prompts.</summary>
    public static byte[]? Open(string path, string password)
    {
        try
        {
            var hex = WalletExtras.DecryptSeed(File.ReadAllText(path), password);
            var seed = Convert.FromHexString(hex);
            return seed.Length == 32 ? seed : null;
        }
        catch { return null; }   // wrong password / tampered file
    }
}
