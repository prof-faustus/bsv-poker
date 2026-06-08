using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BsvPoker.App.Controls;
using BsvPoker.Core;
using BsvPoker.Core.Games;
using BsvPoker.Crypto;
using BsvPoker.Net.Bsv;

namespace BsvPoker.App.Views;

/// <summary>
/// Wallet tab — a REAL BSV on-chain wallet. It starts EMPTY (no play money); it holds real satoshis as
/// SPV-verified UTXOs. One 32-byte master SEED backs up everything; spending keys derive from the seed
/// (<see cref="WalletKeys"/>). Funds are learned the no-server SPV way: a payer hands over an envelope
/// (the funding transaction + a merkleblock proving it was mined), which is verified against the block
/// headers the client validated itself (<see cref="HeaderStore"/>) before a UTXO is accepted. SEND builds a
/// real signed transaction (secp256k1, low-S, FORKID) and broadcasts it over the client's own BSV node.
/// Addresses are network-aware (mainnet/testnet/regtest); the same seed works on every network.
/// Persisted atomically to the per-instance profile dir; optional password-at-rest.
/// </summary>
public sealed class WalletView : UserControl
{
    private sealed class Tx { public string Time { get; set; } = ""; public string Type { get; set; } = ""; public long Amount { get; set; } public long Balance { get; set; } public string Memo { get; set; } = ""; }
    private sealed class UtxoRec { public string Txid { get; set; } = ""; public uint Vout { get; set; } public long Value { get; set; } public uint KeyChain { get; set; } public uint KeyIndex { get; set; } public bool Spent { get; set; } public bool Confirmed { get; set; } public bool Frozen { get; set; } }
    private sealed class SendRec { public string Txid { get; set; } = ""; public long Amount { get; set; } public long Fee { get; set; } public string To { get; set; } = ""; public string Time { get; set; } = ""; public string RawHex { get; set; } = ""; }
    private sealed class Contact { public string Handle { get; set; } = ""; public string IdentityPub { get; set; } = ""; public string Note { get; set; } = ""; }
    private sealed class PayRequest { public string Address { get; set; } = ""; public long Amount { get; set; } public string Memo { get; set; } = ""; public string Time { get; set; } = ""; public string Expires { get; set; } = ""; }
    private sealed class File_
    {
        public string Seed { get; set; } = "";
        public int RecvIndex { get; set; }
        public List<UtxoRec> Utxos { get; set; } = new();
        public List<Tx> History { get; set; } = new();
        public List<SendRec> Sends { get; set; } = new();                  // real outgoing broadcasts (for history)
        public Dictionary<string, string> TxLabels { get; set; } = new();  // txid -> user label
        public Dictionary<string, string> AddrLabels { get; set; } = new();// address -> user label
        public List<Contact> Contacts { get; set; } = new();               // handle -> identity pubkey
        public List<PayRequest> Requests { get; set; } = new();            // payment requests we issued
        public string Handle { get; set; } = "";                           // this wallet's own identity handle
        public List<string> SweptKeys { get; set; } = new();               // external private keys (hex) being swept in
        public Dictionary<string, string> CoinLabels { get; set; } = new();// "txid:vout" -> user label
        public List<Vault> Vaults { get; set; } = new();                   // 2-of-2 multisig vaults (with recovery)
        public Dictionary<string, string> NftMints { get; set; } = new();  // sealedHex -> on-chain mint txid (provenance)
    }
    private sealed class Vault
    {
        public string Name { get; set; } = "";
        public uint MyKeyIndex { get; set; }            // my 2-of-2 + recovery key (chain 5)
        public string CosignerPub { get; set; } = "";   // the other signer (a contact identity pubkey)
        public long LockHeight { get; set; }            // recovery becomes available at this height
        public string RedeemHex { get; set; } = "";
        public string Address { get; set; } = "";       // shareable P2SH address
        public string FundTxid { get; set; } = "";
        public uint FundVout { get; set; }
        public long FundValue { get; set; }
        public bool Funded { get; set; }
    }

    private string _path;
    private string _dataDir = "";
    private int _accountIndex;            // 0 = the original wallet.json; 1.. = wallet-N.json (separate seeds)
    private File_ _w = new();
    private byte[] _seed = Array.Empty<byte>();   // the 32-byte master seed held in memory
    private bool _locked;                          // true when the wallet is encrypted and not yet unlocked this session

    /// <summary>This wallet's own identity handle (for naming the owner's bots: &lt;handle&gt;-Bot-NNN).</summary>
    public string MyHandle => _w.Handle;

    /// <summary>True once the seed is in memory and the wallet can derive keys (needed before the SPV filter is built).</summary>
    public bool IsUnlocked => !_locked;
    /// <summary>Raised the moment the wallet becomes usable (fresh, unlocked, or restored) so the node can load our SPV filter.</summary>
    public event Action? OnUnlocked;

    private readonly Func<BsvNode?> _node;          // current network's live node (for broadcast); may be null/peerless
    private readonly Func<HeaderStore?> _store;     // current network's validated header store (for SPV verification)
    private readonly Func<NetworkParams> _net;      // current network parameters (address version, etc.)

    private readonly TextBlock _bal = new() { Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0xE0, 0x7C)), FontSize = 16, FontWeight = FontWeights.Bold };
    // a read-only, SELECTABLE text box (not a TextBlock) so the address can be clicked, selected, and Ctrl+C'd
    private readonly TextBox _recv = new() { IsReadOnly = true, IsReadOnlyCaretVisible = true, Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0xE0, 0x7C)), Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)), BorderThickness = new Thickness(1), Padding = new Thickness(4, 3, 4, 3), FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Left, Width = 560 };
    private readonly ListView _history = new() { Height = 200 };
    private readonly TextBox _amount = new() { Width = 150, Text = "0" };
    private readonly TextBox _fee = new() { Width = 90, Text = "500" };
    private readonly TextBox _dest = new() { Width = 300 };
    private readonly TextBlock _status = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly CardVault _vault;
    private readonly WrapPanel _cards = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _cardsLabel = new() { Foreground = Brushes.Gray, Margin = new Thickness(0, 14, 0, 2), Text = "My cards (NFTs)" };

    // ---- ElectrumSV-style tabbed UI state ----
    private KeyRing _ring = null!;                                  // hash-chained Type-42 key ring over the master seed
    private bool _freshWallet;                                      // true when Load() had to create a brand-new wallet
    private readonly TabControl _tabs = new() { Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x12)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
    private readonly TextBox _sendPayTo = new() { Width = 520, FontFamily = new FontFamily("Consolas"), AcceptsReturn = true, Height = 56, TextWrapping = TextWrapping.Wrap, ToolTip = "An address, an identity handle (@bob), an identity pubkey (hex), or a bitcoin:/pay: URI" };
    private readonly TextBox _sendLabel = new() { Width = 520 };
    private readonly ComboBox _feeRate = new() { Width = 200 };
    private readonly TextBlock _sendStatus = new() { Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBox _reqAmount = new() { Width = 160, Text = "0" };
    private readonly TextBox _reqMemo = new() { Width = 320 };
    private readonly ComboBox _reqExpiry = new();
    private readonly TextBox _reqUri = new() { Width = 520, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)), Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0xE0, 0x7C)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)), BorderThickness = new Thickness(1), Padding = new Thickness(4, 3, 4, 3) };
    private readonly System.Windows.Controls.Image _reqQr = new() { Width = 220, Height = 220, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.None };
    private readonly DataGrid _historyGrid = NewGrid();
    private readonly DataGrid _coinsGrid = NewGrid();
    private readonly DataGrid _addrGrid = NewGrid();
    private readonly DataGrid _contactsGrid = NewGrid();
    private readonly DataGrid _requestsGrid = NewGrid();
    private readonly DataGrid _vaultsGrid = NewGrid();
    private readonly TextBlock _idPub = new() { Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0xE0, 0x7C)), FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _idHandle = new() { Width = 240 };

    private static DataGrid NewGrid() => new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = true,
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)), Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEC)),
        RowBackground = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18)), AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)),
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HeadersVisibility = DataGridHeadersVisibility.Column,
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)), BorderThickness = new Thickness(1),
        FontFamily = new FontFamily("Consolas"), FontSize = 12, SelectionMode = DataGridSelectionMode.Extended, RowHeaderWidth = 0,
    };

    // The ONE identity (Base ID) shared across wallet, chat, game, and NFT sealing — passed in from the profile
    // so paying an identity, encrypting to an identity, the chat key, and NFT ownership are all the SAME key.
    private readonly byte[] _identityPriv;
    private readonly byte[] _identityPub;

    public WalletView(string dataDir, CardVault vault, Func<BsvNode?> node, Func<HeaderStore?> store, Func<NetworkParams> net, byte[] identityPriv, byte[] identityPub)
    {
        _vault = vault; _node = node; _store = store; _net = net;
        _identityPriv = identityPriv; _identityPub = identityPub;
        Background = WinBg;                                  // ElectrumSVP-style LIGHT theme (not dark)
        Foreground = Ink;
        Directory.CreateDirectory(dataDir);
        _dataDir = dataDir;
        _path = AccountPath(0);
        Load();

        if (_seed.Length == 32) _ring = new KeyRing(_seed, Math.Max(1, _w.RecvIndex));
        ThemeInputs();

        // Menu bar (ElectrumSVP: File / Wallet / Account / View / Tools / Help)
        var menu = BuildMenuBar();

        // Tabs in ElectrumSVP order: History | Transactions | Send | Receive | Notifications | Destinations |
        // Coins (UTXOs) | Console — plus our Contacts / Identity (kept for the identity-payment features).
        _tabs.Items.Add(new TabItem { Header = "History", Content = BuildHistoryTab() });
        _tabs.Items.Add(new TabItem { Header = "Transactions", Content = BuildTransactionsTab() });
        _tabs.Items.Add(new TabItem { Header = "Send", Content = BuildSendTab() });
        _tabs.Items.Add(new TabItem { Header = "Receive", Content = BuildReceiveTab() });
        _tabs.Items.Add(new TabItem { Header = "Notifications", Content = BuildNotificationsTab() });
        _tabs.Items.Add(new TabItem { Header = "Destinations", Content = BuildAddressesTab() });
        _tabs.Items.Add(new TabItem { Header = "Coins (UTXOs)", Content = BuildCoinsTab() });
        _tabs.Items.Add(new TabItem { Header = "Contacts", Content = BuildContactsTab() });
        _tabs.Items.Add(new TabItem { Header = "Vaults", Content = BuildVaultsTab() });
        _tabs.Items.Add(new TabItem { Header = "Identity", Content = BuildIdentityTab() });
        _tabs.Items.Add(new TabItem { Header = "NFTs", Content = BuildNftTab() });
        _tabs.Items.Add(new TabItem { Header = "Console", Content = BuildToolsTab() });
        _tabs.Items.Add(new TabItem { Header = "Agent", Content = BuildAgentTab() });
        _tabs.SelectedIndex = 2; // open on Send like ElectrumSVP shows the wallet ready to use
        _tabs.SelectionChanged += (_, _) => Render();
        StyleTabs();

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // menu
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // tabs
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // status bar
        Grid.SetRow(menu, 0); rootGrid.Children.Add(menu);
        Grid.SetRow(_tabs, 1); rootGrid.Children.Add(_tabs);
        var statusBar = BuildStatusBar();
        Grid.SetRow(statusBar, 2); rootGrid.Children.Add(statusBar);
        Content = rootGrid;
        Render();

        // Live refresh: status bar (balance/peers/lock) ticks without disturbing grid selections; the grids
        // themselves refresh on real events (ConfirmIncoming/ConsiderIncoming/SendPayment already call Render).
        var tick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        tick.Tick += (_, _) => UpdateStatusBar();
        tick.Start();

        // First run: walk the user through the ElectrumSVP-style account wizard (create / restore / import).
        if (_freshWallet) Loaded += (_, _) => { if (_freshWallet) { _freshWallet = false; AccountWizard(); } };
    }

    // ---- DARK theme palette (matches the poker app; ElectrumSVP STRUCTURE on a dark skin) ----
    private static readonly SolidColorBrush WinBg = new(Color.FromRgb(0x0D, 0x0D, 0x0D));   // window
    private static readonly SolidColorBrush PanelBg = new(Color.FromRgb(0x16, 0x16, 0x16)); // panels
    private static readonly SolidColorBrush FieldBg = new(Color.FromRgb(0x1E, 0x1E, 0x1E)); // inputs
    private static readonly SolidColorBrush Ink = new(Color.FromRgb(0xEC, 0xEC, 0xEC));     // text
    private static readonly SolidColorBrush SubInk = new(Color.FromRgb(0xAA, 0xAA, 0xAA));  // labels
    private static readonly SolidColorBrush Line = new(Color.FromRgb(0x3A, 0x3A, 0x3A));    // borders
    private static readonly SolidColorBrush Accent = new(Color.FromRgb(0x7C, 0xE0, 0x7C));  // money green
    static WalletView() { WinBg.Freeze(); PanelBg.Freeze(); FieldBg.Freeze(); Ink.Freeze(); SubInk.Freeze(); Line.Freeze(); Accent.Freeze(); }

    // ============================ ElectrumSVP-style tabs ============================

    private static TextBlock Lbl(string t) => new() { Text = t, Foreground = SubInk, Margin = new Thickness(0, 8, 0, 2), VerticalAlignment = VerticalAlignment.Center };
    private static TextBlock H(string t) => new() { Text = t, Foreground = Ink, FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) };
    private static ScrollViewer Scroll(UIElement e) => new() { Content = e, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Background = WinBg };

    /// <summary>Dark-theme the tab headers (the default WPF tab strip is light) so the whole wallet is dark.</summary>
    private void StyleTabs()
    {
        var st = new Style(typeof(TabItem));
        st.Setters.Add(new Setter(TabItem.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A))));
        st.Setters.Add(new Setter(TabItem.ForegroundProperty, SubInk));
        st.Setters.Add(new Setter(TabItem.BorderBrushProperty, Line));
        st.Setters.Add(new Setter(TabItem.PaddingProperty, new Thickness(12, 6, 12, 6)));
        st.Setters.Add(new Setter(TabItem.FontSizeProperty, 13.0));
        var sel = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
        sel.Setters.Add(new Setter(TabItem.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A))));
        sel.Setters.Add(new Setter(TabItem.ForegroundProperty, Accent));
        sel.Setters.Add(new Setter(TabItem.FontWeightProperty, FontWeights.Bold));
        st.Triggers.Add(sel);
        var hov = new Trigger { Property = TabItem.IsMouseOverProperty, Value = true };
        hov.Setters.Add(new Setter(TabItem.ForegroundProperty, Ink));
        st.Triggers.Add(hov);
        _tabs.Resources[typeof(TabItem)] = st;
    }

    /// <summary>Dark-theme every editable input control so the wallet is one consistent dark surface.</summary>
    private void ThemeInputs()
    {
        foreach (var tb in new[] { _sendPayTo, _sendLabel, _amount, _fee, _dest, _reqAmount, _reqMemo, _idHandle })
        { tb.Background = FieldBg; tb.Foreground = Ink; tb.BorderBrush = Line; tb.BorderThickness = new Thickness(1); tb.CaretBrush = Ink; tb.Padding = new Thickness(4, 3, 4, 3); }
        _feeRate.Background = FieldBg; _feeRate.Foreground = Ink; _feeRate.BorderBrush = Line;
        _sendStatus.Foreground = SubInk;
    }

    // A two-column aligned form row (label | field) — the ElectrumSVP grid-form look.
    private static void FormRow(Grid g, int row, string label, UIElement field)
    {
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = new TextBlock { Text = label, Foreground = SubInk, Margin = new Thickness(0, 8, 12, 4), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetRow(l, row); Grid.SetColumn(l, 0); g.Children.Add(l);
        Grid.SetRow(field, row); Grid.SetColumn(field, 1); g.Children.Add(field);
    }
    private static Grid FormGrid()
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return g;
    }

    // ---- the ElectrumSVP menu bar (File / Wallet / Account / View / Tools / Help) ----
    private UIElement BuildMenuBar()
    {
        var menu = new Menu { Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x16)) };
        MenuItem M(string h) => new() { Header = h, Foreground = Ink, Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x16)) };
        MenuItem I(string h, Action a) { var mi = new MenuItem { Header = h, Foreground = Brushes.Black }; mi.Click += (_, _) => { try { a(); } catch (Exception ex) { _status.Text = ex.Message; } }; return mi; }

        var file = M("_File");
        file.Items.Add(I("_New / Restore…", () => AccountWizard()));
        file.Items.Add(I("_Open (restore from seed)…", Restore));
        file.Items.Add(I("_Save a copy of the seed backup…", BackupSeedToFile));
        file.Items.Add(I("Save a copy of the _wallet file…", SaveWalletCopy));
        file.Items.Add(new Separator());
        file.Items.Add(I("_Quit", () => Window.GetWindow(this)?.Close()));
        menu.Items.Add(file);

        var wallet = M("_Wallet");
        wallet.Items.Add(I("_Information (master public key)…", () => { if (Guard()) MessageBox.Show(Convert.ToHexString(_identityPub).ToLowerInvariant(), "Wallet information — identity / master public key"); }));
        wallet.Items.Add(I("_Password (encrypt keys)…", SetPassword));
        wallet.Items.Add(I("_Unlock…", () => { Unlock(); Render(); }));
        wallet.Items.Add(I("_Find / rescan for payments", () => { if (Guard()) RescanRequested?.Invoke(); }));
        menu.Items.Add(wallet);

        var account = M("_Account");
        account.Items.Add(I("Accounts (switch / add)…", AccountsDialog));
        account.Items.Add(I("Show _seed backup…", () => { if (Guard()) MessageBox.Show(WalletKeys.SeedToBackup(_seed), "Wallet seed — write it down"); }));
        account.Items.Add(I("New _receiving address", () => { if (Guard()) { _w.RecvIndex++; Save(); Render(); } }));
        menu.Items.Add(account);

        var view = M("_View");
        foreach (var name in new[] { "History", "Transactions", "Send", "Receive", "Notifications", "Destinations", "Coins (UTXOs)", "Contacts", "Identity", "NFTs", "Console" })
        { var nm = name; view.Items.Add(I(nm, () => { foreach (var it in _tabs.Items) if (it is TabItem ti && (string)ti.Header == nm) _tabs.SelectedItem = ti; })); }
        menu.Items.Add(view);

        var tools = M("_Tools");
        tools.Items.Add(I("_Sign message…", () => { if (Guard()) SignMessageDialog(); }));
        tools.Items.Add(I("_Verify message…", VerifyMessageDialog));
        tools.Items.Add(I("_Encrypt message to identity…", () => { if (Guard()) EncryptMessageDialog(); }));
        tools.Items.Add(I("_Decrypt message…", () => { if (Guard()) DecryptMessageDialog(); }));
        tools.Items.Add(new Separator());
        tools.Items.Add(I("_Pay to many…", () => { _tabs.SelectedIndex = 2; _sendPayTo.Focus(); _sendStatus.Text = "Pay-to-many: one  payee,amount  per line."; }));
        tools.Items.Add(I("_Sweep private key (WIF)…", () => { if (Guard()) SweepWif(); }));
        tools.Items.Add(I("_Load / broadcast a transaction…", () => { if (Guard()) LoadBroadcastTx(); }));
        tools.Items.Add(I("Pay an _invoice (BIP270)…", async () => { if (Guard()) await PayInvoice(); }));
        menu.Items.Add(tools);

        var help = M("_Help");
        help.Items.Add(I("_About this wallet", () => MessageBox.Show("BSV wallet — a full SPV, IP-to-IP, on-chain wallet with identity (Base ID + Type-42), modelled on ElectrumSVP. No server.", "About")));
        menu.Items.Add(help);
        return menu;
    }

    // ---- the ElectrumSVP status bar (balance | network | lock | notifications) ----
    private readonly TextBlock _sbBalance = new() { Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0xE0, 0x7C)), FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _sbNetwork = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _sbLock = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)), VerticalAlignment = VerticalAlignment.Center };
    private Border BuildStatusBar()
    {
        var bar = new DockPanel { LastChildFill = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(new TextBlock { Text = "Balance:  ", Foreground = SubInk, VerticalAlignment = VerticalAlignment.Center });
        left.Children.Add(_sbBalance);
        DockPanel.SetDock(left, Dock.Left); bar.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_sbLock); right.Children.Add(new TextBlock { Text = "    ", VerticalAlignment = VerticalAlignment.Center }); right.Children.Add(_sbNetwork);
        DockPanel.SetDock(right, Dock.Right); bar.Children.Add(right);
        bar.LastChildFill = true;
        var mid = new Border { Margin = new Thickness(24, 0, 24, 0), Child = _status };   // live status message
        bar.Children.Add(mid);
        return new Border { Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x16)), BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(10, 3, 10, 3), Child = bar };
    }

    // ---- Transactions tab: unpublished / pending (mirrors ElectrumSVP's split of History vs Transactions) ----
    private readonly DataGrid _pendingGrid = NewGrid();
    private UIElement BuildTransactionsTab()
    {
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(H("Transactions (pending / unconfirmed)"));
        _pendingGrid.Columns.Clear();
        _pendingGrid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new System.Windows.Data.Binding("Status"), Width = 110 });
        _pendingGrid.Columns.Add(new DataGridTextColumn { Header = "Amount (sat)", Binding = new System.Windows.Data.Binding("Amount"), Width = 130 });
        _pendingGrid.Columns.Add(new DataGridTextColumn { Header = "Txid / address", Binding = new System.Windows.Data.Binding("Memo"), Width = 560 });
        _pendingGrid.Height = 440;
        sp.Children.Add(_pendingGrid);
        return Scroll(sp);
    }

    // ---- Notifications tab ----
    private readonly System.Collections.ObjectModel.ObservableCollection<string> _notes = new();
    private UIElement BuildNotificationsTab()
    {
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(H("Notifications"));
        var list = new ListBox { ItemsSource = _notes, Height = 440, Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)), Foreground = Ink, BorderBrush = Line, BorderThickness = new Thickness(1) };
        sp.Children.Add(list);
        return Scroll(sp);
    }
    private void Notify(string m) { if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => Notify(m))); return; } _notes.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {m}"); }

    // ---- built-in SMART AGENT: understands wallet commands and PREPARES actions (human confirms money) ----
    private readonly TextBox _historySearch = new();
    private void ApplyHistoryFilter()
    {
        var q = _historySearch.Text.Trim();
        var rows = DerivedHistory();
        if (q.Length > 0) rows = rows.Where(t => (t.Memo + " " + t.Type + " " + t.Amount + " " + t.Time).Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        _historyGrid.ItemsSource = rows;
    }
    private readonly System.Collections.ObjectModel.ObservableCollection<string> _agentLog = new();
    private readonly TextBox _agentInput = new() { Width = 560 };
    private UIElement BuildAgentTab()
    {
        ThemeOne(_agentInput);
        var sp = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        sp.Children.Add(H("Wallet agent"));
        sp.Children.Add(new TextBlock { Text = "Ask the agent in plain language. It reads your wallet and PREPARES actions; you always press Send yourself for anything that moves money. Try: balance · new address · coins · history · identity · rescan · send 1000 to @bob · pay <address> 5000 · contacts · lock · help", Foreground = SubInk, TextWrapping = TextWrapping.Wrap, MaxWidth = 660, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) });
        var log = new ListBox { ItemsSource = _agentLog, Height = 380, Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x12)), Foreground = Ink, BorderBrush = Line, BorderThickness = new Thickness(1), FontFamily = new FontFamily("Consolas") };
        sp.Children.Add(log);
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(_agentInput);
        var ask = Btn("Ask"); ask.Margin = new Thickness(8, 0, 0, 0);
        void Go() { var q = _agentInput.Text.Trim(); if (q.Length == 0) return; _agentLog.Insert(0, "you ▸ " + q); _agentInput.Clear(); AgentHandle(q); }
        ask.Click += (_, _) => Go();
        _agentInput.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Go(); };
        row.Children.Add(ask);
        sp.Children.Add(row);
        if (_agentLog.Count == 0) _agentLog.Insert(0, "agent ▸ Hi. I'm your wallet agent. Type 'help' for what I can do.");
        return Scroll(sp);
    }
    private void ThemeOne(TextBox tb) { tb.Background = FieldBg; tb.Foreground = Ink; tb.BorderBrush = Line; tb.BorderThickness = new Thickness(1); tb.CaretBrush = Ink; tb.Padding = new Thickness(4, 3, 4, 3); }
    private void A(string m) => _agentLog.Insert(0, "agent ▸ " + m);

    /// <summary>Interpret a wallet command. Read-only queries run immediately; anything that spends only PREPARES
    /// the Send tab (the human presses Send) — per the rule that a person chooses every money action.</summary>
    private void AgentHandle(string q)
    {
        var s = q.Trim(); var lower = s.ToLowerInvariant();
        try
        {
            if (lower is "help" or "?" ) { A("I can: balance · new address · coins · history · identity · contacts · rescan · lock · unlock · send <sat> to <payee> · pay <payee> <sat>. For sends I fill in the Send tab and you press Send."); return; }
            if (_locked && lower is not ("unlock" or "help" or "?")) { A("The wallet is locked. Say 'unlock' (or use Wallet → Unlock)."); return; }
            if (lower is "balance" or "what is my balance" or "bal") { A($"Confirmed balance: {Balance:N0} sat" + (Pending > 0 ? $", plus {Pending:N0} sat pending." : ".")); return; }
            if (lower is "new address" or "address" or "receive") { _w.RecvIndex++; Save(); Render(); A($"Your receiving address is {ReceiveAddress()} (also on the Receive tab)."); return; }
            if (lower is "coins" or "utxos" or "utxo") { var n = _w.Utxos.Count(u => !u.Spent); A($"{n} spendable coin(s), {Balance:N0} sat total. See the Coins tab for full control."); return; }
            if (lower is "history") { var h = DerivedHistory(); A(h.Count == 0 ? "No transactions yet." : $"{h.Count} entries. Most recent: {h[^1].Type} {h[^1].Amount:N0} sat."); return; }
            if (lower is "identity" or "who am i") { A($"Your identity (Base ID) is {Convert.ToHexString(_identityPub).ToLowerInvariant()}" + (string.IsNullOrEmpty(_w.Handle) ? "" : $", handle @{_w.Handle}") + "."); return; }
            if (lower is "contacts") { A(_w.Contacts.Count == 0 ? "No contacts yet (add them in the Contacts tab)." : "Contacts: " + string.Join(", ", _w.Contacts.Select(c => "@" + c.Handle))); return; }
            if (lower is "rescan") { RescanRequested?.Invoke(); A("Rescanning the chain for your payments…"); return; }
            var mNc = System.Text.RegularExpressions.Regex.Match(s, @"^newcontact\s+(\S+)\s+([0-9a-fA-F]{66})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mNc.Success) { ImportContact(mNc.Groups[1].Value.TrimStart('@'), mNc.Groups[2].Value.ToLowerInvariant()); A($"Saved contact @{mNc.Groups[1].Value.TrimStart('@')}."); return; }
            if (lower is "lock") { _locked = true; Render(); A("Locked."); return; }
            if (lower is "unlock") { Unlock(); Render(); A(_locked ? "Still locked." : "Unlocked."); return; }

            // send <amount> to <payee>   |   pay <payee> <amount>
            var mSend = System.Text.RegularExpressions.Regex.Match(s, @"^send\s+([\d,]+)\s+to\s+(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var mPay = System.Text.RegularExpressions.Regex.Match(s, @"^pay\s+(\S+)\s+([\d,]+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            string? payee = null, amtStr = null;
            if (mSend.Success) { amtStr = mSend.Groups[1].Value; payee = mSend.Groups[2].Value.Trim(); }
            else if (mPay.Success) { payee = mPay.Groups[1].Value.Trim(); amtStr = mPay.Groups[2].Value; }
            if (payee != null && long.TryParse(amtStr!.Replace(",", ""), out var amt))
            {
                _sendPayTo.Text = payee; _amount.Text = amt.ToString(); SelectTab("Send");
                A($"Prepared a payment of {amt:N0} sat to {payee} on the Send tab. Review it and press Send — I never move money for you.");
                return;
            }
            // send max to <payee>
            var mMax = System.Text.RegularExpressions.Regex.Match(s, @"^send\s+max\s+to\s+(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mMax.Success)
            {
                var max = Math.Max(0, Balance - EstimateFee(1));
                _sendPayTo.Text = mMax.Groups[1].Value.Trim(); _amount.Text = max.ToString(); SelectTab("Send");
                A($"Prepared a max payment of {max:N0} sat (balance minus fee) to {mMax.Groups[1].Value.Trim()}. Review and press Send.");
                return;
            }
            // explain <topic>
            if (lower.StartsWith("explain") || lower.StartsWith("what is"))
            {
                if (lower.Contains("identity")) { A("Your identity is a Base ID key that's never an address — it only derives one-time Type-42 sub-keys. You pay/chat/play under it; NFTs are sealed to it."); return; }
                if (lower.Contains("spv")) { A("SPV = the wallet validates block headers itself and accepts a coin only with a merkle proof against those headers. No server."); return; }
                if (lower.Contains("nft")) { A("Card NFTs are 1-sat on-chain outputs sealed to your identity; the NFTs tab shows the ones you own."); return; }
                if (lower.Contains("fee")) { A("Fees are estimated from your fee-rate choice and the number of inputs; you can also set a custom fee on the Send tab."); return; }
                A("I can explain: identity, spv, nft, fee."); return;
            }
            A("I didn't understand that. Type 'help' for what I can do.");
        }
        catch (Exception ex) { A("Error: " + ex.Message); }
    }

    // ---- the ElectrumSVP-style account wizard (Standard / Restore / Import) ----
    private void AccountWizard()
    {
        var sp = new StackPanel { Margin = new Thickness(20) };
        sp.Children.Add(new TextBlock { Text = "Add an account", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Ink, Margin = new Thickness(0, 0, 0, 6) });
        sp.Children.Add(new TextBlock { Text = "How would you like to set up this wallet? Your keys are kept encrypted and password-protected.", TextWrapping = TextWrapping.Wrap, Foreground = SubInk, Margin = new Thickness(0, 0, 0, 12), MaxWidth = 420 });
        var win = new Window { Title = "BSV wallet — account wizard", Width = 480, Height = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this), Background = WinBg };
        Button Opt(string t, string sub)
        {
            var b = new Button { Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(12, 8, 12, 8), HorizontalContentAlignment = HorizontalAlignment.Left };
            b.Content = new StackPanel { Children = { new TextBlock { Text = t, FontWeight = FontWeights.Bold }, new TextBlock { Text = sub, Foreground = SubInk, TextWrapping = TextWrapping.Wrap } } };
            return b;
        }
        var standard = Opt("Standard wallet (recommended)", "Create a brand-new wallet with a fresh BSV-native seed.");
        var restore = Opt("Restore from a seed backup", "I already have a wallet seed and want to restore it.");
        var import = Opt("Import a private key (WIF)", "Sweep coins controlled by an existing private key into this wallet.");
        standard.Click += (_, _) => { win.Close(); StandardSeedFlow(); };
        restore.Click += (_, _) => { win.Close(); Restore(); };
        import.Click += (_, _) => { win.Close(); SweepWif(); };
        sp.Children.Add(standard); sp.Children.Add(restore); sp.Children.Add(import);
        win.Content = new ScrollViewer { Content = sp };
        win.ShowDialog();
    }

    /// <summary>The Standard new-wallet flow: show the seed for backup → confirm it was written down → set a
    /// password. Mirrors ElectrumSVP's create-seed / confirm-seed / password pages (BSV-native seed, not BIP39).</summary>
    private void StandardSeedFlow()
    {
        var seed = WalletKeys.SeedToBackup(_seed);
        // page 1: show the seed
        var seedBox = new TextBox { Text = seed, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), Background = FieldBg, Foreground = Accent, BorderBrush = Line, BorderThickness = new Thickness(1), Padding = new Thickness(6), Width = 420 };
        var wrote = new CheckBox { Content = "I have written my seed down and stored it safely", Foreground = Ink, Margin = new Thickness(0, 10, 0, 0) };
        var next = new Button { Content = "Continue", Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(12, 6, 12, 6), IsEnabled = false };
        wrote.Checked += (_, _) => next.IsEnabled = true; wrote.Unchecked += (_, _) => next.IsEnabled = false;
        var sp1 = new StackPanel { Margin = new Thickness(16) };
        sp1.Children.Add(new TextBlock { Text = "Back up your wallet seed", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Ink });
        sp1.Children.Add(new TextBlock { Text = "This single seed recovers your entire wallet. Anyone with it controls your coins. Write it down — it is shown only now.", Foreground = SubInk, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(0, 4, 0, 8) });
        sp1.Children.Add(seedBox); sp1.Children.Add(wrote); sp1.Children.Add(next);
        var w1 = new Window { Title = "New wallet — back up your seed", Width = 480, Height = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this), Background = WinBg, Content = new ScrollViewer { Content = sp1 } };
        next.Click += (_, _) => w1.Close();
        w1.ShowDialog();

        // page 2: confirm the seed by re-typing it
        var confirm = new TextBox { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), Background = FieldBg, Foreground = Ink, BorderBrush = Line, BorderThickness = new Thickness(1), Padding = new Thickness(6), Width = 420, Height = 60, AcceptsReturn = true };
        var ok = new Button { Content = "Confirm", Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        var sp2 = new StackPanel { Margin = new Thickness(16) };
        sp2.Children.Add(new TextBlock { Text = "Confirm your seed", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Ink });
        sp2.Children.Add(new TextBlock { Text = "Re-type the seed exactly to confirm you have it.", Foreground = SubInk, Margin = new Thickness(0, 4, 0, 8) });
        sp2.Children.Add(confirm); sp2.Children.Add(ok);
        var w2 = new Window { Title = "New wallet — confirm your seed", Width = 480, Height = 250, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this), Background = WinBg, Content = new ScrollViewer { Content = sp2 } };
        ok.Click += (_, _) =>
        {
            if (confirm.Text.Trim() != seed) { MessageBox.Show("That does not match your seed. Try again (copy it exactly).", "Confirm seed"); return; }
            w2.Close();
        };
        w2.ShowDialog();

        // page 3: set a password (encrypt the keys at rest)
        SetPassword();
        Render();
        _status.Text = "New wallet ready — seed backed up and keys encrypted.";
    }

    private string AccountPath(int i) => Path.Combine(_dataDir, i == 0 ? "wallet.json" : $"wallet-{i}.json");

    /// <summary>How many account files exist (0..N). Account 0 is the original wallet.</summary>
    private int AccountCount() { int n = 0; while (File.Exists(AccountPath(n))) n++; return Math.Max(1, n); }

    /// <summary>Switch the active account to a separate seed/wallet file (account 0 = the original wallet). Each
    /// account has its own seed, coins, and history; the identity (chat/game/NFT) stays the profile identity.</summary>
    private void SwitchAccount(int i)
    {
        try
        {
            Save();                              // persist the current account first
            _accountIndex = i;
            _path = AccountPath(i);
            _locked = false; _w = new File_(); _seed = Array.Empty<byte>();
            Load();                              // loads (or creates) account i; may prompt the wizard/unlock
            if (_seed.Length == 32) _ring = new KeyRing(_seed, Math.Max(1, _w.RecvIndex));
            if (_freshWallet) { _freshWallet = false; AccountWizard(); }
            Render();
            OnUnlocked?.Invoke();                // re-arm the SPV filter for the new account's addresses
            _status.Text = $"Switched to account #{i}.";
        }
        catch (Exception ex) { _status.Text = "Account switch failed: " + ex.Message; }
    }

    private void AccountsDialog()
    {
        int count = AccountCount();
        var list = new ListBox { Height = 160, Width = 360 };
        for (int i = 0; i < count; i++) list.Items.Add($"Account #{i}" + (i == _accountIndex ? "  (active)" : "") + $"  [{(i == 0 ? "wallet.json" : $"wallet-{i}.json")}]");
        list.SelectedIndex = _accountIndex;
        var open = Btn("Open selected"); var add = Btn("Add a new account");
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = "Accounts (each its own seed + coins):", Foreground = Ink }); sp.Children.Add(list);
        sp.Children.Add(new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Children = { open, add } });
        var win = new Window { Title = "Accounts", Width = 420, Height = 280, Owner = Window.GetWindow(this), Background = WinBg, Content = new ScrollViewer { Content = sp } };
        open.Click += (_, _) => { if (list.SelectedIndex >= 0) { win.Close(); SwitchAccount(list.SelectedIndex); } };
        add.Click += (_, _) => { win.Close(); SwitchAccount(count); };   // next free index → fresh wallet wizard
        win.ShowDialog();
    }

    private void BackupSeedToFile()
    {
        if (!Guard()) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Text (*.txt)|*.txt", FileName = "bsv-wallet-seed-backup.txt" };
        if (dlg.ShowDialog() == true) { File.WriteAllText(dlg.FileName, WalletKeys.SeedToBackup(_seed)); _status.Text = "Seed backup saved (keep it secret)."; }
    }

    private void SaveWalletCopy()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Wallet (*.json)|*.json", FileName = "bsvpoker-wallet-copy.json" };
        if (dlg.ShowDialog() != true) return;
        try { Save(); File.Copy(_path, dlg.FileName, true); _status.Text = "Wallet file copied (keys remain encrypted if a password is set)."; }
        catch (Exception ex) { MessageBox.Show("Could not save copy: " + ex.Message, "Save copy"); }
    }

    private void LoadTxFromFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Transaction (*.txt;*.hex;*.tx)|*.txt;*.hex;*.tx|All files (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var hex = File.ReadAllText(dlg.FileName).Trim();
            var raw = Convert.FromHexString(hex);
            var tx = Chain.Deserialize(raw);
            var node = _node();
            if (node == null || node.PeerCount == 0) { MessageBox.Show("No BSV peers connected.", "Broadcast"); return; }
            node.Broadcast(raw);
            _status.Text = "Broadcast tx " + Chain.Txid(tx);
        }
        catch (Exception ex) { MessageBox.Show("Could not load/broadcast: " + ex.Message, "Load transaction"); }
    }

    private void ExportLabels()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "bsvpoker-labels.json" };
        if (dlg.ShowDialog() != true) return;
        var obj = new { txLabels = _w.TxLabels, addrLabels = _w.AddrLabels };
        File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
        _status.Text = "Labels exported.";
    }

    private void ImportLabels()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON (*.json)|*.json|All files (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(dlg.FileName));
            if (doc.RootElement.TryGetProperty("txLabels", out var tl)) foreach (var p in tl.EnumerateObject()) _w.TxLabels[p.Name] = p.Value.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("addrLabels", out var al)) foreach (var p in al.EnumerateObject()) _w.AddrLabels[p.Name] = p.Value.GetString() ?? "";
            Save(); Render(); _status.Text = "Labels imported.";
        }
        catch (Exception ex) { MessageBox.Show("Bad labels file: " + ex.Message, "Import labels"); }
    }

    private void ExportAddresses()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "bsvpoker-addresses.csv" };
        if (dlg.ShowDialog() != true) return;
        var lines = new List<string> { "path,address,balance_sat,label" };
        for (uint i = 0; i <= (uint)_w.RecvIndex; i++)
        {
            var addr = AddressForKey(0, i);
            long bal = _w.Utxos.Where(u => !u.Spent && u.KeyChain == 0 && u.KeyIndex == i).Sum(u => u.Value);
            var lbl = _w.AddrLabels.TryGetValue(addr, out var l) ? l.Replace(",", " ") : "";
            lines.Add($"receive/{i},{addr},{bal},{lbl}");
        }
        File.WriteAllLines(dlg.FileName, lines);
        _status.Text = "Addresses exported.";
    }

    // ---- SEND: ElectrumSVP-style aligned grid form (Pay to / Amount+Max / Fee / Description) ----
    private UIElement BuildSendTab()
    {
        var sp = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        sp.Children.Add(H("Send"));

        var g = FormGrid();
        int r = 0;
        FormRow(g, r++, "Pay to", _sendPayTo);

        var amtPanel = new StackPanel { Orientation = Orientation.Horizontal };
        amtPanel.Children.Add(_amount);
        var max = Btn("Max"); max.Margin = new Thickness(8, 0, 0, 0); max.Click += (_, _) => { if (Guard()) _amount.Text = Math.Max(0, Balance - EstimateFee(1)).ToString(); };
        amtPanel.Children.Add(max);
        amtPanel.Children.Add(new TextBlock { Text = " sat", Foreground = SubInk, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
        FormRow(g, r++, "Amount", amtPanel);

        var feePanel = new StackPanel { Orientation = Orientation.Horizontal };
        _feeRate.Items.Clear();
        foreach (var fr in new[] { "0.5 sat/kB", "1 sat/kB (standard)", "5 sat/kB (priority)", "Custom fee (sat)…" }) _feeRate.Items.Add(fr);
        _feeRate.SelectedIndex = 1;
        feePanel.Children.Add(_feeRate);
        feePanel.Children.Add(new TextBlock { Text = "  custom: ", Foreground = SubInk, VerticalAlignment = VerticalAlignment.Center });
        feePanel.Children.Add(_fee);
        FormRow(g, r++, "Fee", feePanel);

        FormRow(g, r++, "Description", _sendLabel);
        sp.Children.Add(g);

        sp.Children.Add(new TextBlock { Text = "Pay to accepts: an address · an identity handle (@bob) · an identity public key · a bitcoin:/pay: URI · or several lines of \"payee,amount\" for pay-to-many.", Foreground = SubInk, TextWrapping = TextWrapping.Wrap, MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) });

        var btns = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var sendBtn = Btn("Send"); sendBtn.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)); sendBtn.Foreground = Brushes.White; sendBtn.FontWeight = FontWeights.Bold;
        sendBtn.Click += async (_, _) => { if (Guard()) await SendPayment(); };
        var preview = Btn("Preview…"); preview.Click += (_, _) => { if (Guard()) PreviewSend(); };
        var paste = Btn("Paste URI / invoice…"); paste.Click += (_, _) => PasteUri();
        var invoice = Btn("Pay an invoice (BIP270)…"); invoice.Click += async (_, _) => { if (Guard()) await PayInvoice(); };
        var clear = Btn("Clear"); clear.Click += (_, _) => { _sendPayTo.Clear(); _sendLabel.Clear(); _amount.Text = "0"; _sendStatus.Text = ""; };
        btns.Children.Add(sendBtn); btns.Children.Add(preview); btns.Children.Add(paste); btns.Children.Add(invoice); btns.Children.Add(clear);
        sp.Children.Add(btns);
        sp.Children.Add(_sendStatus);
        void Est() { if (long.TryParse(_amount.Text, out var a) && a > 0) { var f = EstimateFee(1); _sendStatus.Text = $"Estimated fee {f:N0} sat · total {a + f:N0} sat · balance {Balance:N0} sat" + (a + f > Balance ? "  — INSUFFICIENT" : ""); } }
        _amount.TextChanged += (_, _) => Est();
        _feeRate.SelectionChanged += (_, _) => Est();
        _fee.TextChanged += (_, _) => Est();
        return Scroll(sp);
    }

    // ---- RECEIVE: ElectrumSVP-style grid form on the left, 200x200 QR on the right, requests below ----
    private UIElement BuildReceiveTab()
    {
        var sp = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        sp.Children.Add(H("Receive"));

        var topRow = new Grid();
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var form = FormGrid(); int r = 0;
        var addrPanel = new StackPanel();
        addrPanel.Children.Add(_recv);
        var addrBtns = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var copyAddr = Btn("Copy address"); copyAddr.Click += (_, _) => { if (Guard()) CopyToClipboard(ReceiveAddress(), "Address copied."); };
        var newAddr = Btn("New address"); newAddr.Click += (_, _) => { if (Guard()) { _w.RecvIndex++; Save(); Render(); } };
        addrBtns.Children.Add(copyAddr); addrBtns.Children.Add(newAddr);
        addrPanel.Children.Add(addrBtns);
        FormRow(form, r++, "Receiving destination", addrPanel);
        FormRow(form, r++, "Requested amount", _reqAmount);
        FormRow(form, r++, "Description", _reqMemo);
        _reqExpiry.Items.Clear();
        foreach (var e in new[] { "Never", "1 hour", "1 day", "1 week" }) _reqExpiry.Items.Add(e);
        _reqExpiry.SelectedIndex = 0; _reqExpiry.Background = FieldBg; _reqExpiry.Foreground = Ink; _reqExpiry.BorderBrush = Line; _reqExpiry.Width = 160;
        FormRow(form, r++, "Request expires", _reqExpiry);
        var reqBtns = new WrapPanel();
        var makeReq = Btn("Save request"); makeReq.Click += (_, _) => { if (Guard()) CreateRequest(); };
        var copyUri = Btn("Copy URI"); copyUri.Click += (_, _) => CopyToClipboard(_reqUri.Text, "Payment URI copied.");
        reqBtns.Children.Add(makeReq); reqBtns.Children.Add(copyUri);
        FormRow(form, r++, "", reqBtns);
        FormRow(form, r++, "Payment URI", _reqUri);
        Grid.SetColumn(form, 0); topRow.Children.Add(form);

        var qrPanel = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        qrPanel.Children.Add(new Border { Background = Brushes.White, Padding = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Left, Child = _reqQr });
        Grid.SetColumn(qrPanel, 1); topRow.Children.Add(qrPanel);
        sp.Children.Add(topRow);

        sp.Children.Add(Lbl("Saved requests"));
        _requestsGrid.Columns.Clear();
        _requestsGrid.Columns.Add(new DataGridTextColumn { Header = "Time", Binding = new System.Windows.Data.Binding("Time"), IsReadOnly = true, Width = 150 });
        _requestsGrid.Columns.Add(new DataGridTextColumn { Header = "Amount", Binding = new System.Windows.Data.Binding("Amount"), IsReadOnly = true, Width = 110 });
        _requestsGrid.Columns.Add(new DataGridTextColumn { Header = "Description", Binding = new System.Windows.Data.Binding("Memo"), IsReadOnly = true, Width = 220 });
        _requestsGrid.Columns.Add(new DataGridTextColumn { Header = "Expires", Binding = new System.Windows.Data.Binding("Expires"), IsReadOnly = true, Width = 130 });
        _requestsGrid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new System.Windows.Data.Binding("Status"), IsReadOnly = true, Width = 80 });
        _requestsGrid.Columns.Add(new DataGridTextColumn { Header = "Address", Binding = new System.Windows.Data.Binding("Address"), IsReadOnly = true, Width = 360 });
        _requestsGrid.Height = 160;
        sp.Children.Add(_requestsGrid);

        sp.Children.Add(Lbl("SPV funding (no server) — import a payment proof, find by txid, or claim a payment sent to your identity"));
        var fund = new WrapPanel();
        var importBtn = Btn("Import funding (SPV envelope)…"); importBtn.Click += (_, _) => { if (Guard()) ImportFunding(); };
        var makeEnv = Btn("Create funding envelope…"); makeEnv.Click += async (_, _) => { if (Guard()) await CreateEnvelope(); };
        var findTx = Btn("Find a payment by txid…"); findTx.Click += async (_, _) => { if (Guard()) await FindByTxid(); };
        var rescan = Btn("Rescan now"); rescan.Click += (_, _) => { if (Guard()) { _status.Text = "Rescanning the chain for payments…"; RescanRequested?.Invoke(); } };
        var claim = Btn("Claim a payment to my identity…"); claim.Click += (_, _) => { if (Guard()) ClaimIdentityPayment(); };
        fund.Children.Add(importBtn); fund.Children.Add(makeEnv); fund.Children.Add(findTx); fund.Children.Add(rescan); fund.Children.Add(claim);
        sp.Children.Add(fund);
        return Scroll(sp);
    }

    // ---- HISTORY: every real movement (received + sent), editable labels, running balance ----
    private UIElement BuildHistoryTab()
    {
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(H("History"));
        var searchRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        searchRow.Children.Add(new TextBlock { Text = "Search ", Foreground = SubInk, VerticalAlignment = VerticalAlignment.Center });
        ThemeOne(_historySearch); _historySearch.Width = 360;
        _historySearch.TextChanged += (_, _) => ApplyHistoryFilter();
        searchRow.Children.Add(_historySearch);
        sp.Children.Add(searchRow);
        _historyGrid.Columns.Clear();
        _historyGrid.Columns.Add(new DataGridTextColumn { Header = "Time", Binding = new System.Windows.Data.Binding("Time"), IsReadOnly = true, Width = 150 });
        _historyGrid.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new System.Windows.Data.Binding("Type"), IsReadOnly = true, Width = 90 });
        _historyGrid.Columns.Add(new DataGridTextColumn { Header = "Amount (sat)", Binding = new System.Windows.Data.Binding("Amount"), IsReadOnly = true, Width = 120 });
        _historyGrid.Columns.Add(new DataGridTextColumn { Header = "Balance", Binding = new System.Windows.Data.Binding("Balance"), IsReadOnly = true, Width = 120 });
        _historyGrid.Columns.Add(new DataGridTextColumn { Header = "Description / txid", Binding = new System.Windows.Data.Binding("Memo"), IsReadOnly = true, Width = 460 });
        _historyGrid.Height = 460;
        sp.Children.Add(_historyGrid);
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var copyTxid = Btn("Copy txid"); copyTxid.Click += (_, _) => { if (_historyGrid.SelectedItem is Tx t) CopyToClipboard(t.Memo.Split(' ')[0], "txid copied."); };
        var details = Btn("Transaction details…"); details.Click += (_, _) => { if (_historyGrid.SelectedItem is Tx t) TxDetails(t.Memo.Split(' ')[0]); };
        var setLabel = Btn("Set label…"); setLabel.Click += (_, _) => { if (_historyGrid.SelectedItem is Tx t) SetTxLabel(t.Memo.Split(' ')[0]); };
        var export = Btn("Export history (CSV)…"); export.Click += (_, _) => ExportHistory();
        row.Children.Add(copyTxid); row.Children.Add(details); row.Children.Add(setLabel); row.Children.Add(export);
        _historyGrid.MouseDoubleClick += (_, _) => { if (_historyGrid.SelectedItem is Tx t) TxDetails(t.Memo.Split(' ')[0]); };
        sp.Children.Add(row);
        return Scroll(sp);
    }

    // ---- COINS: every UTXO, freeze/unfreeze, spend selected, labels ----
    private UIElement BuildCoinsTab()
    {
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(H("Coins (UTXOs) — full coin control"));
        _coinsGrid.Columns.Clear();
        _coinsGrid.Columns.Add(new DataGridTextColumn { Header = "Outpoint", Binding = new System.Windows.Data.Binding("Outpoint"), IsReadOnly = true, Width = 340 });
        _coinsGrid.Columns.Add(new DataGridTextColumn { Header = "Address", Binding = new System.Windows.Data.Binding("Address"), IsReadOnly = true, Width = 320 });
        _coinsGrid.Columns.Add(new DataGridTextColumn { Header = "Value (sat)", Binding = new System.Windows.Data.Binding("Value"), IsReadOnly = true, Width = 120 });
        _coinsGrid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new System.Windows.Data.Binding("Status"), IsReadOnly = true, Width = 100 });
        _coinsGrid.Columns.Add(new DataGridTextColumn { Header = "Frozen", Binding = new System.Windows.Data.Binding("Frozen"), IsReadOnly = true, Width = 70 });
        _coinsGrid.Columns.Add(new DataGridTextColumn { Header = "Label", Binding = new System.Windows.Data.Binding("Label"), IsReadOnly = true, Width = 200 });
        _coinsGrid.Height = 440;
        sp.Children.Add(_coinsGrid);
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var freeze = Btn("Freeze"); freeze.Click += (_, _) => SetFrozen(true);
        var unfreeze = Btn("Unfreeze"); unfreeze.Click += (_, _) => SetFrozen(false);
        var spend = Btn("Spend selected…"); spend.Click += (_, _) => { if (Guard()) SpendSelectedCoins(); };
        var copyOp = Btn("Copy outpoint"); copyOp.Click += (_, _) => { if (_coinsGrid.SelectedItem != null) CopyToClipboard(PropOf(_coinsGrid.SelectedItem, "FullOutpoint"), "Outpoint copied."); };
        var details = Btn("Details…"); details.Click += (_, _) => { if (_coinsGrid.SelectedItem != null) TxDetails(PropOf(_coinsGrid.SelectedItem, "FullOutpoint").Split(':')[0]); };
        var clabel = Btn("Set label…"); clabel.Click += (_, _) => { if (_coinsGrid.SelectedItem != null) SetCoinLabel(PropOf(_coinsGrid.SelectedItem, "FullOutpoint")); };
        row.Children.Add(freeze); row.Children.Add(unfreeze); row.Children.Add(spend); row.Children.Add(copyOp); row.Children.Add(details); row.Children.Add(clabel);
        sp.Children.Add(row);
        return Scroll(sp);
    }

    // ---- ADDRESSES: the KeyRing — per-address balance, derivation path, WIF, freeze, labels ----
    private UIElement BuildAddressesTab()
    {
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(H("Addresses / Keys (hash-chained Type-42 key ring)"));
        _addrGrid.Columns.Clear();
        _addrGrid.Columns.Add(new DataGridTextColumn { Header = "Address", Binding = new System.Windows.Data.Binding("Address"), IsReadOnly = true, Width = 340 });
        _addrGrid.Columns.Add(new DataGridTextColumn { Header = "Path", Binding = new System.Windows.Data.Binding("Path"), IsReadOnly = true, Width = 150 });
        _addrGrid.Columns.Add(new DataGridTextColumn { Header = "Balance (sat)", Binding = new System.Windows.Data.Binding("Balance"), IsReadOnly = true, Width = 120 });
        _addrGrid.Columns.Add(new DataGridTextColumn { Header = "Used", Binding = new System.Windows.Data.Binding("Used"), IsReadOnly = true, Width = 60 });
        _addrGrid.Columns.Add(new DataGridTextColumn { Header = "Label", Binding = new System.Windows.Data.Binding("Label"), IsReadOnly = true, Width = 260 });
        _addrGrid.Height = 440;
        sp.Children.Add(_addrGrid);
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var copy = Btn("Copy address"); copy.Click += (_, _) => { if (_addrGrid.SelectedItem != null) CopyToClipboard(PropOf(_addrGrid.SelectedItem, "Address"), "Address copied."); };
        var label = Btn("Set label…"); label.Click += (_, _) => { if (_addrGrid.SelectedItem != null) SetAddrLabel(PropOf(_addrGrid.SelectedItem, "Address")); };
        var wif = Btn("Show private key (WIF)…"); wif.Click += (_, _) => { if (Guard()) ShowWif(); };
        var more = Btn("Show 20 more addresses"); more.Click += (_, _) => { if (Guard()) { _w.RecvIndex += 20; Save(); Render(); } };
        row.Children.Add(copy); row.Children.Add(label); row.Children.Add(wif); row.Children.Add(more);
        _addrGrid.MouseDoubleClick += (_, _) => { if (_addrGrid.SelectedItem != null) SetAddrLabel(PropOf(_addrGrid.SelectedItem, "Address")); };
        sp.Children.Add(row);
        return Scroll(sp);
    }

    // ---- CONTACTS: handle ↔ identity public key; pay or chat a contact ----
    private UIElement BuildContactsTab()
    {
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(H("Contacts (identities / handles)"));
        _contactsGrid.Columns.Clear();
        _contactsGrid.Columns.Add(new DataGridTextColumn { Header = "Handle", Binding = new System.Windows.Data.Binding("Handle"), IsReadOnly = true, Width = 160 });
        _contactsGrid.Columns.Add(new DataGridTextColumn { Header = "Identity public key", Binding = new System.Windows.Data.Binding("IdentityPub"), IsReadOnly = true, Width = 520 });
        _contactsGrid.Columns.Add(new DataGridTextColumn { Header = "Note", Binding = new System.Windows.Data.Binding("Note"), IsReadOnly = true, Width = 220 });
        _contactsGrid.Height = 360;
        sp.Children.Add(_contactsGrid);
        var add = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var hBox = new TextBox { Width = 160 }; var pBox = new TextBox { Width = 520, FontFamily = new FontFamily("Consolas") };
        add.Children.Add(new TextBlock { Text = "Handle ", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center }); add.Children.Add(hBox);
        add.Children.Add(new TextBlock { Text = "  Identity pubkey (hex) ", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center }); add.Children.Add(pBox);
        var addBtn = Btn("Add / update");
        addBtn.Click += (_, _) => { AddContact(hBox.Text.Trim(), pBox.Text.Trim()); hBox.Clear(); pBox.Clear(); };
        add.Children.Add(addBtn);
        sp.Children.Add(add);
        var ops = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var pay = Btn("Pay this contact…"); pay.Click += (_, _) => { if (_contactsGrid.SelectedItem != null) { _sendPayTo.Text = "@" + PropOf(_contactsGrid.SelectedItem, "Handle"); SelectTab("Send"); _amount.Focus(); } };
        var msg = Btn("Message (encrypt)…"); msg.Click += (_, _) => { if (Guard() && _contactsGrid.SelectedItem != null) EncryptMessageDialog("@" + PropOf(_contactsGrid.SelectedItem, "Handle")); };
        var copyKey = Btn("Copy identity key"); copyKey.Click += (_, _) => { if (_contactsGrid.SelectedItem != null) CopyToClipboard(PropOf(_contactsGrid.SelectedItem, "IdentityPub"), "Identity key copied."); };
        var del = Btn("Delete"); del.Click += (_, _) => { if (_contactsGrid.SelectedItem != null) { var h = PropOf(_contactsGrid.SelectedItem, "Handle"); _w.Contacts.RemoveAll(c => c.Handle == h); Save(); Render(); } };
        ops.Children.Add(pay); ops.Children.Add(msg); ops.Children.Add(copyKey); ops.Children.Add(del);
        sp.Children.Add(ops);
        return Scroll(sp);
    }

    // ---- VAULTS: 2-of-2 multisig with a contact, WITH mandatory unilateral nLockTime recovery ----
    private UIElement BuildVaultsTab()
    {
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(H("Vaults — 2-of-2 multisig (with unilateral recovery)"));
        sp.Children.Add(new TextBlock { Text = "A shared vault both you and a contact must sign to spend — BUT you can always recover your funds alone after the recovery height, so funds can never be locked if the cosigner disappears.", Foreground = SubInk, TextWrapping = TextWrapping.Wrap, MaxWidth = 660, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) });
        _vaultsGrid.Columns.Clear();
        _vaultsGrid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new System.Windows.Data.Binding("Name"), Width = 130 });
        _vaultsGrid.Columns.Add(new DataGridTextColumn { Header = "Address", Binding = new System.Windows.Data.Binding("Address"), Width = 340 });
        _vaultsGrid.Columns.Add(new DataGridTextColumn { Header = "Funded (sat)", Binding = new System.Windows.Data.Binding("Funded"), Width = 120 });
        _vaultsGrid.Columns.Add(new DataGridTextColumn { Header = "Recover at height", Binding = new System.Windows.Data.Binding("LockHeight"), Width = 130 });
        _vaultsGrid.Height = 300;
        sp.Children.Add(_vaultsGrid);
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var create = Btn("Create a vault…"); create.Click += (_, _) => { if (Guard()) CreateVault(); };
        var fund = Btn("Fund selected…"); fund.Click += (_, _) => { if (Guard()) FundVault(); };
        var copyAddr = Btn("Copy address"); copyAddr.Click += (_, _) => { if (_vaultsGrid.SelectedItem != null) CopyToClipboard(PropOf(_vaultsGrid.SelectedItem, "Address"), "Vault address copied."); };
        var release = Btn("Cooperative release…"); release.Click += (_, _) => { if (Guard()) ReleaseVault(); };
        var recover = Btn("Recover (after timeout)…"); recover.Click += (_, _) => { if (Guard()) RecoverVault(); };
        row.Children.Add(create); row.Children.Add(fund); row.Children.Add(copyAddr); row.Children.Add(release); row.Children.Add(recover);
        sp.Children.Add(row);
        return Scroll(sp);
    }

    private Vault? SelectedVault() => _vaultsGrid.SelectedItem is { } it ? _w.Vaults.FirstOrDefault(v => v.Name == PropOf(it, "Name")) : null;

    private long RecoveryHeight()
    {
        var node = _node();
        long tip = node?.BestHeight ?? 0;
        if (tip <= 0) { var st = _store(); tip = st?.Count ?? 0; }
        return (tip > 0 ? tip : 800_000) + 4320;   // ~30 days of blocks after the current tip
    }

    private void CreateVault()
    {
        var name = new TextBox { Width = 200 }; ThemeOne(name);
        var cosg = new TextBox { Width = 520, FontFamily = new FontFamily("Consolas") }; ThemeOne(cosg);
        var ok = new Button { Content = "Create", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = "Vault name:", Foreground = Ink }); sp.Children.Add(name);
        sp.Children.Add(new TextBlock { Text = "Cosigner (@handle or identity pubkey hex):", Foreground = Ink, Margin = new Thickness(0, 8, 0, 0) }); sp.Children.Add(cosg);
        sp.Children.Add(ok);
        var win = new Window { Title = "Create a 2-of-2 vault", Width = 580, Height = 230, Owner = Window.GetWindow(this), Background = WinBg, Content = new ScrollViewer { Content = sp } };
        ok.Click += (_, _) =>
        {
            try
            {
                var raw = cosg.Text.Trim();
                if (raw.StartsWith("@")) { var c = _w.Contacts.FirstOrDefault(x => string.Equals(x.Handle, raw[1..], StringComparison.OrdinalIgnoreCase)); if (c == null) { MessageBox.Show("Unknown contact."); return; } raw = c.IdentityPub; }
                var cosignerPub = Convert.FromHexString(raw);
                if (!Secp256k1.IsValidPoint(cosignerPub)) { MessageBox.Show("Bad cosigner key."); return; }
                uint idx = (uint)_w.Vaults.Count;
                var myPub = WalletKeys.Account(_seed, 5, idx).Pub;
                // canonical ordering so BOTH parties derive the same redeem/address
                var (a, b) = string.CompareOrdinal(Convert.ToHexString(myPub), Convert.ToHexString(cosignerPub)) < 0 ? (myPub, cosignerPub) : (cosignerPub, myPub);
                long lockH = RecoveryHeight();
                var redeem = Chain.MultisigVaultRedeem(a, b, myPub, lockH);   // recovery to ME
                var addr = Base58.CheckEncode(Prefix(_net().ScriptVersion, Chain.ScriptHash160(redeem)));
                _w.Vaults.Add(new Vault { Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : $"vault-{idx}", MyKeyIndex = idx, CosignerPub = Convert.ToHexString(cosignerPub).ToLowerInvariant(), LockHeight = lockH, RedeemHex = Convert.ToHexString(redeem).ToLowerInvariant(), Address = addr });
                Save(); Render();
                CopyToClipboard(addr, "Vault created — address copied. Both of you fund this address.");
                win.Close();
            }
            catch (Exception ex) { MessageBox.Show("Could not create vault: " + ex.Message); }
        };
        win.ShowDialog();
    }

    private void FundVault()
    {
        var v = SelectedVault(); if (v == null) { _status.Text = "Select a vault."; return; }
        var amt = new TextBox { Width = 160, Text = "10000" }; ThemeOne(amt);
        var ok = new Button { Content = "Fund", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = $"Fund {v.Name} ({v.Address}) with (sat):", Foreground = Ink }); sp.Children.Add(amt); sp.Children.Add(ok);
        var win = new Window { Title = "Fund vault", Width = 480, Height = 180, Owner = Window.GetWindow(this), Background = WinBg, Content = new ScrollViewer { Content = sp } };
        ok.Click += (_, _) =>
        {
            try
            {
                if (!long.TryParse(amt.Text.Trim(), out var value) || value <= 0) { MessageBox.Show("Bad amount."); return; }
                var node = _node(); if (node == null || node.PeerCount == 0) { MessageBox.Show("No BSV peers."); return; }
                var redeem = Convert.FromHexString(v.RedeemHex);
                var (raw, status) = FundTx(Chain.P2shLock(redeem), value, 600);
                if (raw == null) { MessageBox.Show(status); return; }
                node.Broadcast(raw);
                var tx = Chain.Deserialize(raw);
                v.FundTxid = Chain.Txid(tx); v.FundVout = 0; v.FundValue = value; v.Funded = true;
                Save(); Render();
                _status.Text = $"Funded {v.Name} with {value:N0} sat (tx {v.FundTxid[..12]}…).";
                win.Close();
            }
            catch (Exception ex) { MessageBox.Show("Fund failed: " + ex.Message); }
        };
        win.ShowDialog();
    }

    /// <summary>Cooperative 2-of-2 release: produce your partial signature for the cosigner, or combine theirs and
    /// broadcast. Both sides build the SAME tx (same destination + fee carried in the partial) so the sigs match.</summary>
    private void ReleaseVault()
    {
        var v = SelectedVault(); if (v == null || !v.Funded) { _status.Text = "Select a funded vault."; return; }
        var dest = new TextBox { Width = 360, FontFamily = new FontFamily("Consolas") }; ThemeOne(dest);
        var fee = new TextBox { Width = 90, Text = "600" }; ThemeOne(fee);
        var partial = new TextBox { Width = 520, Height = 60, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas") }; ThemeOne(partial);
        var outp = new TextBox { Width = 520, Height = 60, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), Background = FieldBg, Foreground = Accent, BorderBrush = Line, BorderThickness = new Thickness(1) };
        var go = new Button { Content = "Produce my part / Combine & broadcast", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = "Pay the vault to (address):", Foreground = Ink }); sp.Children.Add(dest);
        sp.Children.Add(new TextBlock { Text = "Fee (sat):", Foreground = Ink, Margin = new Thickness(0, 6, 0, 0) }); sp.Children.Add(fee);
        sp.Children.Add(new TextBlock { Text = "Cosigner's partial (leave EMPTY to produce yours first):", Foreground = Ink, Margin = new Thickness(0, 6, 0, 0) }); sp.Children.Add(partial);
        sp.Children.Add(go);
        sp.Children.Add(new TextBlock { Text = "Your partial — send it to the cosigner:", Foreground = SubInk, Margin = new Thickness(0, 6, 0, 0) }); sp.Children.Add(outp);
        var win = new Window { Title = $"Cooperative release — {v.Name}", Width = 580, Height = 420, Owner = Window.GetWindow(this), Background = WinBg, Content = new ScrollViewer { Content = sp } };
        go.Click += (_, _) =>
        {
            try
            {
                var redeem = Convert.FromHexString(v.RedeemHex);
                var myPriv = WalletKeys.Account(_seed, 5, v.MyKeyIndex).Priv;
                var myPub = WalletKeys.Account(_seed, 5, v.MyKeyIndex).Pub;
                var coPub = Convert.FromHexString(v.CosignerPub);
                var (a, _) = string.CompareOrdinal(Convert.ToHexString(myPub), Convert.ToHexString(coPub)) < 0 ? (myPub, coPub) : (coPub, myPub);
                bool iAmA = myPub.AsSpan().SequenceEqual(a);

                string destAddr; long feeVal;
                byte[]? coSig = null;
                var p = partial.Text.Trim();
                if (p.Length > 0)
                {
                    var parts = p.Split('|');               // vaultpartial|dest|fee|sigHex|pubHex
                    if (parts.Length != 5 || parts[0] != "vaultpartial") { MessageBox.Show("Bad partial."); return; }
                    destAddr = parts[1]; feeVal = long.Parse(parts[2]); coSig = Convert.FromHexString(parts[3]);
                }
                else { destAddr = dest.Text.Trim(); if (!long.TryParse(fee.Text.Trim(), out feeVal)) { MessageBox.Show("Bad fee."); return; } }

                var payload = Base58.CheckDecode(destAddr);
                var lockScript = payload[0] == _net().ScriptVersion ? Chain.P2shLockFromHash(payload[1..]) : Chain.P2pkhLock(payload[1..]);
                var tx = new Chain.Tx(2, new() { new(v.FundTxid, v.FundVout, Array.Empty<byte>(), 0xffffffff) }, new() { new(v.FundValue - feeVal, lockScript) }, 0);
                var mySig = Chain.VaultSign(tx, 0, redeem, v.FundValue, myPriv);

                if (coSig == null) { outp.Text = $"vaultpartial|{destAddr}|{feeVal}|{Convert.ToHexString(mySig).ToLowerInvariant()}|{Convert.ToHexString(myPub).ToLowerInvariant()}"; CopyToClipboard(outp.Text, "Your partial copied — send it to the cosigner, then paste theirs here."); return; }

                var sigA = iAmA ? mySig : coSig; var sigB = iAmA ? coSig : mySig;
                var signed = tx with { Ins = new() { tx.Ins[0] with { ScriptSig = Chain.VaultCoopScriptSig(sigA, sigB, redeem) } } };
                var node = _node(); if (node == null || node.PeerCount == 0) { MessageBox.Show("No BSV peers."); return; }
                node.Broadcast(Chain.Serialize(signed));
                v.Funded = false; _w.Sends.Add(new SendRec { Txid = Chain.Txid(signed), Amount = v.FundValue - feeVal, Fee = feeVal, To = $"vault {v.Name} → {destAddr}", Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), RawHex = Convert.ToHexString(Chain.Serialize(signed)).ToLowerInvariant() });
                Save(); Render(); _status.Text = $"Vault released — tx {Chain.Txid(signed)[..12]}…"; win.Close();
            }
            catch (Exception ex) { MessageBox.Show("Release failed: " + ex.Message); }
        };
        win.ShowDialog();
    }

    /// <summary>Unilateral recovery (the always-available safety net): after the recovery height, spend the vault
    /// back to yourself with your single signature via the redeem's ELSE branch. No cosigner needed.</summary>
    private void RecoverVault()
    {
        var v = SelectedVault(); if (v == null || !v.Funded) { _status.Text = "Select a funded vault."; return; }
        try
        {
            var node = _node(); if (node == null || node.PeerCount == 0) { _status.Text = "No BSV peers."; return; }
            var redeem = Convert.FromHexString(v.RedeemHex);
            var myPriv = WalletKeys.Account(_seed, 5, v.MyKeyIndex).Priv;
            long fee = 600;
            var toMe = Chain.P2pkhLockForPub(WalletKeys.Account(_seed, 0, (uint)_w.RecvIndex).Pub);
            // CLTV needs a non-final input sequence and nLockTime >= the vault's recovery height
            var tx = new Chain.Tx(2, new() { new(v.FundTxid, v.FundVout, Array.Empty<byte>(), 0xfffffffe) }, new() { new(v.FundValue - fee, toMe) }, (uint)v.LockHeight);
            var sig = Chain.VaultSign(tx, 0, redeem, v.FundValue, myPriv);
            var signed = tx with { Ins = new() { tx.Ins[0] with { ScriptSig = Chain.VaultRecoveryScriptSig(sig, redeem) } } };
            node.Broadcast(Chain.Serialize(signed));
            v.Funded = false; Save(); Render();
            _status.Text = $"Recovery broadcast (valid once height ≥ {v.LockHeight}). If the network rejects it as premature, retry after that height. tx {Chain.Txid(signed)[..12]}…";
            Notify($"Vault {v.Name}: unilateral recovery broadcast to {v.FundValue - fee:N0} sat back to you.");
        }
        catch (Exception ex) { _status.Text = "Recover failed: " + ex.Message; }
    }

    // ---- NFTs: 1-sat on-chain card/token outputs the wallet holds ----
    private UIElement BuildNftTab()
    {
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(H("NFTs / tokens"));
        sp.Children.Add(_cardsLabel);
        sp.Children.Add(_cards);
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var transfer = Btn("Transfer an NFT to an identity…"); transfer.Click += (_, _) => { if (Guard()) TransferNft(); };
        var import = Btn("Import an NFT (sealed blob)…"); import.Click += (_, _) => { if (Guard()) ImportNft(); };
        row.Children.Add(transfer); row.Children.Add(import);
        sp.Children.Add(row);
        return Scroll(sp);
    }

    private void TransferNft()
    {
        var owned = _vault.Owned();
        if (owned.Count == 0) { _status.Text = "You hold no NFTs to transfer."; return; }
        var pick = new ComboBox { Width = 200 }; foreach (var (c, _) in owned) pick.Items.Add(c.ToString()); pick.SelectedIndex = 0;
        pick.Background = FieldBg; pick.Foreground = Ink;
        var to = new TextBox { Width = 520, FontFamily = new FontFamily("Consolas") }; ThemeOne(to);
        var outp = new TextBox { Width = 520, Height = 70, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), Background = FieldBg, Foreground = Accent, BorderBrush = Line, BorderThickness = new Thickness(1) };
        var go = new Button { Content = "Transfer", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = "NFT to transfer:", Foreground = Ink }); sp.Children.Add(pick);
        sp.Children.Add(new TextBlock { Text = "Recipient identity (@handle or identity pubkey hex):", Foreground = Ink, Margin = new Thickness(0, 8, 0, 0) }); sp.Children.Add(to);
        sp.Children.Add(go);
        sp.Children.Add(new TextBlock { Text = "Transferred NFT blob — give it to the recipient to import:", Foreground = SubInk, Margin = new Thickness(0, 8, 0, 0) }); sp.Children.Add(outp);
        var win = new Window { Title = "Transfer NFT", Width = 580, Height = 360, Owner = Window.GetWindow(this), Background = WinBg, Content = new ScrollViewer { Content = sp } };
        go.Click += (_, _) =>
        {
            try
            {
                var raw = to.Text.Trim();
                if (raw.StartsWith("@")) { var c = _w.Contacts.FirstOrDefault(x => string.Equals(x.Handle, raw[1..], StringComparison.OrdinalIgnoreCase)); if (c == null) { MessageBox.Show("Unknown contact."); return; } raw = c.IdentityPub; }
                var toPub = Convert.FromHexString(raw);
                if (!Secp256k1.IsValidPoint(toPub)) { MessageBox.Show("Bad recipient key."); return; }
                var (_, sealedHex) = owned[pick.SelectedIndex];
                var transferred = CardNft.Transfer(sealedHex, _identityPriv, toPub);   // re-seal to the recipient
                _vault.Remove(sealedHex);                                                // ownership leaves our vault
                RefreshCards();
                outp.Text = transferred;
                CopyToClipboard(transferred, "Transferred NFT blob copied — give it to the recipient.");
            }
            catch (Exception ex) { MessageBox.Show("Transfer failed: " + ex.Message); }
        };
        win.ShowDialog();
    }

    private void ImportNft()
    {
        var box = new TextBox { Width = 520, Height = 70, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas") }; ThemeOne(box);
        var go = new Button { Content = "Import", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = "Paste a sealed NFT blob addressed to your identity:", Foreground = Ink }); sp.Children.Add(box); sp.Children.Add(go);
        var win = new Window { Title = "Import NFT", Width = 580, Height = 220, Owner = Window.GetWindow(this), Background = WinBg, Content = new ScrollViewer { Content = sp } };
        go.Click += (_, _) =>
        {
            try
            {
                var blob = box.Text.Trim();
                if (!CardNft.CanOpen(blob, _identityPriv)) { MessageBox.Show("That NFT is not sealed to your identity (you can't open it)."); return; }
                _vault.AddSealed(blob); RefreshCards();
                _status.Text = "NFT imported."; win.Close();
            }
            catch (Exception ex) { MessageBox.Show("Import failed: " + ex.Message); }
        };
        win.ShowDialog();
    }

    // ---- IDENTITY: your Base ID key (login/handle), master public key, what it is ----
    private UIElement BuildIdentityTab()
    {
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(H("Your identity (Base ID key)"));
        sp.Children.Add(new TextBlock { Text = "Your Base ID key is your identity — like an NFT you own. It is NEVER used as an address; it only derives one-time ECDH sub-keys (Type-42), all linked in an HMAC hash chain. Give others your handle or identity public key so they can pay and message you.", Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left });
        sp.Children.Add(Lbl("Handle (your name, e.g. bob)"));
        var hrow = new WrapPanel();
        hrow.Children.Add(_idHandle);
        var setH = Btn("Set handle"); setH.Click += (_, _) => { _w.Handle = _idHandle.Text.Trim().TrimStart('@'); Save(); Render(); _status.Text = $"Handle set to @{_w.Handle}"; };
        hrow.Children.Add(setH);
        sp.Children.Add(hrow);
        sp.Children.Add(Lbl("Identity public key (share this)"));
        sp.Children.Add(_idPub);
        var idBtns = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var copyId = Btn("Copy identity key"); copyId.Click += (_, _) => { if (Guard()) CopyToClipboard(Convert.ToHexString(_identityPub).ToLowerInvariant(), "Identity key copied."); };
        idBtns.Children.Add(copyId);
        sp.Children.Add(idBtns);
        sp.Children.Add(Lbl("Identity QR (others scan this to add you as a contact)"));
        try { sp.Children.Add(new Border { Background = Brushes.White, Padding = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Left, Child = new System.Windows.Controls.Image { Source = RenderQr("bsvid:" + Convert.ToHexString(_identityPub).ToLowerInvariant()), Stretch = Stretch.None } }); } catch { }
        return Scroll(sp);
    }

    // ---- TOOLS: seed, password/encrypt, sign/verify, load+broadcast tx, master pubkey ----
    private UIElement BuildToolsTab()
    {
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(H("Console"));
        sp.Children.Add(Lbl("Type a command (read-only). help · getbalance · getheight · peers · getaddress · listunspent · listaddresses · getidentity · listcontacts · gettx <txid>"));
        var conOut = new ListBox { ItemsSource = _consoleLog, Height = 160, Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)), Foreground = Ink, BorderBrush = Line, BorderThickness = new Thickness(1), FontFamily = new FontFamily("Consolas") };
        sp.Children.Add(conOut);
        var conRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 12) };
        ThemeOne(_consoleInput); _consoleInput.Width = 460;
        void Run() { var c = _consoleInput.Text.Trim(); if (c.Length == 0) return; _consoleLog.Insert(0, "> " + c); _consoleInput.Clear(); ConsoleRun(c); }
        _consoleInput.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Run(); };
        var runBtn = Btn("Run"); runBtn.Margin = new Thickness(8, 0, 0, 0); runBtn.Click += (_, _) => Run();
        conRow.Children.Add(_consoleInput); conRow.Children.Add(runBtn);
        sp.Children.Add(conRow);

        sp.Children.Add(H("Wallet security"));
        sp.Children.Add(Lbl("The keys that control your bitcoin are kept encrypted and password-protected at rest."));
        var sec = new WrapPanel();
        var pwBtn = Btn("Set / change password (encrypt keys)…"); pwBtn.Click += (_, _) => SetPassword();
        var unlockBtn = Btn("Unlock…"); unlockBtn.Click += (_, _) => { Unlock(); Render(); };
        var showPhrase = Btn("Back up — show wallet seed"); showPhrase.Click += (_, _) => { if (Guard()) MessageBox.Show(WalletKeys.SeedToBackup(_seed), "Write this seed down — it recovers your whole wallet", MessageBoxButton.OK, MessageBoxImage.Warning); };
        var restore = Btn("Restore from seed…"); restore.Click += (_, _) => Restore();
        sec.Children.Add(pwBtn); sec.Children.Add(unlockBtn); sec.Children.Add(showPhrase); sec.Children.Add(restore);
        sp.Children.Add(sec);

        sp.Children.Add(Lbl("Messages"));
        var msg = new WrapPanel();
        var sign = Btn("Sign message…"); sign.Click += (_, _) => { if (Guard()) SignMessageDialog(); };
        var verify = Btn("Verify message…"); verify.Click += (_, _) => VerifyMessageDialog();
        var enc = Btn("Encrypt message to identity…"); enc.Click += (_, _) => { if (Guard()) EncryptMessageDialog(); };
        var dec = Btn("Decrypt message…"); dec.Click += (_, _) => { if (Guard()) DecryptMessageDialog(); };
        msg.Children.Add(sign); msg.Children.Add(verify); msg.Children.Add(enc); msg.Children.Add(dec);
        sp.Children.Add(msg);

        sp.Children.Add(Lbl("Import"));
        var imp = new WrapPanel();
        var sweep = Btn("Sweep a private key (WIF)…"); sweep.Click += (_, _) => { if (Guard()) SweepWif(); };
        imp.Children.Add(sweep);
        sp.Children.Add(imp);

        sp.Children.Add(Lbl("Transactions"));
        var txs = new WrapPanel();
        var load = Btn("Load / broadcast a raw transaction (text)…"); load.Click += (_, _) => { if (Guard()) LoadBroadcastTx(); };
        var loadFile = Btn("Load transaction from file…"); loadFile.Click += (_, _) => { if (Guard()) LoadTxFromFile(); };
        var mpk = Btn("Show master public key"); mpk.Click += (_, _) => { if (Guard()) MessageBox.Show(Convert.ToHexString(_identityPub).ToLowerInvariant(), "Identity / master public key"); };
        txs.Children.Add(load); txs.Children.Add(loadFile); txs.Children.Add(mpk);
        sp.Children.Add(txs);

        sp.Children.Add(Lbl("Export / import"));
        var exp = new WrapPanel();
        var expLabels = Btn("Export labels (JSON)…"); expLabels.Click += (_, _) => ExportLabels();
        var impLabels = Btn("Import labels (JSON)…"); impLabels.Click += (_, _) => { if (Guard()) ImportLabels(); };
        var expAddrs = Btn("Export addresses (CSV)…"); expAddrs.Click += (_, _) => { if (Guard()) ExportAddresses(); };
        exp.Children.Add(expLabels); exp.Children.Add(impLabels); exp.Children.Add(expAddrs);
        sp.Children.Add(exp);
        return Scroll(sp);
    }

    // ---- Console: precise read-only commands (no money moves; that's the Send tab) ----
    private readonly System.Collections.ObjectModel.ObservableCollection<string> _consoleLog = new();
    private readonly TextBox _consoleInput = new();
    private void C(string m) => _consoleLog.Insert(0, "  " + m);
    private void ConsoleRun(string cmd)
    {
        var parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var op = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        try
        {
            switch (op)
            {
                case "help": C("getbalance getheight peers getaddress listunspent listaddresses getidentity listcontacts gettx <txid>"); break;
                case "getbalance": C($"confirmed={Balance} pending={Pending} (sat)"); break;
                case "getheight": { var st = _store(); C($"validated headers={(st?.Count ?? 0)}"); break; }
                case "peers": { var n = _node(); C($"spv_peers={(n?.PeerCount ?? 0)} best_height={(n?.BestHeight ?? 0)} network={_net().Network}"); break; }
                case "getaddress": if (Guard()) C(ReceiveAddress()); break;
                case "getidentity": C(Convert.ToHexString(_identityPub).ToLowerInvariant() + (string.IsNullOrEmpty(_w.Handle) ? "" : $" (@{_w.Handle})")); break;
                case "listcontacts": C(_w.Contacts.Count == 0 ? "(none)" : string.Join(", ", _w.Contacts.Select(c => $"@{c.Handle}={c.IdentityPub[..12]}…"))); break;
                case "listunspent": if (Guard()) foreach (var u in _w.Utxos.Where(u => !u.Spent)) C($"{u.Txid[..12]}…:{u.Vout} {u.Value} sat {(u.Confirmed ? "conf" : "pend")}{(u.Frozen ? " frozen" : "")}"); break;
                case "listaddresses": if (Guard()) for (uint i = 0; i <= (uint)_w.RecvIndex; i++) C($"receive/{i} {AddressForKey(0, i)}"); break;
                case "gettx": if (parts.Length > 1) TxDetails(parts[1]); else C("usage: gettx <txid>"); break;
                default: C("unknown command — type 'help'"); break;
            }
        }
        catch (Exception ex) { C("error: " + ex.Message); }
    }

    private static string PropOf(object o, string p) => o.GetType().GetProperty(p)!.GetValue(o)?.ToString() ?? "";
    private void SelectTab(string header) { foreach (var it in _tabs.Items) if (it is TabItem ti && (string)ti.Header == header) { _tabs.SelectedItem = ti; return; } }

    // ---- real on-chain funding & spending ----

    // Balance counts ONLY real, SPV-confirmed, unspent coins. Unconfirmed coins (detected-but-not-yet-mined
    // incoming, or our own in-flight change) are shown separately as PENDING and never counted as spendable.
    private long Balance => _w.Utxos.Where(u => !u.Spent && u.Confirmed).Sum(u => u.Value);
    private long Pending => _w.Utxos.Where(u => !u.Spent && !u.Confirmed).Sum(u => u.Value);

    /// <summary>
    /// Detect an INCOMING payment: scan a transaction's outputs for any that pay one of our receive keys and,
    /// if new, record it as PENDING (not counted as balance until SPV-confirmed in a block). Returns true if
    /// something new was added. This is how the balance reflects the chain — the node hands us relayed txs.
    /// </summary>
    public bool ConsiderIncoming(Core.Chain.Tx tx)
    {
        if (_locked) return false;
        bool added = false;
        var txid = Core.Chain.Txid(tx);
        for (int v = 0; v < tx.Outs.Count; v++)
        {
            for (uint i = 0; i <= (uint)_w.RecvIndex + 50; i++)
            {
                if (tx.Outs[v].Script.AsSpan().SequenceEqual(Core.Chain.P2pkhLockForPub(WalletKeys.Account(_seed, 0, i).Pub)))
                {
                    if (!_w.Utxos.Any(u => u.Txid == txid && u.Vout == (uint)v))
                    { _w.Utxos.Add(new UtxoRec { Txid = txid, Vout = (uint)v, Value = tx.Outs[v].Value, KeyChain = 0, KeyIndex = i, Confirmed = false }); added = true; }
                    break;
                }
            }
        }
        if (added) { AppendTx("incoming (pending)", 0, "awaiting confirmation"); Notify("Incoming payment detected (pending confirmation)."); Save(); Render(); }
        return added;
    }

    /// <summary>
    /// Confirm pending coins by SPV: if a pending UTXO's transaction is in <paramref name="parsed"/> and that
    /// block is in a header chain we validated ourselves, verify the merkle proof and mark the coin CONFIRMED
    /// (now counted as real balance). Only confirmed-on-chain coins ever become spendable.
    /// </summary>
    public void ConfirmFromBlock(BsvBlock.Parsed parsed, HeadersChain chain)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => ConfirmFromBlock(parsed, chain))); return; }
        if (chain.Get(parsed.Header.HashHex()) == null) return;                 // block not in OUR validated chain
        if (!MerkleProof.Root(parsed.Txids).AsSpan().SequenceEqual(parsed.Header.MerkleRoot)) return; // block self-consistent
        var index = new Dictionary<string, int>();
        for (int i = 0; i < parsed.Txs.Count; i++) index[Core.Chain.Txid(parsed.Txs[i])] = i;
        bool changed = false;
        foreach (var u in _w.Utxos.Where(u => !u.Confirmed && !u.Spent))
            if (index.TryGetValue(u.Txid, out int idx) && MerkleProof.Verify(parsed.Txids[idx], idx, MerkleProof.Branch(parsed.Txids, idx), parsed.Header.MerkleRoot))
            { u.Confirmed = true; changed = true; AppendTx("confirmed", u.Value, $"{u.Txid[..12]}…:{u.Vout} mined"); }
        if (changed) { Save(); Render(); }
    }

    /// <summary>Txids of coins still awaiting confirmation (so the node can fetch their blocks).</summary>
    public IReadOnlyList<string> PendingTxids() => _w.Utxos.Where(u => !u.Confirmed && !u.Spent).Select(u => u.Txid).Distinct().ToList();

    /// <summary>
    /// The address material to load into the node's SPV bloom filter: for every receive and change key in our
    /// scan range, both the 20-byte hash160 (which a P2PKH output pushes — this is what matches a payment TO us)
    /// and the 33-byte public key. Peers then relay back exactly our own transactions, with no server scanning
    /// the chain for us. A wider scan range = a few more keys watched; correctness is unaffected either way.
    /// </summary>
    public IReadOnlyList<byte[]> FilterElements()
    {
        var elems = new List<byte[]>();
        uint gap = (uint)_w.RecvIndex + 50;
        for (uint chain = 0; chain <= 2; chain++)            // 0 = receive, 1 = change, 2 = seat/escrow keys
            for (uint i = 0; i <= gap; i++)
            {
                var pub = WalletKeys.Account(_seed, chain, i).Pub;
                elems.Add(Hashes.Hash160(pub));
                elems.Add(pub);
            }
        foreach (var hex in _w.SweptKeys)                    // external keys being swept in
            try { var pub = Secp256k1.PublicKeyCompressed(Convert.FromHexString(hex)); elems.Add(Hashes.Hash160(pub)); elems.Add(pub); } catch { }
        foreach (var v in _w.Vaults)                         // watch each 2-of-2 vault's P2SH script hash
            try { elems.Add(Chain.ScriptHash160(Convert.FromHexString(v.RedeemHex))); } catch { }
        return elems;
    }

    /// <summary>
    /// Credit a payment the node discovered for us via SPV: the matching transaction plus the raw merkleblock
    /// proving it was mined. We re-verify the proof against the headers WE validated and that the output really
    /// pays one of our keys (<see cref="SpvFunding.VerifyFromMerkleBlock"/>); only then is a CONFIRMED UTXO
    /// added. This is the automatic "you sent funds and it shows up" path — no envelope to paste, no server.
    /// </summary>
    public bool ConfirmIncoming(Core.Chain.Tx tx, byte[] merkleBlockPayload)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => ConfirmIncoming(tx, merkleBlockPayload))); return false; }
        if (_locked) return false;
        var store = _store(); if (store == null || store.Count == 0) return false;     // need our own validated headers
        var (chain, _) = store.BuildChain();
        bool changed = false;
        uint gap = (uint)_w.RecvIndex + 50;
        for (uint v = 0; v < (uint)tx.Outs.Count; v++)
            for (uint c = 0; c <= 2 && !changed; c++)
                for (uint i = 0; i <= gap; i++)
                {
                    var pub = WalletKeys.Account(_seed, c, i).Pub;
                    var utxo = SpvFunding.VerifyFromMerkleBlock(tx, v, merkleBlockPayload, chain, pub, c, i);
                    if (utxo == null) continue;
                    if (!_w.Utxos.Any(u => u.Txid == utxo.Txid && u.Vout == utxo.Vout))
                    {
                        _w.Utxos.Add(new UtxoRec { Txid = utxo.Txid, Vout = utxo.Vout, Value = utxo.Value, KeyChain = c, KeyIndex = i, Confirmed = true });
                        AppendTx("received", utxo.Value, $"SPV-confirmed {utxo.Txid[..12]}…:{utxo.Vout}");
                        Notify($"Received {utxo.Value:N0} sat (SPV-confirmed).");
                        changed = true;
                    }
                    else // already known (was pending) → mark it confirmed now that we hold the proof
                        foreach (var u in _w.Utxos.Where(u => u.Txid == utxo.Txid && u.Vout == utxo.Vout && !u.Confirmed))
                        { u.Confirmed = true; changed = true; AppendTx("confirmed", u.Value, $"{u.Txid[..12]}…:{u.Vout} mined"); }
                    break;
                }
        // sweep: if an output of this proven tx pays an external key we're sweeping, move it into THIS wallet now
        SweepFromProvenTx(tx);
        if (changed) { Save(); Render(); }
        return changed;
    }

    /// <summary>
    /// If a confirmed (proven) transaction pays one of the external keys being swept, build and broadcast a
    /// transaction that spends that coin to a fresh receive address of THIS wallet, signed with the external
    /// key. The swept funds then arrive as a normal received coin. This is a real end-to-end sweep, no stub.
    /// </summary>
    private void SweepFromProvenTx(Core.Chain.Tx tx)
    {
        if (_w.SweptKeys.Count == 0) return;
        var node = _node(); if (node == null || node.PeerCount == 0) return;
        var txid = Core.Chain.Txid(tx);
        foreach (var hex in _w.SweptKeys.ToList())
        {
            byte[] priv, pub;
            try { priv = Convert.FromHexString(hex); pub = Secp256k1.PublicKeyCompressed(priv); } catch { continue; }
            var lockScript = Chain.P2pkhLockForPub(pub);
            for (uint v = 0; v < (uint)tx.Outs.Count; v++)
            {
                if (!tx.Outs[(int)v].Script.AsSpan().SequenceEqual(lockScript)) continue;
                long value = tx.Outs[(int)v].Value;
                long fee = Math.Max(1, EstimateFee(1));
                if (value <= fee) continue;
                var toPub = WalletKeys.Account(_seed, 0, (uint)_w.RecvIndex).Pub;     // sweep into our wallet
                var sweepTx = new Chain.Tx(2,
                    new() { new(txid, v, Array.Empty<byte>(), 0xffffffff) },
                    new() { new(value - fee, Chain.P2pkhLockForPub(toPub)) }, 0);
                sweepTx = Chain.SignP2pkhInput(sweepTx, 0, priv, pub, value);          // sign with the EXTERNAL key
                node.Broadcast(Chain.Serialize(sweepTx));
                _w.Sends.Add(new SendRec { Txid = Chain.Txid(sweepTx), Amount = value - fee, Fee = fee, To = "sweep → my wallet", Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm") });
            }
        }
    }

    /// <summary>Set by the host window: triggers an SPV rescan (mempool pull + recent filtered blocks) for our addresses.</summary>
    public Action? RescanRequested;

    /// <summary>
    /// True when the wallet holds enough REAL (SPV-confirmed) coin to seat a hand. STANDALONE SPV — there is
    /// NO "connected to the network" requirement; the client never depends on a node connection.
    /// </summary>
    public bool CanPlayOnChain(long pot) => !_locked && Balance >= pot + OnChainHandReserve;

    /// <summary>
    /// Reserve a single spendable coin to seat a live two-party hand: returns the UTXO, the key that controls
    /// it (the seat key, which also serves as the player's 2-of-2 escrow member), and a fresh change key.
    /// Null if locked or no coin is large enough.
    /// </summary>
    public (OnChainWallet.Utxo Utxo, byte[] Priv, byte[] Pub, byte[] ChangePub)? ReserveSeat(long min)
    {
        if (_locked) return null;
        var u = _w.Utxos.FirstOrDefault(x => !x.Spent && x.Value >= min);
        if (u == null) return null;
        var k = WalletKeys.Account(_seed, u.KeyChain, u.KeyIndex);
        var change = WalletKeys.Account(_seed, 1, 0).Pub;
        return (new OnChainWallet.Utxo(u.Txid, u.Vout, u.Value, u.KeyChain, u.KeyIndex), k.Priv, k.Pub, change);
    }

    /// <summary>Mark a UTXO spent (after its escrow/spend transaction has been broadcast).</summary>
    public void MarkSpent(string txid, uint vout)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => MarkSpent(txid, vout))); return; }
        foreach (var u in _w.Utxos.Where(u => u.Txid == txid && u.Vout == vout)) u.Spent = true;
        Save(); Render();
    }

    /// <summary>
    /// Fund an arbitrary output script (e.g. an encrypted chat message, or any on-chain action) from real
    /// sats, advancing wallet state, and return the signed raw transaction to push IP-to-IP + to miners.
    /// Everything the app sends between machines goes through a real funded Bitcoin transaction like this.
    /// </summary>
    public (byte[]? Raw, string Status) FundTx(byte[] outputScript, long value = 1000, long fee = 500)
    {
        if (_locked) return (null, "🔒 Unlock the wallet first.");
        var w = new OnChainWallet(_seed);
        foreach (var u in _w.Utxos.Where(u => !u.Spent)) w.Add(new OnChainWallet.Utxo(u.Txid, u.Vout, u.Value, u.KeyChain, u.KeyIndex));
        if (w.Balance < value + fee) return (null, $"Insufficient sats: have {w.Balance:N0}, need {value + fee:N0}. Fund your wallet first.");
        try
        {
            var spend = w.SpendAction(outputScript, value, fee);
            _w.Utxos = w.Coins.Select(u => new UtxoRec { Txid = u.Txid, Vout = u.Vout, Value = u.Value, KeyChain = u.KeyChain, KeyIndex = u.KeyIndex }).ToList();
            AppendTx("message", -(value + fee), $"on-chain tx {Chain.Txid(spend.Tx)[..12]}…");
            Save(); Render();
            return (Chain.Serialize(spend.Tx), "");
        }
        catch (Exception ex) { return (null, ex.Message); }
    }
    private const long OnChainHandReserve = 60_000; // headroom for the ~20 per-step values + fees of one hand

    // (REMOVED) PlayOnChainHand — the old single-player LOCAL-RNG-DECK settle path. Deleted so there is ZERO
    // non-mental-poker hand path: every hand (human↔human and human↔bot) runs the genuine dealerless two-party
    // mental-poker protocol via LiveHand/LiveDeal (commutative encryption + mutually-verified shuffle/remask
    // proofs + joint unmask). A local deck never decides a hand.

    /// <summary>
    /// Mint the given cards as REAL on-chain 1-sat encrypted NFTs sealed to this wallet's identity: each card is
    /// ECDH-sealed (CardNft.SealToPub), funded as a 1-sat NftLock output from real sats, broadcast, and stored
    /// in the vault. No free/fake NFTs — if there aren't enough sats it stops and reports honestly. Returns a
    /// status string. This is how every card dealt to you becomes an on-chain encrypted NFT in your wallet.
    /// </summary>
    public string MintCardNftsOnChain(IReadOnlyList<int> cardIndexes)
    {
        if (_locked) return "🔒 wallet locked — cannot mint card NFTs.";
        var node = _node();
        if (node == null || node.PeerCount == 0) return "no BSV peers — card NFTs not minted on-chain.";
        int minted = 0;
        foreach (var ci in cardIndexes)
        {
            try
            {
                var blind = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
                var sealedHex = CardNft.SealToPub(ci, blind, _identityPub);     // encrypted to my identity
                var script = CardNft.NftLock(sealedHex, _identityPub);          // 1-sat on-chain NFT output
                var (raw, status) = FundTx(script, 1, 500);
                if (raw == null) return $"minted {minted} card NFT(s); stopped: {status}";
                node.Broadcast(raw);
                _vault.AddSealed(sealedHex);
                _w.NftMints[sealedHex] = Chain.Txid(Chain.Deserialize(raw));    // on-chain provenance
                Save();
                minted++;
            }
            catch (Exception ex) { return $"minted {minted} card NFT(s); error: {ex.Message}"; }
        }
        RefreshCards();
        Notify($"Minted {minted} card NFT(s) on-chain (encrypted to your identity).");
        return $"minted {minted} card NFT(s) on-chain";
    }

    /// <summary>The receive key for the given receive index (chain 0).</summary>
    private WalletKeys RecvKey(uint index) => WalletKeys.Account(_seed, 0, index);

    private string ReceiveAddress()
    {
        var pub = RecvKey((uint)_w.RecvIndex).Pub;
        var payload = new byte[21]; payload[0] = _net().AddressVersion; Hashes.Hash160(pub).CopyTo(payload, 1);
        return Base58.CheckEncode(payload);
    }

    /// <summary>
    /// Import a payer's SPV funding envelope: the funding transaction (hex) + a merkleblock (hex) proving it
    /// was mined + the output index that pays us. The proof is verified against the headers this client has
    /// validated itself; only then is a real UTXO added. This is the pure peer-to-peer funding path (no server).
    /// </summary>
    private void ImportFunding()
    {
        var txBox = new TextBox { Width = 520, Height = 70, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas") };
        var mbBox = new TextBox { Width = 520, Height = 70, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas") };
        var voutBox = new TextBox { Width = 80, Text = "0" };
        var go = new Button { Content = "Verify & import", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = "Funding transaction (raw hex):", Foreground = Brushes.Gray }); sp.Children.Add(txBox);
        sp.Children.Add(new TextBlock { Text = "Merkleblock proof (raw hex):", Foreground = Brushes.Gray, Margin = new Thickness(0, 8, 0, 0) }); sp.Children.Add(mbBox);
        sp.Children.Add(new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Children = { new TextBlock { Text = "Output index paying me: ", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center }, voutBox } });
        sp.Children.Add(go);
        var win = new Window { Title = "Import funding (SPV envelope)", Width = 580, Height = 380, Owner = Window.GetWindow(this), Content = new ScrollViewer { Content = sp } };
        go.Click += (_, _) =>
        {
            try
            {
                var store = _store();
                if (store == null || store.Count == 0) { MessageBox.Show("No validated headers yet — wait for the node to sync headers, then retry.", "Cannot verify"); return; }
                var fundTx = Chain.Deserialize(Convert.FromHexString(txBox.Text.Trim()));
                var mb = Convert.FromHexString(mbBox.Text.Trim());
                if (!uint.TryParse(voutBox.Text.Trim(), out var vout)) { MessageBox.Show("Output index must be a number.", "Import"); return; }
                var (chain, _) = store.BuildChain();
                // find which of our receive keys this output pays (scan current indices + a gap)
                OnChainWallet.Utxo? utxo = null; uint maxScan = (uint)_w.RecvIndex + 50;
                for (uint i = 0; i <= maxScan; i++)
                {
                    var pub = RecvKey(i).Pub;
                    utxo = SpvFunding.VerifyFromMerkleBlock(fundTx, vout, mb, chain, pub, 0, i);
                    if (utxo != null) break;
                }
                if (utxo == null) { MessageBox.Show("Proof did not verify against our validated headers, or the output does not pay any of our addresses.", "Import rejected"); return; }
                if (_w.Utxos.Any(u => u.Txid == utxo.Txid && u.Vout == utxo.Vout)) { MessageBox.Show("That UTXO is already in the wallet.", "Already imported"); return; }
                _w.Utxos.Add(new UtxoRec { Txid = utxo.Txid, Vout = utxo.Vout, Value = utxo.Value, KeyChain = utxo.KeyChain, KeyIndex = utxo.KeyIndex, Confirmed = true }); // SPV-verified against our headers
                AppendTx("funded", utxo.Value, $"SPV funding {utxo.Txid[..12]}…:{utxo.Vout}");
                Save(); Render();
                _status.Text = $"Imported {utxo.Value:N0} sat (verified against our own headers).";
                win.Close();
            }
            catch (Exception ex) { MessageBox.Show("Could not import: " + ex.Message, "Import error"); }
        };
        win.ShowDialog();
    }

    /// <summary>
    /// Find a payment by its transaction id (and the hash of the block that confirmed it): fetch that block
    /// from a peer over our own node, locate the transaction, build a merkle proof, and credit any output that
    /// pays one of our keys — verified against the headers we validated ourselves. This is the txid funding
    /// path: you (or the payer) supply the txid + block hash and the wallet does the rest, no server.
    /// </summary>
    private async System.Threading.Tasks.Task FindByTxid()
    {
        var txidBox = new TextBox { Width = 520, FontFamily = new FontFamily("Consolas") };
        var blkBox = new TextBox { Width = 520, FontFamily = new FontFamily("Consolas") };
        var go = new Button { Content = "Fetch & credit", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = "Payment transaction id (txid):", Foreground = Brushes.Gray }); sp.Children.Add(txidBox);
        sp.Children.Add(new TextBlock { Text = "Hash of the block that confirmed it:", Foreground = Brushes.Gray, Margin = new Thickness(0, 8, 0, 0) }); sp.Children.Add(blkBox);
        sp.Children.Add(go);
        var win = new Window { Title = "Find a payment by txid", Width = 580, Height = 240, Owner = Window.GetWindow(this), Content = new ScrollViewer { Content = sp } };
        go.Click += async (_, _) =>
        {
            try
            {
                var node = _node();
                if (node == null || node.PeerCount == 0) { MessageBox.Show("No BSV peers connected yet — wait for peers, then retry.", "No peers"); return; }
                var store = _store();
                if (store == null || store.Count == 0) { MessageBox.Show("No validated headers yet — wait for header sync, then retry.", "Cannot verify"); return; }
                var wantTxid = txidBox.Text.Trim().ToLowerInvariant();
                go.IsEnabled = false;
                var raw = await node.GetBlockAsync(blkBox.Text.Trim());
                go.IsEnabled = true;
                if (raw == null) { MessageBox.Show("That block was not served by any peer — check the block hash.", "Not found"); return; }
                var parsed = BsvBlock.Parse(raw);                                  // validates merkle root vs header
                int idx = parsed.Txs.FindIndex(t => Chain.Txid(t) == wantTxid);
                if (idx < 0) { MessageBox.Show("That txid is not in that block — wrong block hash?", "Not in block"); return; }
                var mb = PartialMerkleTree.BuildMerkleBlock(parsed.Header, parsed.Txids, new HashSet<int> { idx });
                if (ConfirmIncoming(parsed.Txs[idx], mb))
                { _status.Text = "Payment found and credited (SPV-verified)."; win.Close(); }
                else MessageBox.Show("The transaction was found, but no output pays your wallet (or that block isn't in your validated headers yet).", "Nothing to credit");
            }
            catch (Exception ex) { go.IsEnabled = true; MessageBox.Show("Could not find the payment: " + ex.Message, "Error"); }
        };
        win.ShowDialog();
        await System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>
    /// Payer side of the no-server funding handshake: given a funding transaction and the hash of the block
    /// that confirmed it, fetch that block from our own BSV node, build a compact merkleblock proof, and emit
    /// the envelope (funding-tx hex + merkleblock hex + output index) for the recipient to import and verify.
    /// </summary>
    private async System.Threading.Tasks.Task CreateEnvelope()
    {
        var txBox = new TextBox { Width = 520, Height = 70, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas") };
        var blkBox = new TextBox { Width = 520, FontFamily = new FontFamily("Consolas") };
        var voutBox = new TextBox { Width = 80, Text = "0" };
        var outBox = new TextBox { Width = 520, Height = 110, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, IsReadOnly = true, FontFamily = new FontFamily("Consolas") };
        var go = new Button { Content = "Fetch block & build envelope", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = "Funding transaction (raw hex):", Foreground = Brushes.Gray }); sp.Children.Add(txBox);
        sp.Children.Add(new TextBlock { Text = "Confirming block hash (the block that mined it):", Foreground = Brushes.Gray, Margin = new Thickness(0, 8, 0, 0) }); sp.Children.Add(blkBox);
        sp.Children.Add(new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Children = { new TextBlock { Text = "Output index paying the recipient: ", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center }, voutBox } });
        sp.Children.Add(go);
        sp.Children.Add(new TextBlock { Text = "Envelope to hand to the recipient (merkleblock hex):", Foreground = Brushes.Gray, Margin = new Thickness(0, 8, 0, 0) }); sp.Children.Add(outBox);
        var win = new Window { Title = "Create funding envelope (SPV)", Width = 580, Height = 480, Owner = Window.GetWindow(this), Content = new ScrollViewer { Content = sp } };

        go.Click += async (_, _) =>
        {
            try
            {
                var node = _node();
                if (node == null || node.PeerCount == 0) { MessageBox.Show("Not connected to any BSV peers — cannot fetch the block.", "No peers"); return; }
                var fundTx = Chain.Deserialize(Convert.FromHexString(txBox.Text.Trim()));
                var wantTxid = Chain.Txid(fundTx);
                go.IsEnabled = false; outBox.Text = "Fetching block…";
                var raw = await node.GetBlockAsync(blkBox.Text.Trim());
                go.IsEnabled = true;
                if (raw == null) { outBox.Text = "(no block received — check the block hash, or the peer did not serve it)"; return; }
                var parsed = BsvBlock.Parse(raw); // validates the merkle root vs the header
                int idx = parsed.Txs.FindIndex(t => Chain.Txid(t) == wantTxid);
                if (idx < 0) { outBox.Text = "(the funding tx is not in that block — wrong block hash?)"; return; }
                var mb = PartialMerkleTree.BuildMerkleBlock(parsed.Header, parsed.Txids, new HashSet<int> { idx });
                outBox.Text = Convert.ToHexString(mb).ToLowerInvariant();
                _status.Text = $"Envelope built for {wantTxid[..12]}… (vout {voutBox.Text.Trim()}). Give the recipient: funding tx hex + this merkleblock hex + the output index.";
            }
            catch (Exception ex) { go.IsEnabled = true; outBox.Text = "Error: " + ex.Message; }
        };
        win.ShowDialog();
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private void DoSend()
    {
        if (!Guard()) return;
        if (!long.TryParse(_amount.Text, out var a) || a <= 0) { _status.Text = "Enter a positive amount (satoshis)."; return; }
        if (!long.TryParse(_fee.Text, out var fee) || fee < 0) { _status.Text = "Enter a fee (satoshis)."; return; }
        var dest = _dest.Text.Trim();
        byte[] destPayload;
        try { destPayload = Base58.CheckDecode(dest); } catch { _status.Text = "Invalid address (bad checksum)."; return; }
        if (destPayload.Length != 21) { _status.Text = "Invalid address length."; return; }
        if (destPayload[0] != _net().AddressVersion) { _status.Text = $"Address is for a different network (version 0x{destPayload[0]:x2}); current network expects 0x{_net().AddressVersion:x2}."; return; }
        if (a + fee > Balance) { _status.Text = $"Insufficient funds: have {Balance:N0}, need {a + fee:N0} sat."; return; }

        var node = _node();
        if (node == null || node.PeerCount == 0) { _status.Text = "Not connected to any BSV peers yet — cannot broadcast. Wait for peers, then retry."; return; }

        try
        {
            var wallet = new OnChainWallet(_seed);
            foreach (var u in _w.Utxos.Where(u => !u.Spent)) wallet.Add(new OnChainWallet.Utxo(u.Txid, u.Vout, u.Value, u.KeyChain, u.KeyIndex));
            var lockScript = Chain.P2pkhLock(destPayload[1..]);            // pay the destination's hash160
            var spend = wallet.BuildAction(lockScript, a, fee);
            if (!wallet.VerifySpend(spend)) { _status.Text = "Internal error: built transaction failed self-verification — not broadcast."; return; }

            node.Broadcast(Chain.Serialize(spend.Tx));                     // relay to the network over our own peers
            var newTxid = Chain.Txid(spend.Tx);

            // mark spent inputs and pick up our change as a new UTXO
            foreach (var inp in spend.Inputs)
                foreach (var u in _w.Utxos.Where(u => u.Txid == inp.Txid && u.Vout == inp.Vout)) u.Spent = true;
            DetectSelfOutputs(spend.Tx, newTxid);

            AppendTx("sent", -(a + fee), $"to {dest}  (tx {newTxid[..12]}…)");
            Save(); Render();
            _status.Text = $"Broadcast {a:N0} sat to {dest} (fee {fee}). tx {newTxid}";
        }
        catch (Exception ex) { _status.Text = "Send failed: " + ex.Message; }
    }

    /// <summary>After building a spend, record any outputs that pay back to one of our own keys (the change) as spendable UTXOs.</summary>
    private void DetectSelfOutputs(Chain.Tx tx, string txid)
    {
        for (int i = 0; i < tx.Outs.Count; i++)
        {
            var script = tx.Outs[i].Script;
            // change is produced on chain 1; scan a reasonable window of change keys
            for (uint ci = 0; ci < 64; ci++)
            {
                var pub = WalletKeys.Account(_seed, 1, ci).Pub;
                if (script.AsSpan().SequenceEqual(Chain.P2pkhLockForPub(pub)))
                {
                    if (!_w.Utxos.Any(u => u.Txid == txid && u.Vout == (uint)i))
                        _w.Utxos.Add(new UtxoRec { Txid = txid, Vout = (uint)i, Value = tx.Outs[i].Value, KeyChain = 1, KeyIndex = ci });
                    break;
                }
            }
        }
    }

    private void ShowCoins()
    {
        var lv = new ListView { Height = 300, Width = 560, Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x0F, 0x0F)), Foreground = Brushes.White };
        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = "Txid", Width = 280, DisplayMemberBinding = new System.Windows.Data.Binding("Txid") });
        gv.Columns.Add(new GridViewColumn { Header = "Vout", Width = 50, DisplayMemberBinding = new System.Windows.Data.Binding("Vout") });
        gv.Columns.Add(new GridViewColumn { Header = "Value (sat)", Width = 110, DisplayMemberBinding = new System.Windows.Data.Binding("Value") });
        gv.Columns.Add(new GridViewColumn { Header = "Spent", Width = 60, DisplayMemberBinding = new System.Windows.Data.Binding("Spent") });
        lv.View = gv;
        lv.ItemsSource = _w.Utxos.Select(u => new { Txid = u.Txid, u.Vout, u.Value, Spent = u.Spent ? "yes" : "" }).ToList();
        var win = new Window { Title = $"Coins — {_w.Utxos.Count(u => !u.Spent)} unspent, {Balance:N0} sat", Width = 600, Height = 360, Owner = Window.GetWindow(this), Content = lv };
        win.ShowDialog();
    }

    // ---- message signing dialogs (unchanged) ----

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

    private static Button Btn(string t) => new() { Content = t, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 6, 12, 6), Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)), Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEC)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)), BorderThickness = new Thickness(1) };

    /// <summary>Copy text to the clipboard (with a short retry — the Windows clipboard can be briefly locked).</summary>
    private void CopyToClipboard(string text, string ok)
    {
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(text); _status.Text = ok; return; }
            catch { System.Threading.Thread.Sleep(40); }
        }
        _status.Text = "Could not access the clipboard — select the text and copy manually.";
    }

    // ---- persistence / seed lifecycle ----

    private void Load()
    {
        try { if (File.Exists(_path)) _w = JsonSerializer.Deserialize<File_>(File.ReadAllText(_path)) ?? new File_(); } catch { _w = new File_(); }
        // NO FAKE COINS / NO FAKE HISTORY: keep ONLY real, SPV-CONFIRMED coins (spent ones are kept so genuine
        // history survives a restart; the balance counts confirmed-unspent only). Drop any not-yet-confirmed
        // (pending) coins — they are re-discovered by the SPV rescan, never carried over as if real. The send
        // log (_w.Sends) is real broadcasts this wallet made; labels/contacts/requests are user metadata.
        _w.Utxos = _w.Utxos.Where(u => u.Confirmed).ToList();
        _w.History.Clear();
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
            // brand-new wallet: a fresh seed, and EMPTY — no play money, no opening balance, no UTXOs.
            _seed = WalletKeys.NewSeed();
            _w = new File_ { Seed = WalletKeys.SeedToBackup(_seed), RecvIndex = 0 };
            _freshWallet = true;     // first run → run the ElectrumSVP-style account wizard
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
                OnUnlocked?.Invoke();
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

    private void Restore()
    {
        var box = new TextBox { AcceptsReturn = true, Height = 80, Width = 460, TextWrapping = TextWrapping.Wrap };
        var ok = new Button { Content = "Restore", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        var win = new Window { Title = "Restore wallet — enter your seed backup", Width = 500, Height = 200, Owner = Window.GetWindow(this), Content = new StackPanel { Margin = new Thickness(12), Children = { box, ok } } };
        ok.Click += (_, _) =>
        {
            try { _seed = WalletKeys.BackupToSeed(box.Text.Trim()); _w = new File_ { Seed = WalletKeys.SeedToBackup(_seed), RecvIndex = 0 }; _locked = false; OnUnlocked?.Invoke(); AppendTx("restore", 0, "wallet restored from seed"); Save(); Render(); win.Close(); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Invalid seed"); }
        };
        win.ShowDialog();
    }

    // History is DERIVED from real SPV-confirmed coins (see DerivedHistory) — there is NO free-form log that
    // could record amounts/events that never happened. This is intentionally a no-op: nothing fake is stored.
    private void AppendTx(string type, long amount, string memo) { }

    /// <summary>
    /// The wallet history, derived ONLY from REAL on-chain movements the wallet actually saw: every received
    /// coin (SPV-confirmed or pending), and every transaction this wallet actually broadcast (recorded in
    /// _w.Sends). A fresh/unfunded wallet has no coins and no sends, so it shows NOTHING — never invented.
    /// </summary>
    private List<Tx> DerivedHistory()
    {
        var rows = new List<Tx>();
        foreach (var u in _w.Utxos)
            rows.Add(new Tx {
                Time = u.Confirmed ? "on-chain" : "pending",
                Type = "received",
                Amount = u.Value,
                Balance = 0,
                Memo = $"{u.Txid} :{u.Vout}" + (_w.TxLabels.TryGetValue(u.Txid, out var rl) ? "  — " + rl : ""),
            });
        foreach (var s in _w.Sends)
            rows.Add(new Tx {
                Time = s.Time,
                Type = "sent",
                Amount = -(s.Amount + s.Fee),
                Balance = 0,
                Memo = $"{s.Txid}  → {s.To}" + (_w.TxLabels.TryGetValue(s.Txid, out var sl) ? "  — " + sl : ""),
            });
        long run = 0;
        foreach (var r in rows) { run += r.Amount; r.Balance = run; }
        return rows;
    }

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

    /// <summary>Re-render the wallet (e.g. after the network selector changes, so the address matches the network).</summary>
    public void Refresh()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Refresh)); return; }
        Render();
    }

    private void Render()
    {
        if (_locked)
        {
            _bal.Text = "🔒 locked";
            _sbBalance.Text = "🔒 locked"; _sbLock.Text = "🔒 locked"; _sbNetwork.Text = $"{_net().Network}";
            _recv.Text = "Wallet is encrypted — press “Unlock…” (Wallet menu) to enter your password.";
            _historyGrid.ItemsSource = null; _coinsGrid.ItemsSource = null; _addrGrid.ItemsSource = null;
            return;
        }
        if (_ring == null && _seed.Length == 32) _ring = new KeyRing(_seed, Math.Max(1, _w.RecvIndex));
        _bal.Text = Balance.ToString("N0") + " sat" + (Pending > 0 ? $"   (+{Pending:N0} pending)" : "");
        _recv.Text = ReceiveAddress() + $"   (#{_w.RecvIndex})";
        // always show a scannable QR of the current receiving address (or the active payment-request URI)
        try { _reqQr.Source = RenderQr(_reqUri.Text.Length > 0 ? _reqUri.Text : "bitcoin:" + ReceiveAddress()); } catch { }

        ApplyHistoryFilter();
        _coinsGrid.ItemsSource = _w.Utxos.Select(u => new {
            Outpoint = $"{u.Txid[..Math.Min(12, u.Txid.Length)]}…:{u.Vout}",
            Address = AddressForKey(u.KeyChain, u.KeyIndex),
            Value = u.Value.ToString("N0"),
            Status = u.Spent ? "spent" : (u.Confirmed ? "confirmed" : "pending"),
            Frozen = u.Frozen ? "❄" : "",
            Label = _w.CoinLabels.TryGetValue($"{u.Txid}:{u.Vout}", out var cl) ? cl : "",
            FullOutpoint = $"{u.Txid}:{u.Vout}",
        }).Where(x => true).ToList();

        // Addresses: identity-derived receive keys 0..RecvIndex + their balances/labels
        var addrRows = new List<object>();
        for (uint i = 0; i <= (uint)_w.RecvIndex; i++)
        {
            var addr = AddressForKey(0, i);
            long bal = _w.Utxos.Where(u => !u.Spent && u.KeyChain == 0 && u.KeyIndex == i).Sum(u => u.Value);
            bool used = _w.Utxos.Any(u => u.KeyChain == 0 && u.KeyIndex == i);
            addrRows.Add(new { Address = addr, Path = $"receive/{i}", Balance = bal.ToString("N0"), Used = used ? "yes" : "", Label = _w.AddrLabels.TryGetValue(addr, out var l) ? l : "" });
        }
        _addrGrid.ItemsSource = addrRows;

        _contactsGrid.ItemsSource = _w.Contacts.ToList();
        _vaultsGrid.ItemsSource = _w.Vaults.Select(v => new { v.Name, v.Address, Funded = v.Funded ? v.FundValue.ToString("N0") : "—", v.LockHeight }).ToList();
        _requestsGrid.ItemsSource = _w.Requests.AsEnumerable().Reverse().Select(q => new {
            q.Time, q.Amount, q.Memo, q.Expires, q.Address,
            Status = (string.IsNullOrEmpty(q.Expires) ? "active" : (DateTime.TryParse(q.Expires, out var e) && e < DateTime.Now ? "expired" : "active")),
        }).ToList();

        // Transactions tab: pending / unconfirmed coins only
        _pendingGrid.ItemsSource = _w.Utxos.Where(u => !u.Confirmed && !u.Spent).Select(u => new {
            Status = "unconfirmed", Amount = u.Value.ToString("N0"), Memo = $"{u.Txid} :{u.Vout}",
        }).ToList();

        if (_ring != null) _idPub.Text = Convert.ToHexString(_identityPub).ToLowerInvariant();
        if (string.IsNullOrEmpty(_idHandle.Text)) _idHandle.Text = _w.Handle;

        UpdateStatusBar();
        RefreshCards();
    }

    private void UpdateStatusBar()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(UpdateStatusBar)); return; }
        if (_locked) { _sbBalance.Text = "🔒 locked"; _sbLock.Text = "🔒 locked"; _sbNetwork.Text = $"{_net().Network}"; return; }
        _sbBalance.Text = $"{Balance:N0} sat" + (Pending > 0 ? $"  (+{Pending:N0} pending)" : "");
        _sbLock.Text = "🔓 unlocked";
        var node = _node();
        _sbNetwork.Text = $"{_net().Network}  ·  SPV peers: {(node?.PeerCount ?? 0)}";
    }

    /// <summary>Resolve a peer's identity public key (hex) to a saved contact handle, or null. Lets the chat /
    /// game show @handles instead of raw keys — the wallet's Contacts are the app-wide address book.</summary>
    public string? HandleFor(string identityPubHex)
    {
        var c = _w.Contacts.FirstOrDefault(x => string.Equals(x.IdentityPub, identityPubHex, StringComparison.OrdinalIgnoreCase));
        return c?.Handle;
    }

    /// <summary>The P2PKH address for one of our keys (network-aware), used to label coins/addresses.</summary>
    private string AddressForKey(uint chain, uint index)
    {
        var pub = WalletKeys.Account(_seed, chain, index).Pub;
        var payload = new byte[21]; payload[0] = _net().AddressVersion; Hashes.Hash160(pub).CopyTo(payload, 1);
        return Base58.CheckEncode(payload);
    }

    // ============================ Send / fees / payee resolution ============================

    private long FeeRatePerKb() => (_feeRate.SelectedIndex) switch { 0 => 500, 2 => 5000, 3 => -1, _ => 1000 };
    private long EstimateFee(int outputs)
    {
        long rate = FeeRatePerKb();
        if (rate < 0) { return long.TryParse(_fee.Text, out var f) && f >= 0 ? f : 0; } // custom fee field
        int inputs = Math.Max(1, _w.Utxos.Count(u => !u.Spent && !u.Frozen && u.Confirmed));
        int size = inputs * 148 + outputs * 34 + 10;
        return Math.Max(1, (long)Math.Ceiling(size * rate / 1000.0));
    }

    /// <summary>
    /// Resolve a "pay to" string into a P2PKH lock script and a human destination label. Accepts: a Base58
    /// address; an identity handle (@bob / a known contact); an identity public key (hex, 33 bytes); or a
    /// bitcoin:/pay: URI (address + optional amount). Identity targets derive a fresh one-time Type-42 sub-
    /// address from the payee's identity key + our identity key + a random invoice (returned for the receipt).
    /// </summary>
    private (byte[] Lock, string Dest, string? IdentityPub, string? Invoice)? ResolvePayee(string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0) { _sendStatus.Text = "Enter who to pay."; return null; }

        // a payment URI: bitcoin:<addr>?amount=..  or pay:<addr>
        if (raw.StartsWith("bitcoin:", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("pay:", StringComparison.OrdinalIgnoreCase))
        {
            var body = raw.Substring(raw.IndexOf(':') + 1);
            var q = body.IndexOf('?');
            var addr = (q >= 0 ? body[..q] : body).Trim();
            if (q >= 0)
            {
                foreach (var kv in body[(q + 1)..].Split('&'))
                {
                    var p = kv.Split('=', 2);
                    if (p.Length == 2 && p[0].Equals("amount", StringComparison.OrdinalIgnoreCase) && decimal.TryParse(p[1], out var amtBsv))
                        _amount.Text = ((long)(amtBsv * 100_000_000m)).ToString(); // URI amounts are in BSV
                }
            }
            raw = addr;
        }

        return ResolveToken(raw);
    }

    /// <summary>Resolve a single clean payee token (no URI parsing): a handle (@bob), an identity pubkey (hex)
    /// → a one-time Type-42 address, or a Base58 address. Returns null and sets the status on failure.</summary>
    private (byte[] Lock, string Dest, string? IdentityPub, string? Invoice)? ResolveToken(string raw)
    {
        raw = raw.Trim();
        // an identity handle
        if (raw.StartsWith("@"))
        {
            var h = raw[1..];
            var c = _w.Contacts.FirstOrDefault(x => string.Equals(x.Handle, h, StringComparison.OrdinalIgnoreCase));
            if (c == null) { _sendStatus.Text = $"No contact @{h}. Add their identity key in Contacts first."; return null; }
            raw = c.IdentityPub; // fall through to identity-pubkey handling
        }

        // an identity public key (33-byte compressed, hex) → Type-42 one-time payment address
        if ((raw.Length == 66 || raw.Length == 130) && System.Text.RegularExpressions.Regex.IsMatch(raw, "^[0-9a-fA-F]+$"))
        {
            try
            {
                var payeeId = Convert.FromHexString(raw);
                if (Secp256k1.IsValidPoint(payeeId))
                {
                    var invoice = "pay-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
                    var oneTimePub = IdentityPayment.PayToPub(payeeId, _identityPriv, invoice);
                    var lockS = Chain.P2pkhLockForPub(oneTimePub);
                    return (lockS, $"identity {raw[..12]}… (one-time {Convert.ToHexString(Hashes.Hash160(oneTimePub))[..8]}…)", raw, invoice);
                }
            }
            catch { }
        }

        // a Base58 address — P2PKH (AddressVersion) or P2SH/multisig vault (ScriptVersion)
        try
        {
            var payload = Base58.CheckDecode(raw);
            if (payload.Length != 21) { _sendStatus.Text = "Invalid address length."; return null; }
            if (payload[0] == _net().AddressVersion) return (Chain.P2pkhLock(payload[1..]), raw, null, null);
            if (payload[0] == _net().ScriptVersion) return (Chain.P2shLockFromHash(payload[1..]), raw + " (P2SH)", null, null);
            _sendStatus.Text = $"Address is for a different network (0x{payload[0]:x2}); current network expects 0x{_net().AddressVersion:x2} / 0x{_net().ScriptVersion:x2}."; return null;
        }
        catch { _sendStatus.Text = "Could not parse 'pay to' — not an address, identity, handle, or URI."; return null; }
    }

    private async System.Threading.Tasks.Task SendPayment()
    {
        _sendStatus.Text = "";
        // Pay-to-many: each non-empty line is "<payee>,<amount>" (or "<payee> <amount>"). A single line with no
        // amount falls back to the Amount box. The payee on any line may be an address, @handle, identity key,
        // or URI — a payment is a payment.
        var lines = _sendPayTo.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var outputs = new List<(byte[] Lock, long Value)>();
        var dests = new List<string>();
        var receipts = new List<(string Dest, string IdPub, string Invoice)>();
        if (lines.Count == 0) { _sendStatus.Text = "Enter who to pay."; return; }

        if (lines.Count == 1 && !lines[0].Contains(',') && !(lines[0].Contains(' ') && long.TryParse(lines[0].Split(' ').Last(), out _)))
        {
            var r = ResolvePayee(lines[0]); if (r == null) return;
            if (!long.TryParse(_amount.Text, out var amt) || amt <= 0) { _sendStatus.Text = "Enter a positive amount (sat)."; return; }
            outputs.Add((r.Value.Lock, amt)); dests.Add(r.Value.Dest);
            if (r.Value.IdentityPub != null) receipts.Add((r.Value.Dest, r.Value.IdentityPub, r.Value.Invoice!));
        }
        else
        {
            foreach (var line in lines)
            {
                var sep = line.Contains(',') ? ',' : ' ';
                var idx = line.LastIndexOf(sep);
                if (idx <= 0 || !long.TryParse(line[(idx + 1)..].Trim(), out var amt) || amt <= 0)
                { _sendStatus.Text = $"Each line must be '<payee>{sep}<amount-in-sat>'. Bad line: {line}"; return; }
                var token = line[..idx].Trim();
                var r = token.StartsWith("bitcoin:", StringComparison.OrdinalIgnoreCase) || token.StartsWith("pay:", StringComparison.OrdinalIgnoreCase) ? ResolvePayee(token) : ResolveToken(token);
                if (r == null) return;
                outputs.Add((r.Value.Lock, amt)); dests.Add(r.Value.Dest);
                if (r.Value.IdentityPub != null) receipts.Add((r.Value.Dest, r.Value.IdentityPub, r.Value.Invoice!));
            }
        }

        var label = _sendLabel.Text.Trim();
        long total = outputs.Sum(o => o.Value);
        long fee = EstimateFee(outputs.Count + 1);
        if (total + fee > Balance) { _sendStatus.Text = $"Insufficient funds: have {Balance:N0}, need {total + fee:N0} sat (incl. fee {fee:N0})."; return; }
        var node = _node();
        if (node == null || node.PeerCount == 0) { _sendStatus.Text = "No BSV peers connected yet — cannot broadcast. Wait for peers, then retry."; return; }
        try
        {
            var w = new OnChainWallet(_seed);
            foreach (var u in _w.Utxos.Where(u => !u.Spent && !u.Frozen && u.Confirmed)) w.Add(new OnChainWallet.Utxo(u.Txid, u.Vout, u.Value, u.KeyChain, u.KeyIndex));
            var spend = w.BuildActionMany(outputs.Select(o => (o.Lock, o.Value)).ToList(), fee);
            if (!w.VerifySpend(spend)) { _sendStatus.Text = "Built transaction failed self-verification — not broadcast."; return; }
            node.Broadcast(Chain.Serialize(spend.Tx));
            var txid = Chain.Txid(spend.Tx);
            foreach (var inp in spend.Inputs) foreach (var u in _w.Utxos.Where(u => u.Txid == inp.Txid && u.Vout == inp.Vout)) u.Spent = true;
            DetectSelfOutputs(spend.Tx, txid);
            _w.Sends.Add(new SendRec { Txid = txid, Amount = total, Fee = fee, To = string.Join("; ", dests), Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), RawHex = Convert.ToHexString(Chain.Serialize(spend.Tx)).ToLowerInvariant() });
            Notify($"Sent {total:N0} sat (fee {fee:N0}) to {dests.Count} output(s).");
            if (!string.IsNullOrWhiteSpace(label)) _w.TxLabels[txid] = label;
            Save(); Render();
            if (receipts.Count > 0)
            {
                var rcpt = string.Join("\n", receipts.Select(x => $"identity-payment|{Convert.ToHexString(_identityPub).ToLowerInvariant()}|{x.Invoice}|{txid}"));
                CopyToClipboard(rcpt, "Sent. Identity-payment receipt(s) copied — give them to the payee(s) to claim.");
                MessageBox.Show($"Paid {total:N0} sat.\n\nGive each payee their claim receipt so they can derive the key and see the coin:\n\n{rcpt}", "Identity payment sent");
            }
            _sendStatus.Text = $"Broadcast {total:N0} sat (fee {fee:N0}) to {dests.Count} output(s).  tx {txid}";
            _sendPayTo.Clear(); _sendLabel.Clear();
        }
        catch (Exception ex) { _sendStatus.Text = "Send failed: " + ex.Message; }
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private void PreviewSend()
    {
        var resolved = ResolvePayee(_sendPayTo.Text);
        if (resolved == null) return;
        if (!long.TryParse(_amount.Text, out var amount) || amount <= 0) { _sendStatus.Text = "Enter a positive amount (sat)."; return; }
        long fee = EstimateFee(1);
        // build the REAL signed transaction (without broadcasting) so the user can inspect and copy it
        string rawHex = "(built on Send)";
        try
        {
            var w = new OnChainWallet(_seed);
            foreach (var u in _w.Utxos.Where(u => !u.Spent && !u.Frozen && u.Confirmed)) w.Add(new OnChainWallet.Utxo(u.Txid, u.Vout, u.Value, u.KeyChain, u.KeyIndex));
            var spend = w.BuildAction(resolved.Value.Lock, amount, fee);
            rawHex = Convert.ToHexString(Chain.Serialize(spend.Tx)).ToLowerInvariant();
        }
        catch (Exception ex) { rawHex = "(could not build: " + ex.Message + ")"; }

        var info = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), Background = FieldBg, Foreground = Ink, BorderThickness = new Thickness(0),
            Text = $"Pay to: {resolved.Value.Dest}\nAmount: {amount:N0} sat\nFee: {fee:N0} sat\nTotal: {amount + fee:N0} sat\nFrom balance: {Balance:N0} sat\nLabel: {_sendLabel.Text}\n\nRaw transaction (hex):\n{rawHex}" };
        var copy = new Button { Content = "Copy raw tx", Margin = new Thickness(0, 8, 8, 0), Padding = new Thickness(10, 6, 10, 6) };
        copy.Click += (_, _) => CopyToClipboard(rawHex, "Raw transaction copied.");
        var sp = new StackPanel { Margin = new Thickness(12) }; sp.Children.Add(info); sp.Children.Add(copy);
        new Window { Title = "Preview payment", Width = 560, Height = 360, Owner = Window.GetWindow(this), Background = WinBg, Content = new ScrollViewer { Content = sp } }.ShowDialog();
    }

    private void PasteUri()
    {
        try { var t = Clipboard.GetText(); if (!string.IsNullOrWhiteSpace(t)) { _sendPayTo.Text = t.Trim(); SelectTab("Send"); _sendStatus.Text = "Pasted from clipboard — review and Send."; } }
        catch { _sendStatus.Text = "Nothing to paste."; }
    }

    // ============================ Encrypt / decrypt a message (ECIES to an identity) ============================

    private void EncryptMessageDialog(string prefillTo = "")
    {
        var to = new TextBox { Width = 520, FontFamily = new FontFamily("Consolas"), Text = prefillTo };
        var msg = new TextBox { Width = 520, Height = 80, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        var outp = new TextBox { Width = 520, Height = 90, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, IsReadOnly = true, FontFamily = new FontFamily("Consolas") };
        var go = new Button { Content = "Encrypt", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = "Recipient identity (@handle or identity pubkey hex):", Foreground = Brushes.Gray }); sp.Children.Add(to);
        sp.Children.Add(new TextBlock { Text = "Message:", Foreground = Brushes.Gray }); sp.Children.Add(msg);
        sp.Children.Add(go);
        sp.Children.Add(new TextBlock { Text = "Encrypted (base64) — give this to the recipient:", Foreground = Brushes.Gray }); sp.Children.Add(outp);
        var win = new Window { Title = "Encrypt a message to an identity", Width = 580, Height = 420, Owner = Window.GetWindow(this), Content = new ScrollViewer { Content = sp } };
        go.Click += (_, _) =>
        {
            try
            {
                var raw = to.Text.Trim();
                if (raw.StartsWith("@")) { var c = _w.Contacts.FirstOrDefault(x => string.Equals(x.Handle, raw[1..], StringComparison.OrdinalIgnoreCase)); if (c == null) { MessageBox.Show("Unknown contact."); return; } raw = c.IdentityPub; }
                var recipient = Convert.FromHexString(raw);
                var eph = Secp256k1.GenerateKeyPair();                       // ephemeral key — no key reuse
                var key = Aead.Hkdf(Secp256k1.Ecdh(eph.Priv, recipient), eph.Pub, System.Text.Encoding.ASCII.GetBytes("bsvpoker-ecies"));
                var blob = Aead.Seal(key, System.Text.Encoding.UTF8.GetBytes(msg.Text), eph.Pub);
                var packet = new byte[eph.Pub.Length + blob.Length]; eph.Pub.CopyTo(packet, 0); blob.CopyTo(packet, eph.Pub.Length);
                outp.Text = Convert.ToBase64String(packet);
            }
            catch (Exception ex) { MessageBox.Show("Encrypt failed: " + ex.Message); }
        };
        win.ShowDialog();
    }

    private void DecryptMessageDialog()
    {
        var inp = new TextBox { Width = 520, Height = 90, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas") };
        var outp = new TextBox { Width = 520, Height = 90, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, IsReadOnly = true };
        var go = new Button { Content = "Decrypt", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = "Encrypted message (base64) addressed to your identity:", Foreground = Brushes.Gray }); sp.Children.Add(inp);
        sp.Children.Add(go);
        sp.Children.Add(new TextBlock { Text = "Decrypted:", Foreground = Brushes.Gray }); sp.Children.Add(outp);
        var win = new Window { Title = "Decrypt a message", Width = 580, Height = 360, Owner = Window.GetWindow(this), Content = new ScrollViewer { Content = sp } };
        go.Click += (_, _) =>
        {
            try
            {
                var packet = Convert.FromBase64String(inp.Text.Trim());
                var ephPub = packet[..33]; var blob = packet[33..];
                var key = Aead.Hkdf(Secp256k1.Ecdh(_identityPriv, ephPub), ephPub, System.Text.Encoding.ASCII.GetBytes("bsvpoker-ecies"));
                outp.Text = System.Text.Encoding.UTF8.GetString(Aead.Open(key, blob, ephPub));
            }
            catch (Exception ex) { MessageBox.Show("Decrypt failed (not addressed to you, or tampered): " + ex.Message); }
        };
        win.ShowDialog();
    }

    // ============================ Transaction details + labels ============================

    private void TxDetails(string txid)
    {
        var ins = _w.Utxos.Where(u => u.Txid == txid).ToList();
        var send = _w.Sends.FirstOrDefault(s => s.Txid == txid);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Transaction id: " + txid);
        if (_w.TxLabels.TryGetValue(txid, out var lbl)) sb.AppendLine("Label: " + lbl);
        if (send != null) sb.AppendLine($"Sent {send.Amount:N0} sat (fee {send.Fee:N0}) at {send.Time}  →  {send.To}");
        // full input/output breakdown from the stored raw transaction, when we have it
        if (send != null && send.RawHex.Length > 0)
        {
            try
            {
                var tx = Chain.Deserialize(Convert.FromHexString(send.RawHex));
                sb.AppendLine($"\nVersion {tx.Version}, locktime {tx.LockTime}, size {send.RawHex.Length / 2} bytes");
                sb.AppendLine($"\nInputs ({tx.Ins.Count}):");
                foreach (var i in tx.Ins) sb.AppendLine($"  {i.PrevTxid[..Math.Min(16, i.PrevTxid.Length)]}…:{i.Vout}");
                sb.AppendLine($"\nOutputs ({tx.Outs.Count}):");
                for (int o = 0; o < tx.Outs.Count; o++)
                {
                    var os = tx.Outs[o];
                    string who = os.Script.Length == 25 && os.Script[0] == 0x76 ? Base58.CheckEncode(Prefix(_net().AddressVersion, os.Script[3..23])) : $"script[{os.Script.Length}B]";
                    sb.AppendLine($"  :{o} = {os.Value:N0} sat → {who}");
                }
            }
            catch { }
        }
        else
        {
            sb.AppendLine("\nOur outputs in this tx:");
            foreach (var u in ins) sb.AppendLine($"  :{u.Vout} = {u.Value:N0} sat → {AddressForKey(u.KeyChain, u.KeyIndex)} [{(u.Spent ? "spent" : u.Confirmed ? "confirmed" : "pending")}]");
        }
        var box = new TextBox { Text = sb.ToString(), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), Background = FieldBg, Foreground = Ink, BorderThickness = new Thickness(0) };
        var win = new Window { Title = "Transaction details", Width = 560, Height = 420, Owner = Window.GetWindow(this), Background = WinBg, Content = new ScrollViewer { Content = box, Margin = new Thickness(10) } };
        win.ShowDialog();
    }

    private void SetCoinLabel(string outpoint)
    {
        var box = new TextBox { Width = 420, Text = _w.CoinLabels.TryGetValue(outpoint, out var l) ? l : "" };
        var ok = new Button { Content = "Save", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        var win = new Window { Title = "Label for coin " + outpoint[..Math.Min(18, outpoint.Length)] + "…", Width = 470, Height = 150, Owner = Window.GetWindow(this), Background = WinBg, Content = new StackPanel { Margin = new Thickness(12), Children = { new TextBlock { Text = "Coin label:", Foreground = Ink }, box, ok } } };
        ok.Click += (_, _) => { var t = box.Text.Trim(); if (t.Length == 0) _w.CoinLabels.Remove(outpoint); else _w.CoinLabels[outpoint] = t; Save(); Render(); win.Close(); };
        win.ShowDialog();
    }

    private void SetAddrLabel(string address)
    {
        var box = new TextBox { Width = 420, Text = _w.AddrLabels.TryGetValue(address, out var l) ? l : "" };
        var ok = new Button { Content = "Save", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        var win = new Window { Title = "Label for " + address, Width = 470, Height = 150, Owner = Window.GetWindow(this), Background = WinBg, Content = new StackPanel { Margin = new Thickness(12), Children = { new TextBlock { Text = "Address label:", Foreground = Ink }, box, ok } } };
        ok.Click += (_, _) => { var t = box.Text.Trim(); if (t.Length == 0) _w.AddrLabels.Remove(address); else _w.AddrLabels[address] = t; Save(); Render(); win.Close(); };
        win.ShowDialog();
    }

    private void SetTxLabel(string txid)
    {
        var box = new TextBox { Width = 420, Text = _w.TxLabels.TryGetValue(txid, out var l) ? l : "" };
        var ok = new Button { Content = "Save", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        var win = new Window { Title = "Label for " + txid[..Math.Min(16, txid.Length)] + "…", Width = 470, Height = 150, Owner = Window.GetWindow(this), Content = new StackPanel { Margin = new Thickness(12), Children = { new TextBlock { Text = "Label:", Foreground = Brushes.Gray }, box, ok } } };
        ok.Click += (_, _) => { var t = box.Text.Trim(); if (t.Length == 0) _w.TxLabels.Remove(txid); else _w.TxLabels[txid] = t; Save(); Render(); win.Close(); };
        win.ShowDialog();
    }

    // ============================ Sweep an external private key (WIF) ============================

    private void SweepWif()
    {
        var box = new TextBox { Width = 420, FontFamily = new FontFamily("Consolas") };
        var ok = new Button { Content = "Sweep", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = "Private key (WIF) to sweep — its coins are sent to THIS wallet:", Foreground = Brushes.Gray });
        sp.Children.Add(box); sp.Children.Add(ok);
        var win = new Window { Title = "Sweep a private key", Width = 470, Height = 170, Owner = Window.GetWindow(this), Content = sp };
        ok.Click += (_, _) =>
        {
            try
            {
                var payload = Base58.CheckDecode(box.Text.Trim());
                if (payload.Length < 33 || payload[0] != _net().WifVersion) { MessageBox.Show("Not a WIF for this network."); return; }
                var priv = payload[1..33];
                var pub = Secp256k1.PublicKeyCompressed(priv);
                var addr = Base58.CheckEncode(Prefix(_net().AddressVersion, Hashes.Hash160(pub)));
                // record the key to watch + rescan; once its UTXOs are discovered by SPV, send them to ourselves
                if (!_w.SweptKeys.Contains(Convert.ToHexString(priv))) _w.SweptKeys.Add(Convert.ToHexString(priv).ToLowerInvariant());
                Save();
                RescanRequested?.Invoke();
                MessageBox.Show($"Watching {addr} for coins to sweep. When the SPV rescan finds its UTXOs they will be swept into this wallet.\n\nUse Receive → Rescan / Find by txid if you know the funding txid.", "Sweep");
                win.Close();
            }
            catch (Exception ex) { MessageBox.Show("Bad WIF: " + ex.Message); }
        };
        win.ShowDialog();
    }

    // ============================ BIP270 invoice paying (Anypay, Centi) ============================

    private async System.Threading.Tasks.Task PayInvoice()
    {
        // get the payment URL: prefer what's in "Pay to", else prompt
        string? url = Bip270.ExtractRequestUrl(_sendPayTo.Text);
        if (url == null)
        {
            var box = new TextBox { Width = 520, FontFamily = new FontFamily("Consolas") };
            var ok = new Button { Content = "Fetch invoice", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
            var sp = new StackPanel { Margin = new Thickness(12) };
            sp.Children.Add(new TextBlock { Text = "Paste a BIP270 payment URL or invoice (Anypay, Centi, …):", Foreground = Brushes.Gray });
            sp.Children.Add(box); sp.Children.Add(ok);
            var win = new Window { Title = "Pay an invoice (BIP270)", Width = 580, Height = 180, Owner = Window.GetWindow(this), Content = sp };
            ok.Click += (_, _) => { url = Bip270.ExtractRequestUrl(box.Text) ?? box.Text.Trim(); win.Close(); };
            win.ShowDialog();
            if (string.IsNullOrWhiteSpace(url)) return;
        }
        var node = _node();
        if (node == null || node.PeerCount == 0) { _sendStatus.Text = "No BSV peers connected yet — cannot pay an invoice."; return; }
        try
        {
            _sendStatus.Text = "Fetching invoice…";
            var pr = await Bip270.FetchAsync(url!);
            long fee = EstimateFee(pr.Outputs.Count + 1);
            if (pr.Total + fee > Balance) { _sendStatus.Text = $"Invoice is {pr.Total:N0} sat (+fee {fee:N0}); balance {Balance:N0} sat is too low."; return; }
            var confirm = MessageBox.Show($"Invoice from: {url}\nMemo: {pr.Memo}\nOutputs: {pr.Outputs.Count}\nTotal: {pr.Total:N0} sat\nFee: {fee:N0} sat\n\nPay now?", "Pay invoice (BIP270)", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) { _sendStatus.Text = "Invoice cancelled."; return; }

            var w = new OnChainWallet(_seed);
            foreach (var u in _w.Utxos.Where(u => !u.Spent && !u.Frozen && u.Confirmed)) w.Add(new OnChainWallet.Utxo(u.Txid, u.Vout, u.Value, u.KeyChain, u.KeyIndex));
            var spend = w.BuildActionMany(pr.Outputs.Select(o => (o.Script, o.Amount)).ToList(), fee);
            if (!w.VerifySpend(spend)) { _sendStatus.Text = "Built invoice tx failed self-verification — not sent."; return; }
            var rawHex = Convert.ToHexString(Chain.Serialize(spend.Tx)).ToLowerInvariant();

            node.Broadcast(Chain.Serialize(spend.Tx));                       // put the money on-chain
            var txid = Chain.Txid(spend.Tx);
            var ack = await Bip270.SubmitAsync(pr, rawHex, ReceiveAddress(), pr.Memo);   // hand the merchant the payment

            foreach (var inp in spend.Inputs) foreach (var u in _w.Utxos.Where(u => u.Txid == inp.Txid && u.Vout == inp.Vout)) u.Spent = true;
            DetectSelfOutputs(spend.Tx, txid);
            _w.Sends.Add(new SendRec { Txid = txid, Amount = pr.Total, Fee = fee, To = $"invoice: {pr.Memo}", Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), RawHex = rawHex });
            Save(); Render();
            _sendStatus.Text = ack.Ok ? $"Invoice paid — tx {txid}. Merchant ACK: {ack.Memo}" : $"Paid on-chain (tx {txid}) but the merchant ACK failed: {ack.Raw}";
        }
        catch (Exception ex) { _sendStatus.Text = "Invoice payment failed: " + ex.Message; }
    }

    // ============================ Receive: requests + QR ============================

    private void CreateRequest()
    {
        long.TryParse(_reqAmount.Text, out var amt);
        var addr = ReceiveAddress();
        var memo = _reqMemo.Text.Trim();
        var exp = (_reqExpiry.SelectedItem as string) switch { "1 hour" => DateTime.Now.AddHours(1), "1 day" => DateTime.Now.AddDays(1), "1 week" => DateTime.Now.AddDays(7), _ => DateTime.MaxValue };
        _w.Requests.Add(new PayRequest { Address = addr, Amount = amt, Memo = memo, Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), Expires = exp == DateTime.MaxValue ? "" : exp.ToString("yyyy-MM-dd HH:mm") });
        _w.RecvIndex++; // a new request gets a fresh address next time
        Save();
        var uri = $"bitcoin:{addr}" + (amt > 0 || memo.Length > 0 ? "?" : "") +
                  string.Join("&", new[] { amt > 0 ? $"amount={amt / 100_000_000m}" : null, memo.Length > 0 ? $"label={Uri.EscapeDataString(memo)}" : null }.Where(s => s != null));
        _reqUri.Text = uri;
        try { _reqQr.Source = RenderQr(uri); } catch (Exception ex) { _sendStatus.Text = "QR error: " + ex.Message; }
        Render();
    }

    /// <summary>Render a QR matrix to a crisp black-on-white bitmap (8 px/module + a quiet zone).</summary>
    private static System.Windows.Media.Imaging.BitmapSource RenderQr(string text)
    {
        var m = QrCode.Encode(text);
        int n = m.GetLength(0), scale = 6, quiet = 4, dim = (n + quiet * 2) * scale;
        var wb = new System.Windows.Media.Imaging.WriteableBitmap(dim, dim, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        var px = new byte[dim * dim * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 255; px[i + 1] = 255; px[i + 2] = 255; px[i + 3] = 255; } // white
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
                if (m[r, c])
                    for (int dy = 0; dy < scale; dy++)
                        for (int dx = 0; dx < scale; dx++)
                        {
                            int y = (r + quiet) * scale + dy, x = (c + quiet) * scale + dx, o = (y * dim + x) * 4;
                            px[o] = 0; px[o + 1] = 0; px[o + 2] = 0; px[o + 3] = 255; // black module
                        }
        wb.WritePixels(new Int32Rect(0, 0, dim, dim), px, dim * 4, 0);
        return wb;
    }

    private void ExportHistory()
    {
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "bsvpoker-history.csv" };
            if (dlg.ShowDialog() != true) return;
            var lines = new List<string> { "time,type,amount_sat,balance_sat,memo" };
            foreach (var t in DerivedHistory()) lines.Add($"{t.Time},{t.Type},{t.Amount},{t.Balance},\"{t.Memo.Replace("\"", "'")}\"");
            File.WriteAllLines(dlg.FileName, lines);
            _status.Text = "History exported to " + dlg.FileName;
        }
        catch (Exception ex) { _status.Text = "Export failed: " + ex.Message; }
    }

    // ============================ Coins: freeze + spend selected ============================

    private void SetFrozen(bool frozen)
    {
        foreach (var item in _coinsGrid.SelectedItems)
        {
            var op = PropOf(item!, "FullOutpoint"); var parts = op.Split(':');
            if (parts.Length != 2 || !uint.TryParse(parts[1], out var vout)) continue;
            foreach (var u in _w.Utxos.Where(u => u.Txid == parts[0] && u.Vout == vout)) u.Frozen = frozen;
        }
        Save(); Render();
    }

    private void SpendSelectedCoins()
    {
        var picked = new List<UtxoRec>();
        foreach (var item in _coinsGrid.SelectedItems)
        {
            var op = PropOf(item!, "FullOutpoint"); var parts = op.Split(':');
            if (parts.Length == 2 && uint.TryParse(parts[1], out var vout))
                picked.AddRange(_w.Utxos.Where(u => u.Txid == parts[0] && u.Vout == vout && !u.Spent));
        }
        if (picked.Count == 0) { _status.Text = "Select one or more coins to spend."; return; }
        SelectTab("Send");
        _amount.Text = Math.Max(0, picked.Sum(u => u.Value) - EstimateFee(1)).ToString();
        _sendStatus.Text = $"{picked.Count} coin(s) selected ({picked.Sum(u => u.Value):N0} sat). Enter who to pay and Send.";
    }

    // ============================ Addresses: WIF ============================

    private void ShowWif()
    {
        if (_addrGrid.SelectedItem == null) { _status.Text = "Select an address."; return; }
        var path = PropOf(_addrGrid.SelectedItem, "Path"); // "receive/<i>"
        if (!uint.TryParse(path.Split('/').Last(), out var i)) return;
        var priv = WalletKeys.Account(_seed, 0, i).Priv;
        var payload = new byte[34]; payload[0] = _net().WifVersion; priv.CopyTo(payload, 1); payload[33] = 0x01; // compressed
        MessageBox.Show($"Address: {PropOf(_addrGrid.SelectedItem, "Address")}\nPath: {path}\n\nPRIVATE KEY (WIF) — keep secret:\n{Base58.CheckEncode(payload)}", "Private key", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ============================ Contacts ============================

    /// <summary>Public hook so other views (chat/game) can save a discovered peer into the app-wide address book.</summary>
    public void ImportContact(string handle, string pubHex)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => ImportContact(handle, pubHex))); return; }
        AddContact(handle, pubHex);
    }

    private void AddContact(string handle, string pubHex)
    {
        handle = handle.TrimStart('@');
        if (handle.Length == 0) { _status.Text = "Enter a handle."; return; }
        try { var p = Convert.FromHexString(pubHex); if (!Secp256k1.IsValidPoint(p)) throw new Exception(); }
        catch { _status.Text = "Identity public key must be a valid 33-byte compressed key (hex)."; return; }
        _w.Contacts.RemoveAll(c => string.Equals(c.Handle, handle, StringComparison.OrdinalIgnoreCase));
        _w.Contacts.Add(new Contact { Handle = handle, IdentityPub = pubHex.ToLowerInvariant() });
        Save(); Render();
        _status.Text = $"Contact @{handle} saved.";
    }

    // ============================ Claim an identity payment ============================

    private void ClaimIdentityPayment()
    {
        var box = new TextBox { Width = 520, AcceptsReturn = true, Height = 70, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas") };
        var go = new Button { Content = "Derive spend key & rescan", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = "Paste the claim receipt the payer gave you (identity-payment|payerIdPub|invoice|txid):", Foreground = Brushes.Gray });
        sp.Children.Add(box); sp.Children.Add(go);
        var win = new Window { Title = "Claim a payment to my identity", Width = 580, Height = 220, Owner = Window.GetWindow(this), Content = sp };
        go.Click += (_, _) =>
        {
            try
            {
                var parts = box.Text.Trim().Split('|');
                if (parts.Length < 3) { MessageBox.Show("Bad receipt format.", "Claim"); return; }
                var payerPub = Convert.FromHexString(parts[1]); var invoice = parts[2];
                var spendPriv = IdentityPayment.SpendPriv(_identityPriv, payerPub, invoice);
                var oneTimePub = Secp256k1.PublicKeyCompressed(spendPriv);
                // record the derived key as a watched address so the SPV rescan/import can credit the coin
                var addr = Base58.CheckEncode(Prefix(_net().AddressVersion, Hashes.Hash160(oneTimePub)));
                _status.Text = $"Derived your one-time payment address {addr}. Rescanning…";
                RescanRequested?.Invoke();
                MessageBox.Show($"Your one-time payment address is:\n{addr}\n\nIt is controlled by a key only you can derive (identity + invoice). Once the payment confirms it will appear after a rescan / SPV import.", "Identity payment");
                win.Close();
            }
            catch (Exception ex) { MessageBox.Show("Could not claim: " + ex.Message, "Claim"); }
        };
        win.ShowDialog();
    }

    private static byte[] Prefix(byte v, byte[] h20) { var p = new byte[21]; p[0] = v; h20.CopyTo(p, 1); return p; }

    // ============================ Load / broadcast a raw transaction ============================

    private void LoadBroadcastTx()
    {
        var box = new TextBox { Width = 540, AcceptsReturn = true, Height = 160, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas") };
        var go = new Button { Content = "Broadcast", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = "Paste a raw transaction (hex) to broadcast over your BSV peers:", Foreground = Brushes.Gray });
        sp.Children.Add(box); sp.Children.Add(go);
        var win = new Window { Title = "Load / broadcast a transaction", Width = 600, Height = 300, Owner = Window.GetWindow(this), Content = sp };
        go.Click += (_, _) =>
        {
            try
            {
                var raw = Convert.FromHexString(box.Text.Trim());
                var tx = Chain.Deserialize(raw);            // validate it parses
                var node = _node();
                if (node == null || node.PeerCount == 0) { MessageBox.Show("No BSV peers connected.", "Broadcast"); return; }
                node.Broadcast(raw);
                MessageBox.Show("Broadcast tx " + Chain.Txid(tx), "Broadcast");
                win.Close();
            }
            catch (Exception ex) { MessageBox.Show("Invalid transaction: " + ex.Message, "Broadcast"); }
        };
        win.ShowDialog();
    }

    /// <summary>
    /// Card NFTs are 1-sat ON-CHAIN outputs created by real Deal transactions. None are shown unless they
    /// have on-chain provenance — there are no free/phantom cards, so with no sats there are no cards.
    /// </summary>
    public void RefreshCards()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(RefreshCards)); return; }
        _cards.Children.Clear();
        var owned = _vault.Owned();
        _cardsLabel.Text = owned.Count == 0
            ? "Card NFTs (1-sat on-chain outputs sealed to your identity): none yet — they appear as real Deal transactions seal cards to you"
            : $"Card NFTs (1-sat on-chain outputs sealed to your identity): {owned.Count}";
        foreach (var (card, sealedHex) in owned)
            _cards.Children.Add(NftTile(card, sealedHex));
    }

    /// <summary>An NFT tile rendering a real owned card NFT (its provenance is the sealed on-chain blob).</summary>
    private UIElement NftTile(Card card, string sealedHex)
    {
        var face = new Border
        {
            Width = 84, Height = 116, Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(8),
            Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)), BorderThickness = new Thickness(1),
        };
        var fg = card.IsRed ? Brushes.Crimson : Brushes.Black;
        var grid = new Grid();
        grid.Children.Add(new TextBlock { Text = card.RankLabel, FontSize = 18, FontWeight = FontWeights.Bold, Foreground = fg, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 4, 0, 0) });
        grid.Children.Add(new TextBlock { Text = card.Glyph.ToString(), FontSize = 34, Foreground = fg, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(new TextBlock { Text = card.RankLabel, FontSize = 18, FontWeight = FontWeights.Bold, Foreground = fg, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 6, 4) });
        face.Child = grid;
        var prov = _w.NftMints.TryGetValue(sealedHex, out var mtx) ? $"\nminted on-chain in tx {mtx}" : "\n(imported — provenance held by the sender)";
        face.ToolTip = $"Card NFT {card}\nencrypted (sealed to your identity): {sealedHex[..Math.Min(24, sealedHex.Length)]}…{prov}\nClick to copy the sealed blob.";
        face.MouseLeftButtonUp += (_, _) => CopyToClipboard(sealedHex, $"NFT {card} sealed blob copied.");
        var wrap = new StackPanel { Width = 94 };
        wrap.Children.Add(face);
        wrap.Children.Add(new TextBlock { Text = $"NFT · {card}", Foreground = SubInk, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });
        return wrap;
    }
}
