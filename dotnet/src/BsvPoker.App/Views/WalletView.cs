using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BsvPoker.App.Controls;
using BsvPoker.Core;
using BsvPoker.Crypto;

namespace BsvPoker.App.Views;

/// <summary>
/// Wallet tab — a BSV-native deterministic wallet: one 32-byte master SEED backs up
/// everything; spending keys are derived directly from the seed (see <see cref="WalletKeys"/>). A single
/// SEED backup string (Base58Check) is the whole backup. RECEIVE addresses; SEND payments; a transaction
/// HISTORY; BACKUP (reveal seed) and RESTORE (re-derive from a seed); optional password-at-rest. Persisted
/// atomically to the per-instance profile dir, so closing never loses funds. Same model across all networks.
/// </summary>
public sealed class WalletView : UserControl
{
    private sealed class Tx { public string Time { get; set; } = ""; public string Type { get; set; } = ""; public long Amount { get; set; } public long Balance { get; set; } public string Memo { get; set; } = ""; }
    private sealed class File_ { public string Seed { get; set; } = ""; public int RecvIndex { get; set; } public long Balance { get; set; } public List<Tx> History { get; set; } = new(); }

    private readonly string _path;
    private File_ _w = new();
    private byte[] _seed = Array.Empty<byte>();   // the 32-byte master seed held in memory
    private bool _locked;                          // true when the wallet is encrypted and not yet unlocked this session

    private readonly TextBlock _bal = new() { Foreground = Brushes.White, FontSize = 28, FontWeight = FontWeights.Bold };
    private readonly TextBlock _recv = new() { Foreground = Brushes.LightGreen, FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap };
    private readonly ListView _history = new() { Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x0F, 0x0F)), Foreground = Brushes.White, BorderThickness = new Thickness(0), Height = 220 };
    private readonly TextBox _amount = new() { Width = 110, Text = "100" };
    private readonly TextBox _dest = new() { Width = 320 };
    private readonly TextBlock _status = new() { Foreground = Brushes.Gray, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly CardVault _vault;
    private readonly WrapPanel _cards = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _cardsLabel = new() { Foreground = Brushes.Gray, Margin = new Thickness(0, 14, 0, 2), Text = "My cards (NFTs)" };

    public WalletView(string dataDir, CardVault vault)
    {
        _vault = vault;
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "wallet.json");
        Load();

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = "Wallet (HD, recovery-phrase backed)", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
        root.Children.Add(new TextBlock { Text = "Balance (play chips)", Foreground = Brushes.Gray, Margin = new Thickness(0, 10, 0, 0) });
        root.Children.Add(_bal);

        root.Children.Add(_cardsLabel);
        root.Children.Add(_cards);

        root.Children.Add(new TextBlock { Text = "Receive address", Foreground = Brushes.Gray, Margin = new Thickness(0, 10, 0, 2) });
        root.Children.Add(_recv);
        var newAddr = Btn("New address"); newAddr.Click += (_, _) => { if (Guard()) { _w.RecvIndex++; Save(); Render(); } };
        var deposit = Btn("Deposit (play)"); deposit.Click += (_, _) => { if (long.TryParse(_amount.Text, out var a) && a > 0) Commit("deposit", a, "deposit"); };
        root.Children.Add(new WrapPanel { Children = { newAddr, deposit } });

        var send = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        send.Children.Add(new TextBlock { Text = "Send  amount ", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        send.Children.Add(_amount);
        send.Children.Add(new TextBlock { Text = "  to ", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        send.Children.Add(_dest);
        var sendBtn = Btn("Send"); sendBtn.Click += (_, _) => DoSend();
        send.Children.Add(sendBtn);
        root.Children.Add(send);

        root.Children.Add(new TextBlock { Text = "History", Foreground = Brushes.Gray, Margin = new Thickness(0, 14, 0, 4) });
        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = "Time", Width = 170, DisplayMemberBinding = new System.Windows.Data.Binding("Time") });
        gv.Columns.Add(new GridViewColumn { Header = "Type", Width = 80, DisplayMemberBinding = new System.Windows.Data.Binding("Type") });
        gv.Columns.Add(new GridViewColumn { Header = "Amount", Width = 90, DisplayMemberBinding = new System.Windows.Data.Binding("Amount") });
        gv.Columns.Add(new GridViewColumn { Header = "Balance", Width = 90, DisplayMemberBinding = new System.Windows.Data.Binding("Balance") });
        gv.Columns.Add(new GridViewColumn { Header = "Memo", Width = 280, DisplayMemberBinding = new System.Windows.Data.Binding("Memo") });
        _history.View = gv;
        root.Children.Add(_history);

        var backup = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var showPhrase = Btn("Back up — show wallet seed");
        showPhrase.Click += (_, _) => { if (Guard()) MessageBox.Show(WalletKeys.SeedToBackup(_seed), "Write this seed down — it recovers your whole wallet", MessageBoxButton.OK, MessageBoxImage.Warning); };
        var restore = Btn("Restore from seed…");
        restore.Click += (_, _) => Restore();
        var pwBtn = Btn("Set / change password…");
        pwBtn.Click += (_, _) => SetPassword();
        var unlockBtn = Btn("Unlock…");
        unlockBtn.Click += (_, _) => { Unlock(); Render(); };
        backup.Children.Add(showPhrase); backup.Children.Add(restore); backup.Children.Add(pwBtn); backup.Children.Add(unlockBtn);
        root.Children.Add(backup);

        var adv = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var wif = Btn("Export key (WIF)");
        wif.Click += (_, _) => { if (Guard()) MessageBox.Show(WalletExtras.ToWif(WalletKeys.Account(_seed, 0, 0).Priv), "WIF private key for receive key #0 (keep secret!)"); };
        var sign = Btn("Sign message…");
        sign.Click += (_, _) => { if (Guard()) SignMessageDialog(); };
        var verify = Btn("Verify message…");
        verify.Click += (_, _) => VerifyMessageDialog();
        adv.Children.Add(wif); adv.Children.Add(sign); adv.Children.Add(verify);
        root.Children.Add(adv);
        root.Children.Add(_status);

        Content = new ScrollViewer { Content = root };
        Render();
    }

    private void SignMessageDialog()
    {
        var msg = new TextBox { Width = 460, AcceptsReturn = true, Height = 60, TextWrapping = TextWrapping.Wrap };
        var go = new Button { Content = "Sign", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        var win = new Window { Title = "Sign a message with your key", Width = 500, Height = 220, Owner = Window.GetWindow(this), Content = new StackPanel { Margin = new Thickness(12), Children = { new TextBlock { Text = "Message:", Foreground = Brushes.Gray }, msg, go } } };
        go.Click += (_, _) =>
        {
            var k = WalletKeys.Account(_seed, 0, 0);
            var sig = WalletExtras.SignMessage(k.Priv, msg.Text);
            MessageBox.Show($"pubkey: {Convert.ToHexString(k.Pub).ToLowerInvariant()}\n\nsignature (base64):\n{sig}", "Signed");
        };
        win.ShowDialog();
    }

    private void VerifyMessageDialog()
    {
        var pub = new TextBox { Width = 460 };
        var msg = new TextBox { Width = 460, AcceptsReturn = true, Height = 50, TextWrapping = TextWrapping.Wrap };
        var sig = new TextBox { Width = 460 };
        var go = new Button { Content = "Verify", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = "Signer pubkey (hex):", Foreground = Brushes.Gray }); sp.Children.Add(pub);
        sp.Children.Add(new TextBlock { Text = "Message:", Foreground = Brushes.Gray }); sp.Children.Add(msg);
        sp.Children.Add(new TextBlock { Text = "Signature (base64):", Foreground = Brushes.Gray }); sp.Children.Add(sig);
        sp.Children.Add(go);
        var win = new Window { Title = "Verify a signed message", Width = 520, Height = 320, Owner = Window.GetWindow(this), Content = sp };
        go.Click += (_, _) =>
        {
            try { bool ok = WalletExtras.VerifyMessage(Convert.FromHexString(pub.Text.Trim()), msg.Text, sig.Text.Trim()); MessageBox.Show(ok ? "VALID signature ✓" : "INVALID signature ✗", "Verify"); }
            catch (Exception ex) { MessageBox.Show("Error: " + ex.Message, "Verify"); }
        };
        win.ShowDialog();
    }

    private static Button Btn(string t) => new() { Content = t, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 6, 10, 6) };

    private void Load()
    {
        try { if (File.Exists(_path)) _w = JsonSerializer.Deserialize<File_>(File.ReadAllText(_path)) ?? new File_(); } catch { _w = new File_(); }
        if (WalletExtras.IsEncryptedSeed(_w.Seed))
        {
            _locked = true;            // encrypted on disk — must be unlocked with the password before use
            Unlock();                  // prompt now (modal); if cancelled, the wallet stays locked
            return;
        }
        bool valid = false;
        try { _seed = WalletKeys.BackupToSeed(_w.Seed); valid = _seed.Length == 32; } catch { valid = false; }
        if (!valid)
        {
            _seed = WalletKeys.NewSeed();
            _w = new File_ { Seed = WalletKeys.SeedToBackup(_seed), Balance = 0, RecvIndex = 0 };
            AppendTx("open", 1000, "opening play balance");
            Save();
        }
    }

    /// <summary>Prompt for the wallet password and decrypt the seed into memory. Loops until correct or cancelled.</summary>
    private void Unlock()
    {
        while (_locked)
        {
            var pw = PasswordPrompt("Unlock wallet", "This wallet is encrypted. Enter your password:");
            if (pw == null) return; // cancelled — remains locked; Render() shows the locked state + Unlock button
            try
            {
                _seed = WalletKeys.BackupToSeed(WalletExtras.DecryptSeed(_w.Seed, pw));
                _locked = false;
            }
            catch { MessageBox.Show("Wrong password — try again.", "Unlock failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
    }

    /// <summary>A small modal password entry (masked). Returns null if cancelled.</summary>
    private string? PasswordPrompt(string title, string prompt)
    {
        var pb = new PasswordBox { Width = 300, Margin = new Thickness(0, 6, 0, 0) };
        string? result = null;
        var ok = new Button { Content = "OK", Margin = new Thickness(0, 12, 8, 0), Padding = new Thickness(14, 6, 14, 6), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(14, 6, 14, 6), IsCancel = true };
        var sp = new StackPanel { Margin = new Thickness(14) };
        sp.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(pb);
        sp.Children.Add(new WrapPanel { Children = { ok, cancel } });
        var win = new Window { Title = title, Width = 360, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = sp, ResizeMode = ResizeMode.NoResize };
        if (Window.GetWindow(this) is { } owner && owner.IsLoaded) win.Owner = owner;
        ok.Click += (_, _) => { result = pb.Password; win.DialogResult = true; };
        win.ShowDialog();
        return win.DialogResult == true ? result : null;
    }

    /// <summary>Set or change the wallet password (encrypts the wallet seed at rest), or remove it.</summary>
    private void SetPassword()
    {
        if (_locked) { MessageBox.Show("Unlock the wallet first.", "Wallet locked"); return; }
        var pw = PasswordPrompt("Set wallet password", "Choose a password to encrypt your wallet seed on disk.\nLeave blank and press OK to REMOVE the password.");
        if (pw == null) return;
        if (pw.Length == 0)
        {
            _w.Seed = WalletKeys.SeedToBackup(_seed); Save(); // remove encryption
            _status.Text = "Wallet password removed — seed is now stored in the clear.";
            return;
        }
        var confirm = PasswordPrompt("Confirm password", "Re-enter the password to confirm:");
        if (confirm == null) return;
        if (confirm != pw) { MessageBox.Show("Passwords do not match.", "Set password"); return; }
        _w.Seed = WalletExtras.EncryptSeed(WalletKeys.SeedToBackup(_seed), pw); Save();
        _status.Text = "Wallet encrypted — your seed is now password-protected at rest.";
    }

    private string ReceiveAddress()
    {
        var pub = WalletKeys.Account(_seed, 0, (uint)_w.RecvIndex).Pub;
        var payload = new byte[21]; payload[0] = 0x00; Hashes.Hash160(pub).CopyTo(payload, 1);
        return Base58.CheckEncode(payload);
    }

    private void DoSend()
    {
        if (!Guard()) return;
        if (!long.TryParse(_amount.Text, out var a) || a <= 0) { _status.Text = "Enter a positive amount."; return; }
        if (a > _w.Balance) { _status.Text = "Insufficient balance."; return; }
        var dest = _dest.Text.Trim();
        if (dest.Length < 26) { _status.Text = "Enter a destination address."; return; }
        try { Base58.CheckDecode(dest); } catch { _status.Text = "Invalid address (bad checksum)."; return; }
        Commit("send", -a, "to " + dest);
        _status.Text = $"Sent {a} to {dest}.";
    }

    private void Restore()
    {
        var box = new TextBox { AcceptsReturn = true, Height = 80, Width = 460, TextWrapping = TextWrapping.Wrap };
        var ok = new Button { Content = "Restore", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        var win = new Window { Title = "Restore wallet — enter your seed backup", Width = 500, Height = 200, Owner = Window.GetWindow(this), Content = new StackPanel { Margin = new Thickness(12), Children = { box, ok } } };
        ok.Click += (_, _) =>
        {
            try { _seed = WalletKeys.BackupToSeed(box.Text.Trim()); _w = new File_ { Seed = WalletKeys.SeedToBackup(_seed), Balance = 0, RecvIndex = 0 }; _locked = false; AppendTx("restore", 0, "wallet restored from seed"); Save(); Render(); win.Close(); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Invalid seed"); }
        };
        win.ShowDialog();
    }

    private void AppendTx(string type, long amount, string memo)
    {
        _w.Balance += amount;
        _w.History.Add(new Tx { Time = DateTime.UtcNow.ToString("u"), Type = type, Amount = amount, Balance = _w.Balance, Memo = memo });
    }
    private void Commit(string type, long amount, string memo) { AppendTx(type, amount, memo); Save(); Render(); }

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_w, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, true);
    }

    /// <summary>Block an operation that needs the seed when the wallet is locked; nudge the user to unlock.</summary>
    private bool Guard()
    {
        if (_locked) { _status.Text = "🔒 Wallet is locked — press “Unlock…” and enter your password."; return false; }
        return true;
    }

    private void Render()
    {
        if (_locked)
        {
            _bal.Text = "🔒 locked";
            _recv.Text = "Wallet is encrypted — press “Unlock…” to enter your password.";
            _history.ItemsSource = _w.History.AsEnumerable().Reverse().ToList();
            return;
        }
        _bal.Text = _w.Balance.ToString("N0");
        _recv.Text = ReceiveAddress() + $"   (#{_w.RecvIndex})";
        _history.ItemsSource = _w.History.AsEnumerable().Reverse().ToList();
        RefreshCards();
    }

    /// <summary>Re-render the player's owned card NFTs (called when a hand deals cards into the vault).</summary>
    public void RefreshCards()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(RefreshCards)); return; }
        _cards.Children.Clear();
        var owned = _vault.Owned();
        _cardsLabel.Text = $"My cards (NFTs) — {owned.Count} held, sealed to me";
        foreach (var (card, _) in owned) { var cv = new CardView(); cv.ShowCard(card); _cards.Children.Add(cv); }
    }
}
