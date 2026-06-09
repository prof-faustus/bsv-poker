using System.IO;
using System.Security.Cryptography;
using BsvPoker.Crypto;

namespace BsvPoker.App;

/// <summary>
/// A per-instance data profile. Each RUNNING copy of the app gets its OWN wallet + identity: on startup
/// we find the first profile directory not already locked by another instance and hold an exclusive lock
/// for our lifetime. So double-clicking a second copy is a DIFFERENT player (own wallet, own keys) — not
/// a clone — which is exactly what local 2-player testing and real multiplayer need.
/// </summary>
public sealed class Profile
{
    public string Dir { get; }
    public int Index { get; }
    public string Name => Index == 1 ? "Player 1" : $"Player {Index}";
    public byte[] IdentityPriv { get; }
    public byte[] IdentityPub { get; }

    private static FileStream? _lock; // held for process lifetime

    public Profile()
    {
        var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BsvPoker", "profiles");
        Directory.CreateDirectory(baseDir);
        for (int i = 1; i <= 64; i++)
        {
            var dir = Path.Combine(baseDir, $"p{i}");
            Directory.CreateDirectory(dir);
            try
            {
                _lock = new FileStream(Path.Combine(dir, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                Dir = dir; Index = i;
                // Use the seed the startup gate unlocked (the password-protected, funds-retaining wallet). Only
                // fall back to a per-profile seed file if no gate ran (e.g. an isolated test host).
                (IdentityPriv, IdentityPub) = App.UnlockedSeed is { } us
                    ? (us, Secp256k1.PublicKeyCompressed(us))
                    : LoadOrCreateIdentity(dir);
                return;
            }
            catch (IOException) { /* locked by another instance — try the next profile */ }
        }
        throw new InvalidOperationException("too many running instances (no free profile slot)");
    }

    private static (byte[] Priv, byte[] Pub) LoadOrCreateIdentity(string dir)
    {
        var path = Path.Combine(dir, "identity.bin");
        byte[] seed;
        if (File.Exists(path)) seed = File.ReadAllBytes(path);
        else { seed = RandomNumberGenerator.GetBytes(32); File.WriteAllBytes(path, seed); }
        return (seed, Secp256k1.PublicKeyCompressed(seed));
    }
}
