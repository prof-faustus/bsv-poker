using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BsvPoker.Core;
using BsvPoker.Crypto;

namespace BsvPoker.App;

public partial class App : Application
{
    /// <summary>The decrypted 32-byte seed for THIS session, set by the startup gate; Profile/wallet read it.</summary>
    internal static byte[]? UnlockedSeed;

    protected override void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnMainWindowClose; // close the window => the whole app exits
        base.OnStartup(e);

        // HEADLESS SELF-TEST: poker.exe --selftest runs the REAL wallet open/close lifecycle 100x with NO
        // window — writes the result to %TEMP%/poker_selftest.txt and exits. Verifies the exe without a screen.
        if (e.Args.Any(a => a == "--selftest")) { RunSelfTest(); Shutdown(); return; }

        // STARTUP GATE: the wallet is NEVER accessible without a password. Existing keys/funds are RETAINED —
        // we open an existing encrypted wallet, or migrate a legacy unencrypted seed into an encrypted one;
        // we never discard, overwrite, or regenerate existing keys.
        byte[]? seed = Gate();
        if (seed is null) { Shutdown(); return; }   // cancelled / failed unlock => open nothing
        UnlockedSeed = seed;
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        Environment.Exit(e.ApplicationExitCode); // guaranteed termination: no lingering threads/orphans
    }

    private static SolidColorBrush Bc(string h) => new((Color)ColorConverter.ConvertFromString(h));

    /// <summary>Require unlocking before the app opens. Retains existing keys/funds.</summary>
    private static byte[]? Gate()
    {
        string path = WalletStore.DefaultPath();
        // 1) an encrypted wallet already exists → REQUIRE its password (this is the funded wallet)
        if (WalletStore.Exists(path))
        {
            for (int tries = 0; tries < 6; tries++)
            {
                var pw = new WalletWizard.PromptPw();
                if (pw.ShowDialog() != true) return null;
                var s = WalletStore.Open(path, pw.Value);
                if (s != null) return s;
                MessageBox.Show("Wrong password. Try again.", "Unlock wallet");
            }
            return null;
        }
        // 2) a legacy UNENCRYPTED seed from a previous version exists → migrate it (RETAIN keys/funds): set a
        //    password now so the existing seed becomes encrypted, password-protected, and registered.
        var legacy = FindLegacySeed();
        if (legacy != null)
        {
            var m = new MigrateWindow(legacy);
            return m.ShowDialog() == true ? m.Seed : null;
        }
        // 3) brand-new user → full create/restore wizard
        var wiz = new WalletWizard();
        return wiz.ShowDialog() == true ? wiz.Seed : null;
    }

    /// <summary>Find an existing unencrypted seed written by a previous version (profiles/p*/identity.bin).</summary>
    private static byte[]? FindLegacySeed()
    {
        try
        {
            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BsvPoker", "profiles");
            if (!Directory.Exists(baseDir)) return null;
            foreach (var dir in Directory.GetDirectories(baseDir).OrderBy(d => d))
            {
                var f = Path.Combine(dir, "identity.bin");
                if (File.Exists(f)) { var b = File.ReadAllBytes(f); if (b.Length == 32) return b; }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Run the real wallet open/close lifecycle 100x, headless: create + open with the RIGHT password,
    /// reject the WRONG password, confirm the seed round-trips, then sign + verify a real spend — 100 different
    /// keys/passwords. Result → %TEMP%/poker_selftest.txt. Touches only a temp dir; never the user's wallet.</summary>
    private static void RunSelfTest()
    {
        string outp = Path.Combine(Path.GetTempPath(), "poker_selftest.txt");
        string tmp = Path.Combine(Path.GetTempPath(), "poker_st");
        Directory.CreateDirectory(tmp);
        int ok = 0, fail = 0; string firstErr = "";
        for (int i = 1; i <= 100; i++)
        {
            string wpath = Path.Combine(tmp, "w" + i + ".dat");
            try
            {
                byte[] seed = RandomNumberGenerator.GetBytes(32);
                string pw = "pw" + i + "Aa!";
                if (File.Exists(wpath)) File.Delete(wpath);

                // (1) create + open/close round-trip; the WRONG password must be rejected
                WalletStore.Create(wpath, seed, pw);
                var s2 = WalletStore.Open(wpath, pw);
                if (s2 is null || !s2.AsSpan().SequenceEqual(seed)) throw new Exception("open round-trip mismatch");
                var bad = WalletStore.Open(wpath, pw + "x");
                if (bad is not null && bad.AsSpan().SequenceEqual(seed)) throw new Exception("WRONG PASSWORD ACCEPTED");
                var s3 = WalletStore.Open(wpath, pw);   // open / close / open / close
                if (s3 is null || !s3.AsSpan().SequenceEqual(seed)) throw new Exception("second open mismatch");

                // (2) identity: a derived attestation sub-key signs the profile; the Base ID never signs
                byte[] attPriv = Type42.UniqueKey(seed, "bsvpoker/identity/attestation");
                byte[] attPub = Secp256k1.PublicKeyCompressed(attPriv);
                byte[] profile = System.Text.Encoding.UTF8.GetBytes("{\"pseudonym\":\"p" + i + "\"}");
                byte[] sig = Secp256k1.Sign(attPriv, profile);
                if (!Secp256k1.Verify(attPub, profile, sig)) throw new Exception("identity signature verify failed");

                // (3) a REAL P2PKH spend, signed and verified against the FORKID sighash
                var k = WalletKeys.Account(seed, 0, 0);
                var tx = new Chain.Tx(2, new() { new(new string('b', 64), 0, Array.Empty<byte>(), 0xffffffff) },
                                          new() { new(50_000, Chain.P2pkhLockForPub(k.Pub)) }, 0);
                var signed = Chain.SignP2pkhInput(tx, 0, k.Priv, k.Pub, 100_000);
                if (!Chain.VerifyP2pkhInput(signed, 0, k.Pub, 100_000)) throw new Exception("spend signature verify failed");

                File.Delete(wpath);
                ok++;
            }
            catch (Exception ex) { fail++; if (firstErr.Length == 0) firstErr = "iter " + i + ": " + ex.Message; }
            finally { try { if (File.Exists(wpath)) File.Delete(wpath); } catch { } }
        }
        try { File.WriteAllText(outp, $"SELFTEST {ok}/100 fail={fail}" + (firstErr.Length > 0 ? " firstErr=" + firstErr : "")); } catch { }
    }

    /// <summary>Migrate a legacy unencrypted seed into an encrypted, password-protected, registered wallet —
    /// the existing keys/funds are retained, just secured. Password + pseudonym + email required.</summary>
    private sealed class MigrateWindow : Window
    {
        public byte[]? Seed;
        private readonly byte[] _legacy;
        public MigrateWindow(byte[] legacySeed)
        {
            _legacy = legacySeed;
            Title = "Secure your existing wallet"; Width = 520; Height = 360; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen; Background = Bc("#1b1d1e");
            var pw = new PasswordBox(); var pw2 = new PasswordBox(); var ps = new TextBox(); var em = new TextBox();
            foreach (var f in new Control[] { pw, pw2, ps, em }) { f.Background = Bc("#171819"); f.Foreground = Bc("#e6e6e6"); f.BorderThickness = new Thickness(0); f.Padding = new Thickness(8); f.Margin = new Thickness(0, 4, 0, 8); }
            var msg = new TextBlock { Foreground = Bc("#f7a8c4"), TextWrapping = TextWrapping.Wrap };
            TextBlock Lab(string t) => new() { Text = t, Foreground = Bc("#9aa0a6"), FontSize = 12 };
            var sp = new StackPanel { Margin = new Thickness(20) };
            sp.Children.Add(new TextBlock { Text = "Your existing wallet was found", Foreground = Bc("#e6e6e6"), FontSize = 18, FontWeight = FontWeights.Bold });
            sp.Children.Add(new TextBlock { Text = "Set a password to encrypt your EXISTING keys and funds (nothing is lost), and register your identity.", Foreground = Bc("#9aa0a6"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) });
            sp.Children.Add(Lab("password")); sp.Children.Add(pw);
            sp.Children.Add(Lab("confirm password")); sp.Children.Add(pw2);
            sp.Children.Add(Lab("pseudonym / handle (required)")); sp.Children.Add(ps);
            sp.Children.Add(Lab("email (checked)")); sp.Children.Add(em);
            var ok = new Button { Content = "Secure + register", Width = 170, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Background = Bc("#2d2f34"), Foreground = Bc("#e6e6e6") };
            sp.Children.Add(ok); sp.Children.Add(msg);
            Content = new ScrollViewer { Content = sp };
            ok.Click += (_, _) =>
            {
                if (pw.Password.Length < 1 || pw.Password != pw2.Password) { msg.Text = "passwords must match and be non-empty"; return; }
                if (ps.Text.Trim().Length == 0) { msg.Text = "a pseudonym is required"; return; }
                try { WalletWizard.RegisterCore(WalletStore.DefaultPath(), _legacy, pw.Password, ps.Text.Trim(), em.Text.Trim(), ""); Seed = _legacy; DialogResult = true; Close(); }
                catch (Exception ex) { msg.Text = ex.Message; }
            };
        }
    }
}
