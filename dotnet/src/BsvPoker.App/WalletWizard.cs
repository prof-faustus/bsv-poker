using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BsvPoker.Core;
using BsvPoker.Crypto;

namespace BsvPoker.App;

/// <summary>
/// The wallet startup WIZARD — ported from estates' port of ElectrumSV's wallet/account wizard: a multi-page
/// Back / Next / Cancel window with the real creation path — Splash → Choose (Create | Restore | Open) → set
/// Password → DISPLAY seed (backup) → CONFIRM seed → Register identity (pseudonym required + email checked) →
/// encrypted wallet created. Opening an existing wallet REQUIRES the password (wrong password rejected). On
/// success it exposes the 32-byte seed; the app does not start until this completes — no wallet is ever
/// accessible without unlocking it. Dark themed to match the wallet.
/// </summary>
public sealed class WalletWizard : Window
{
    public byte[]? Seed { get; private set; }
    public string Password { get; private set; } = "";
    public string Pseudonym { get; private set; } = "";

    private enum Step { Splash, Choose, NewPassword, SeedShow, SeedConfirm, Register, Restore }
    private Step _step = Step.Splash;
    private Step _registerFrom = Step.SeedConfirm;
    private byte[] _pending = Array.Empty<byte>();

    private static SolidColorBrush B(string h) => new((Color)ColorConverter.ConvertFromString(h));
    private readonly TextBlock _title = new() { Foreground = B("#e6e6e6"), FontSize = 20, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) };
    private readonly TextBlock _subtitle = new() { Foreground = B("#9aa0a6"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
    private readonly StackPanel _body = new();
    private readonly TextBlock _msg = new() { Foreground = B("#f7a8c4"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _back = new() { Content = "‹ Back", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _next = new() { Content = "Next ›", Width = 150 };
    private readonly Button _cancel = new() { Content = "Cancel", Width = 90, Margin = new Thickness(0, 0, 8, 0) };

    private readonly PasswordBox _pw = new();
    private readonly PasswordBox _pw2 = new();
    private readonly TextBox _seedShow = new() { IsReadOnly = true };
    private readonly TextBox _seedConfirm = new();
    private readonly TextBox _restoreSeed = new();
    private readonly PasswordBox _restorePw = new();
    private readonly TextBox _pseudonym = new();
    private readonly TextBox _email = new();
    private readonly TextBox _realname = new();

    public WalletWizard()
    {
        Title = "BSV Poker — wallet setup"; Width = 560; Height = 470;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Background = B("#1b1d1e"); ResizeMode = ResizeMode.NoResize;
        foreach (var f in new Control[] { _pw, _pw2, _seedShow, _seedConfirm, _restoreSeed, _restorePw, _pseudonym, _email, _realname })
        { f.Background = B("#171819"); f.Foreground = B("#e6e6e6"); f.BorderThickness = new Thickness(0); f.Padding = new Thickness(8); f.Margin = new Thickness(0, 4, 0, 8); f.FontSize = 13; }
        _seedShow.FontFamily = _seedConfirm.FontFamily = _restoreSeed.FontFamily = new FontFamily("Consolas");
        _seedShow.TextWrapping = _seedConfirm.TextWrapping = _restoreSeed.TextWrapping = TextWrapping.Wrap;
        foreach (var btn in new[] { _back, _next, _cancel }) { btn.Background = B("#2d2f34"); btn.Foreground = B("#e6e6e6"); btn.BorderBrush = B("#3a3d42"); btn.Padding = new Thickness(8, 6, 8, 6); }

        var nav = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(_cancel); right.Children.Add(_back); right.Children.Add(_next);
        DockPanel.SetDock(right, Dock.Right); nav.Children.Add(right);

        var root = new DockPanel { Margin = new Thickness(22) };
        DockPanel.SetDock(_title, Dock.Top); DockPanel.SetDock(_subtitle, Dock.Top); DockPanel.SetDock(nav, Dock.Bottom); DockPanel.SetDock(_msg, Dock.Bottom);
        root.Children.Add(_title); root.Children.Add(_subtitle); root.Children.Add(nav); root.Children.Add(_msg);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _body });
        Content = root;

        _cancel.Click += (_, _) => { DialogResult = false; Close(); };
        _back.Click += (_, _) => Back();
        _next.Click += (_, _) => Next();
        Render();
    }

    private TextBlock Lab(string t) => new() { Text = t, Foreground = B("#9aa0a6"), FontSize = 12 };
    private Button Big(string t) => new() { Content = t, Margin = new Thickness(0, 6, 0, 6), Padding = new Thickness(12, 10, 12, 10), Background = B("#2d2f34"), Foreground = B("#e6e6e6"), BorderBrush = B("#3a3d42"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };

    private void Render()
    {
        _body.Children.Clear(); _msg.Text = ""; _back.IsEnabled = _step != Step.Splash;
        _next.Visibility = Visibility.Visible; _next.Content = "Next ›";
        switch (_step)
        {
            case Step.Splash:
                _title.Text = "Welcome to BSV Poker"; _subtitle.Text = "A standalone SPV BSV wallet + dealerless on-chain poker. Let's set up or open your wallet.";
                _body.Children.Add(Lab("Click Next to create or open a wallet. Your keys are encrypted with a password and stored on disk, so closing keeps your money. You cannot enter without unlocking the wallet."));
                break;
            case Step.Choose:
            {
                _title.Text = "Choose your wallet"; _subtitle.Text = "Create a new wallet, restore from your seed, or open your existing wallet.";
                _next.Visibility = Visibility.Collapsed;
                bool exists = WalletStore.Exists(WalletStore.DefaultPath());
                var create = Big("➕  Create a new wallet"); create.Click += (_, _) => { _step = Step.NewPassword; Render(); };
                var restore = Big("↩  Restore from seed"); restore.Click += (_, _) => { _step = Step.Restore; Render(); };
                _body.Children.Add(create); _body.Children.Add(restore);
                if (exists) { var open = Big("🔓  Open my existing wallet"); open.Click += (_, _) => OpenExisting(); _body.Children.Add(open); }
                break;
            }
            case Step.NewPassword:
                _title.Text = "Set a password"; _subtitle.Text = "Your password encrypts your private keys. You'll need it every time you open the wallet.";
                _body.Children.Add(Lab("password")); _body.Children.Add(_pw);
                _body.Children.Add(Lab("confirm password")); _body.Children.Add(_pw2);
                break;
            case Step.SeedShow:
                _title.Text = "Back up your seed"; _subtitle.Text = "Write this seed down. It is the ONLY way to recover your wallet and funds. Anyone with it controls your money.";
                _pending = RandomNumberGenerator.GetBytes(32); _seedShow.Text = Convert.ToHexString(_pending).ToLowerInvariant();
                _body.Children.Add(Lab("your recovery seed (64 hex)")); _body.Children.Add(_seedShow);
                break;
            case Step.SeedConfirm:
                _title.Text = "Confirm your seed"; _subtitle.Text = "Re-enter the seed you just wrote down, to prove you have a backup.";
                _seedConfirm.Text = ""; _body.Children.Add(Lab("re-enter your seed")); _body.Children.Add(_seedConfirm);
                break;
            case Step.Register:
                _title.Text = "Register your identity";
                _subtitle.Text = "Your IDENTITY is the key everything links to — payments, chat, NFTs, the game. Choose a pseudonym (required) and an email (format-checked). It is signed by a sub-key of your Base ID.";
                _body.Children.Add(Lab("pseudonym / handle (required)")); _body.Children.Add(_pseudonym);
                _body.Children.Add(Lab("email (checked)")); _body.Children.Add(_email);
                _body.Children.Add(Lab("real name (optional)")); _body.Children.Add(_realname);
                _next.Content = "Register + create wallet";
                break;
            case Step.Restore:
                _title.Text = "Restore from seed"; _subtitle.Text = "Enter your 64-hex recovery seed and set a password for this device.";
                _body.Children.Add(Lab("recovery seed (64 hex)")); _body.Children.Add(_restoreSeed);
                _body.Children.Add(Lab("password")); _body.Children.Add(_restorePw);
                _next.Content = "Restore wallet";
                break;
        }
    }

    private void Back()
    {
        _step = _step switch
        {
            Step.Choose => Step.Splash,
            Step.NewPassword => Step.Choose,
            Step.SeedShow => Step.NewPassword,
            Step.SeedConfirm => Step.SeedShow,
            Step.Register => _registerFrom,
            Step.Restore => Step.Choose,
            _ => Step.Splash,
        };
        Render();
    }

    private void Next()
    {
        switch (_step)
        {
            case Step.Splash: _step = Step.Choose; Render(); break;
            case Step.NewPassword:
                if (_pw.Password.Length < 1) { _msg.Text = "enter a password"; return; }
                if (_pw.Password != _pw2.Password) { _msg.Text = "passwords do not match"; return; }
                Password = _pw.Password; _step = Step.SeedShow; Render(); break;
            case Step.SeedShow: _step = Step.SeedConfirm; Render(); break;
            case Step.SeedConfirm:
                if (_seedConfirm.Text.Trim().ToLowerInvariant() != Convert.ToHexString(_pending).ToLowerInvariant()) { _msg.Text = "that does not match the seed — check your backup"; return; }
                _registerFrom = Step.SeedConfirm; _step = Step.Register; Render(); break;
            case Step.Register:
            {
                string ps = _pseudonym.Text.Trim();
                if (ps.Length == 0) { _msg.Text = "a PSEUDONYM is required — this is your identity"; return; }
                if (!EmailOk(_email.Text.Trim())) { _msg.Text = "enter a valid email — name@domain.tld"; return; }
                try { RegisterCore(WalletStore.DefaultPath(), _pending, Password, ps, _email.Text.Trim(), _realname.Text.Trim()); }
                catch (Exception ex) { _msg.Text = ex.Message; return; }
                Seed = _pending; Pseudonym = ps; DialogResult = true; Close(); break;
            }
            case Step.Restore:
            {
                string h = _restoreSeed.Text.Trim();
                if (h.Length != 64) { _msg.Text = "seed must be 64 hex characters"; return; }
                byte[] s; try { s = Convert.FromHexString(h); } catch { _msg.Text = "seed is not valid hex"; return; }
                if (_restorePw.Password.Length < 1) { _msg.Text = "set a password"; return; }
                _pending = s; Password = _restorePw.Password; _registerFrom = Step.Restore; _step = Step.Register; Render(); break;
            }
        }
    }

    private void OpenExisting()
    {
        var pwWin = new PromptPw { Owner = this };
        if (pwWin.ShowDialog() != true) return;
        var s = WalletStore.Open(WalletStore.DefaultPath(), pwWin.Value);
        if (s is null) { _msg.Text = "wrong password, or not a wallet file"; return; }
        Seed = s; Password = pwWin.Value; DialogResult = true; Close();
    }

    private static bool EmailOk(string e)
    {
        e = (e ?? "").Trim();
        if (e.Length < 6 || e.Length > 254) return false;
        if (!System.Text.RegularExpressions.Regex.IsMatch(e, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) return false;
        var parts = e.Split('@');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length < 3) return false;
        if (parts[1].StartsWith('.') || parts[1].EndsWith('.') || parts[1].Contains("..")) return false;
        return true;
    }

    /// <summary>The REAL registration: create the encrypted wallet and write the signed identity. The Base ID
    /// (Type-42 identity key) NEVER signs — a derived attestation sub-key signs the profile that binds
    /// pseudonym ↔ email ↔ identity. Shared by the live wizard AND the headless self-test, so the test
    /// exercises the exact production path. Static + GUI-free.</summary>
    public static void RegisterCore(string walletPath, byte[] seed, string password, string pseudonym, string email, string realname)
    {
        WalletStore.Create(walletPath, seed, password);
        var ring = new KeyRing(seed);
        byte[] identityPub = ring.IdentityPub();                 // Base ID (derives only, never signs)
        byte[] attPriv = Type42.UniqueKey(seed, "bsvpoker/identity/attestation");
        byte[] attPub = Secp256k1.PublicKeyCompressed(attPriv);
        string profile = "{" +
            $"\"pseudonym\":{J(pseudonym)},\"email\":{J(email)},\"realname\":{J(realname)}," +
            $"\"identity\":\"{Convert.ToHexString(identityPub).ToLowerInvariant()}\"," +
            $"\"attestation_pub\":\"{Convert.ToHexString(attPub).ToLowerInvariant()}\",\"created\":\"{DateTime.UtcNow:o}\"" +
            "}";
        byte[] sig = Secp256k1.Sign(attPriv, System.Text.Encoding.UTF8.GetBytes(profile));
        if (!Secp256k1.Verify(attPub, System.Text.Encoding.UTF8.GetBytes(profile), sig))
            throw new Exception("identity signature failed to verify");
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BsvPoker");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "identity.json"), profile + "\n" + Convert.ToHexString(sig).ToLowerInvariant());
        File.WriteAllText(Path.Combine(dir, "identity.txt"), pseudonym);
    }

    private static string J(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Password prompt for opening an existing wallet (required — there is no bypass).</summary>
    public sealed class PromptPw : Window
    {
        public string Value = "";
        public PromptPw()
        {
            Title = "Unlock wallet"; Width = 360; Height = 160; WindowStartupLocation = WindowStartupLocation.CenterScreen; Background = B("#1b1d1e"); ResizeMode = ResizeMode.NoResize;
            var sp = new StackPanel { Margin = new Thickness(16) };
            sp.Children.Add(new TextBlock { Text = "Enter your wallet password", Foreground = B("#e6e6e6"), Margin = new Thickness(0, 0, 0, 8) });
            var pw = new PasswordBox { Background = B("#171819"), Foreground = B("#e6e6e6"), BorderThickness = new Thickness(0), Padding = new Thickness(8) };
            var ok = new Button { Content = "Unlock", Width = 90, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, Background = B("#2d2f34"), Foreground = B("#e6e6e6") };
            ok.Click += (_, _) => { Value = pw.Password; DialogResult = true; Close(); };
            pw.KeyDown += (_, e) => { if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Return) { Value = pw.Password; DialogResult = true; Close(); } };
            sp.Children.Add(pw); sp.Children.Add(ok); Content = sp; Loaded += (_, _) => pw.Focus();
        }
    }
}
