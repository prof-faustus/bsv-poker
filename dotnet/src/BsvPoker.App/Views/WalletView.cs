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
    private sealed class Tx { public string Time { get; set; } = ""; public string Type { get; set; } = ""; public long Amount { get; set; } [System.Text.Json.Serialization.JsonIgnore] public long Balance { get; set; } public string Memo { get; set; } = ""; public int Height { get; set; } }
    private sealed class PendingRow { public string Status { get; set; } = ""; public string Amount { get; set; } = ""; public string Memo { get; set; } = ""; public string Txid { get; set; } = ""; }
    // A wallet coin. The SPV proof is SAVED WITH THE COIN (no side files): the raw funding tx plus the
    // merkle proof (branch + index + block hash) to the block that mined it. STATE is derived, never a
    // fabricated flag: CONFIRMED only when that saved proof RE-VERIFIES against headers we validated;
    // UNCONFIRMED = a valid coin received but not yet mined; DoubleSpent = a conflicting spend was proven
    // (invalid). A coin is NEVER deleted regardless of state.
    private sealed class UtxoRec { public string Txid { get; set; } = ""; public uint Vout { get; set; } public long Value { get; set; } public uint KeyChain { get; set; } public uint KeyIndex { get; set; } public bool Spent { get; set; } public bool Confirmed { get; set; } public bool Frozen { get; set; } public bool WatchOnly { get; set; } public bool DoubleSpent { get; set; } public string RawTxHex { get; set; } = ""; public string MerkleBlockHex { get; set; } = ""; public string EnvelopeWire { get; set; } = ""; }
    private sealed class SendRec { public string Txid { get; set; } = ""; public long Amount { get; set; } public long Fee { get; set; } public string To { get; set; } = ""; public string Time { get; set; } = ""; public string RawHex { get; set; } = ""; }
    private sealed class Contact { public string Handle { get; set; } = ""; public string IdentityPub { get; set; } = ""; public string Note { get; set; } = ""; public string DisplayName { get; set; } = ""; public string Email { get; set; } = ""; public bool Verified { get; set; } }
    private sealed class PayRequest { public string Address { get; set; } = ""; public long Amount { get; set; } public string Memo { get; set; } = ""; public string Time { get; set; } = ""; public string Expires { get; set; } = ""; }
    private sealed class ChatGroupRec { public string Name { get; set; } = ""; public List<string> MemberPubs { get; set; } = new(); }   // a named broadcast-encryption group
    private sealed class File_
    {
        public string Seed { get; set; } = "";
        public int RecvIndex { get; set; }
        public List<UtxoRec> Utxos { get; set; } = new();
        public List<Tx> History { get; set; } = new();
        public List<Tx> Activity { get; set; } = new();                    // LIVE log of every action this wallet did (chat/bet/deal/identity/sent/received) — shown immediately, before SPV confirms
        public List<SendRec> Sends { get; set; } = new();                  // real outgoing broadcasts (for history)
        public Dictionary<string, string> TxLabels { get; set; } = new();  // txid -> user label
        public Dictionary<string, string> AddrLabels { get; set; } = new();// address -> user label
        public List<Contact> Contacts { get; set; } = new();               // handle -> identity pubkey
        public List<ChatGroupRec> Groups { get; set; } = new();            // named chat groups (broadcast-encryption members)
        public List<PayRequest> Requests { get; set; } = new();            // payment requests we issued
        public string Handle { get; set; } = "";                           // this wallet's own identity handle
        public List<string> SweptKeys { get; set; } = new();               // external private keys (hex) being swept in
        public Dictionary<string, string> CoinLabels { get; set; } = new();// "txid:vout" -> user label
        public List<Vault> Vaults { get; set; } = new();                   // 2-of-2 multisig vaults (with recovery)
        public Dictionary<string, string> NftMints { get; set; } = new();  // sealedHex -> on-chain mint txid (provenance)
        public List<string> WatchAddresses { get; set; } = new();          // watch-only addresses (balance only, never spendable)
        public Registration? Identity { get; set; }                        // the registered, self-signed identity (null until registered)
    }
    /// <summary>A self-issued identity: details the user registered, signed by their identity key (verifiable).</summary>
    private sealed class Registration
    {
        public string DisplayName { get; set; } = "";
        public string Pseudonym { get; set; } = "";
        public string Email { get; set; } = "";
        public string Country { get; set; } = "";
        public string Website { get; set; } = "";
        public string Bio { get; set; } = "";
        public string IdentityPub { get; set; } = "";
        public string Signature { get; set; } = "";    // identityPriv over the canonical fields
        public string CreatedAt { get; set; } = "";
        public string OnChainTxid { get; set; } = "";   // the identity NFT transaction; EMPTY = draft only (NOT a real identity)
        public bool IsOnChain => !string.IsNullOrEmpty(OnChainTxid);
        // every field is signed (v2) so the whole profile is verifiable, not just name/email.
        public string Canonical() => $"bsvpoker-identity-v2|{DisplayName}|{Pseudonym}|{Email}|{Country}|{Website}|{Bio}|{IdentityPub}|{CreatedAt}";
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
    /// <summary>The most recent on-chain hand tape (Hold'em-vs-bot) — handed to the Replay tab so any hand the
    /// wallet plays can be stepped through move by move. Null until a hand has been played.</summary>
    public BsvPoker.Core.Games.OnChainHandTape.Tape? LastTape { get; private set; }

    /// <summary>True once the seed is in memory and the wallet can derive keys (needed before the SPV filter is built).</summary>
    public bool IsUnlocked => !_locked;
    /// <summary>True when the wallet holds spendable confirmed coin — nothing (play / on-chain identity) happens until funded.</summary>
    public bool IsFunded => !_locked && Balance > 0;
    /// <summary>True once the identity has been written on-chain (a confirmed NFT tx). On-chain is an OPTIONAL
    /// permanence upgrade — see <see cref="HasIdentity"/> for whether the player has set their identity at all.</summary>
    public bool HasOnChainIdentity => _w.Identity is { IsOnChain: true };
    /// <summary>True once the player has SET their identity (a self-signed, fixed handle) — whether or not it has
    /// also been made permanent on-chain. This is all that is required to PLAY (a game is played by identities).</summary>
    public bool HasIdentity => _w.Identity is { Pseudonym.Length: > 0 };
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
    private CardVault _vault;   // re-created from the OPENED wallet's seed after Load (sealed to the wallet's identity)
    /// <summary>The card vault sealed to the OPENED wallet's identity — the rest of the app uses this, not a profile vault.</summary>
    public CardVault WalletVault => _vault;
    private readonly WrapPanel _cards = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _cardsLabel = new() { Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 14, 0, 2), Text = "My cards (NFTs)" };

    // ---- ElectrumSV-style tabbed UI state ----
    private KeyRing _ring = null!;                                  // hash-chained Type-42 key ring over the master seed
    private bool _freshWallet;                                      // true when Load() had to create a brand-new wallet
    private readonly TabControl _tabs = new() { Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x12)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
    private TabItem? _identityTab;   // rebuilt on Render so registration status is never stale
    private ContentControl? _idCard;
    private TabItem? _nftTab;        // rebuilt on Render so newly-minted/owned NFTs show without a restart
    private readonly TextBox _sendPayTo = new() { Width = 520, FontFamily = new FontFamily("Consolas"), AcceptsReturn = true, Height = 56, TextWrapping = TextWrapping.Wrap, ToolTip = "An address, an identity handle (@bob), an identity pubkey (hex), or a bitcoin:/pay: URI" };
    private readonly TextBox _sendLabel = new() { Width = 520 };
    private readonly ComboBox _feeRate = new() { Width = 200 };
    private readonly TextBlock _sendStatus = new() { Foreground = Brushes.Gainsboro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
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
    private readonly TextBlock _fundInfo = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), TextWrapping = TextWrapping.Wrap, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
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

    // The identity (Base ID) is DERIVED FROM THE SELECTED WALLET'S SEED — never assumed/injected. Whatever wallet
    // you open IS your identity (Type-42 "bsvpoker/identity" sub-key of that wallet's seed). Empty until a wallet
    // is opened. Exposed so the rest of the app uses the OPENED wallet's identity, not an auto-profile key.
    private byte[] _identityPriv => _seed.Length == 32 ? Type42.UniqueKey(_seed, "bsvpoker/identity") : Array.Empty<byte>();
    private byte[] _identityPub => _seed.Length == 32 ? Secp256k1.PublicKeyCompressed(_identityPriv) : Array.Empty<byte>();
    /// <summary>The opened wallet's identity (Base ID) — for the rest of the app to use instead of any profile key.</summary>
    public byte[] WalletIdentityPriv => _identityPriv;
    public byte[] WalletIdentityPub => _identityPub;

    /// <summary>Raised when a chat message addressed to me is found ON-CHAIN during a history sync (this is how an
    /// OFFLINE message is delivered when I come back online). Args: (senderPubHex, text). MainWindow forwards it
    /// to the chat view. Deduped by txid so a re-sync never re-delivers.</summary>
    public event Action<string, string>? OnChatReceived;
    private readonly HashSet<string> _deliveredChat = new();   // "txid:vout" already surfaced to chat
    private bool _historyBusy;     // re-entrancy guard: never let two history syncs overlap (they peg the CPU)
    private bool _discoverBusy;    // re-entrancy guard for the SPV discover sync
    /// <summary>A receive address of this wallet (for the bot to refund to). Empty if locked.</summary>
    public string PublicReceiveAddress() => _seed.Length == 32 ? ReceiveAddress() : "";

    public WalletView(string dataDir, CardVault vault, Func<BsvNode?> node, Func<HeaderStore?> store, Func<NetworkParams> net, byte[] identityPriv, byte[] identityPub)
    {
        _vault = vault; _node = node; _store = store; _net = net;
        // identityPriv/identityPub args are IGNORED — identity now comes from the selected wallet's seed (above).
        Background = WinBg;                                  // ElectrumSVP-style LIGHT theme (not dark)
        Foreground = Ink;
        Directory.CreateDirectory(dataDir);
        _dataDir = dataDir;
        _path = AccountPath(0);   // safe default until the user selects a wallet (selection is deferred to Loaded)
        // ElectrumSVP NEVER assumes a wallet: the user MUST select or create a wallet — we do not auto-load any
        // default. No wallet selected = no program. SELECTION IS DEFERRED to AFTER the main window is shown
        // (MainWindow calls SelectWalletAtStartup() from its Loaded handler) — a modal ShowDialog() called during
        // construction, before the window is up, does NOT run its message loop and returns instantly (proven by
        // the startup trace), which dumped the user straight into an empty wallet. Selecting after Loaded makes
        // the "Select your wallet" dialog block properly and be the genuine FIRST thing on screen.
        VaultBackup();   // EVERY RUN: a fresh immutable (read-only) backup in claude\backups

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
        _identityTab = new TabItem { Header = "Identity", Content = BuildIdentityTab() };
        _tabs.Items.Add(_identityTab);
        _nftTab = new TabItem { Header = "NFTs", Content = BuildNftTab() };
        _tabs.Items.Add(_nftTab);
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

        // CORRECT ORDER (the user's rule): Wallet → Fund → Identity (on-chain, needs coins to sign) → Game.
        // We do NOT register an identity at startup — an identity is an on-chain FUNDED transaction, so it is
        // IMPOSSIBLE before the wallet has coins. Startup only LOADS the wallet and FINDS its coins; the user
        // registers the identity later (Identity tab), once funded; and a game requires that on-chain identity.
        Loaded += (_, _) =>
        {
            if (_freshWallet) { _freshWallet = false; AccountWizard(); }   // a brand-new wallet still needs a seed+password
            // FIND THE COINS automatically (you need funds BEFORE an identity). PRIMARY = fast SPV-server lookup
            // (<15s, address-indexed); P2P block scan is the backup. Runs now and keeps running — no button ever.
            // GUARD: only with a loaded seed — before a wallet is selected (deferred to MainWindow.Loaded) there is
            // no seed, and deriving addresses from a 0-byte seed throws ("seed must be 32 bytes").
            bool Ready() => !_locked && _seed.Length == 32;
            if (Ready()) { _ = SpvServerDiscoverAsync(); _ = FetchServerHistoryAsync(); RescanRequested?.Invoke(); }
            var spvTick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            spvTick.Tick += (_, _) => { if (Ready()) { _ = SpvServerDiscoverAsync(); _ = FetchServerHistoryAsync(); RebroadcastPending(); } };   // fast SPV + full history + re-push pending every 15s
            spvTick.Start();
            var fundTick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
            fundTick.Tick += (_, _) => { if (Ready()) RescanRequested?.Invoke(); };      // P2P backup, slower
            fundTick.Start();
        };
    }

    // ---- DARK theme palette (matches the poker app; ElectrumSVP STRUCTURE on a dark skin) ----
    private static readonly SolidColorBrush WinBg = new(Color.FromRgb(0x0D, 0x0D, 0x0D));   // window
    private static readonly SolidColorBrush PanelBg = new(Color.FromRgb(0x16, 0x16, 0x16)); // panels
    private static readonly SolidColorBrush FieldBg = new(Color.FromRgb(0x1E, 0x1E, 0x1E)); // inputs
    private static readonly SolidColorBrush Ink = new(Color.FromRgb(0xEC, 0xEC, 0xEC));     // text
    private static readonly SolidColorBrush SubInk = new(Color.FromRgb(0xCF, 0xCF, 0xCF));  // labels (bright, high-contrast on dark)
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
        // The DEFAULT WPF tab template draws a LIGHT strip and ignores TabItem.Background — which left our light
        // text on a light header (white-on-white). Replace the template with a Border that actually honours our
        // dark Background + light Foreground, so the header is always readable.
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.BackgroundProperty, new System.Windows.TemplateBindingExtension(TabItem.BackgroundProperty));
        bd.SetValue(Border.BorderBrushProperty, new System.Windows.TemplateBindingExtension(TabItem.BorderBrushProperty));
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(1, 1, 1, 0));
        bd.SetValue(Border.MarginProperty, new Thickness(0, 0, 2, 0));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        cp.SetValue(ContentPresenter.MarginProperty, new Thickness(13, 7, 13, 7));
        cp.SetValue(TextBlock.ForegroundProperty, new System.Windows.TemplateBindingExtension(TabItem.ForegroundProperty));
        bd.AppendChild(cp);
        st.Setters.Add(new Setter(TabItem.TemplateProperty, new ControlTemplate(typeof(TabItem)) { VisualTree = bd }));
        st.Setters.Add(new Setter(TabItem.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A))));
        st.Setters.Add(new Setter(TabItem.ForegroundProperty, Ink));   // light text on a dark header — readable
        st.Setters.Add(new Setter(TabItem.BorderBrushProperty, Line));
        st.Setters.Add(new Setter(TabItem.FontSizeProperty, 13.0));
        var sel = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
        sel.Setters.Add(new Setter(TabItem.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x2E))));
        sel.Setters.Add(new Setter(TabItem.ForegroundProperty, Accent));
        sel.Setters.Add(new Setter(TabItem.FontWeightProperty, FontWeights.Bold));
        st.Triggers.Add(sel);
        var hov = new Trigger { Property = TabItem.IsMouseOverProperty, Value = true };
        hov.Setters.Add(new Setter(TabItem.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26))));
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
        // Foreground/Background come from the app-wide dark MenuItem style (bright text on a dark menu surface) —
        // do NOT force black here (black on the dark dropdown was invisible).
        MenuItem I(string h, Action a) { var mi = new MenuItem { Header = h }; mi.Click += (_, _) => { try { a(); } catch (Exception ex) { _status.Text = ex.Message; } }; return mi; }

        var file = M("_File");
        file.Items.Add(I("_Open wallet file…", OpenWalletFileDialog));
        file.Items.Add(I("New _wallet file…", NewWalletFileDialog));
        file.Items.Add(new Separator());
        file.Items.Add(I("_New / Restore…", () => AccountWizard()));
        file.Items.Add(I("Open (restore from _seed)…", Restore));
        file.Items.Add(I("_Save a copy of the seed backup…", BackupSeedToFile));
        file.Items.Add(I("Save a copy of the _wallet file…", SaveWalletCopy));
        file.Items.Add(new Separator());
        file.Items.Add(I("_Quit", () => Window.GetWindow(this)?.Close()));
        menu.Items.Add(file);

        var wallet = M("_Wallet");
        wallet.Items.Add(I("_Information (master public key)…", () => { if (Guard()) MessageBox.Show(Convert.ToHexString(_identityPub).ToLowerInvariant(), "Wallet information — identity / master public key"); }));
        wallet.Items.Add(I("_Password (encrypt keys)…", SetPassword));
        wallet.Items.Add(I("_Unlock…", () => { Unlock(); Render(); }));
        // (no rescan menu item — coin discovery is ALWAYS automatic; the user never asks for it)
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
        tools.Items.Add(I("_Network / connection diagnostics…", NetworkDialog));
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
        _pendingGrid.Height = 410;
        sp.Children.Add(_pendingGrid);
        // Click a transaction → FULL details (inputs/outputs/value/txid), like ElectrumSVP. Double-click a row or
        // select it and press the button. (This grid previously did nothing on click — useless.)
        void ShowSelected() { if (_pendingGrid.SelectedItem is PendingRow r && r.Txid.Length > 0) TxDetails(r.Txid); }
        _pendingGrid.MouseDoubleClick += (_, _) => ShowSelected();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var details = Btn("Transaction details…"); details.Click += (_, _) => ShowSelected();
        var copyTxid = Btn("Copy txid"); copyTxid.Click += (_, _) => { if (_pendingGrid.SelectedItem is PendingRow r) CopyToClipboard(r.Txid, "txid copied."); };
        var label = Btn("Set label…"); label.Click += (_, _) => { if (_pendingGrid.SelectedItem is PendingRow r) SetTxLabel(r.Txid); };
        var rebroadcast = Btn("Rebroadcast pending"); rebroadcast.Click += (_, _) => { RebroadcastPending(); _status.Text = "Re-sent all pending transactions to the network — they should confirm once a miner includes them."; };
        row.Children.Add(details); row.Children.Add(copyTxid); row.Children.Add(label); row.Children.Add(rebroadcast);
        sp.Children.Add(row);
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
        // STEP 0: a welcome / splash page (ElectrumSVP shows one — we do every step, never skip).
        WizardWelcome();
        // DO NOT force identity registration here. The correct order is Wallet → Fund → Identity (registration is
        // an ON-CHAIN funded transaction, so it is IMPOSSIBLE on a brand-new wallet with zero funds). The wallet
        // opens unregistered; the user funds it, then registers (Identity tab) when ready. Forcing register first
        // trapped the user with a dialog they could never complete.

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

    private static bool ValidEmail(string e) => System.Text.RegularExpressions.Regex.IsMatch(e ?? "", @"^[^@\s]+@[^@\s]+\.[^@\s]{2,}$");

    /// <summary>Export my registered identity as a portable, self-signed certificate (JSON) others can verify.</summary>
    private void ExportIdentityCert()
    {
        if (_w.Identity == null) { _status.Text = "Set up your identity first."; RegisterDialog(); return; }
        var json = JsonSerializer.Serialize(_w.Identity, new JsonSerializerOptions { WriteIndented = true });
        var box = new TextBox { Text = json, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), Height = 220, Width = 520, Background = FieldBg, Foreground = Accent, BorderBrush = Line, BorderThickness = new Thickness(1) };
        var copy = new Button { Content = "Copy", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        copy.Click += (_, _) => CopyToClipboard(json, "Identity certificate copied.");
        var win = new Window { Title = "My identity certificate (signed)", Width = 580, Height = 340, Owner = Window.GetWindow(this), Background = WinBg, Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(12), Children = { new TextBlock { Text = "Share this — anyone can verify it was signed by your identity key:", Foreground = Ink }, box, copy } } } };
        win.ShowDialog();
    }

    /// <summary>Verify another person's identity certificate: the signature must check out against the identity
    /// public key embedded in it — proving the name/pseudonym/email really belong to that key.</summary>
    private void VerifyIdentityCert()
    {
        var box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), Height = 180, Width = 520 }; ThemeOne(box);
        var go = new Button { Content = "Verify", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        var result = new TextBlock { Margin = new Thickness(0, 8, 0, 0), FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap };
        var win = new Window { Title = "Verify an identity certificate", Width = 580, Height = 360, Owner = Window.GetWindow(this), Background = WinBg, Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(12), Children = { new TextBlock { Text = "Paste someone's identity certificate (JSON):", Foreground = Ink }, box, go, result } } } };
        go.Click += (_, _) =>
        {
            try
            {
                var r = JsonSerializer.Deserialize<Registration>(box.Text.Trim());
                if (r == null || r.IdentityPub.Length == 0) { result.Text = "Not a certificate."; result.Foreground = Brushes.IndianRed; return; }
                var pub = Convert.FromHexString(r.IdentityPub);
                bool ok = WalletExtras.VerifyMessage(pub, r.Canonical(), r.Signature);
                result.Text = ok ? $"✔ VALID — {r.DisplayName} (@{r.Pseudonym}), {r.Email}\nis bound to identity key {r.IdentityPub[..16]}…" : "✖ INVALID — the signature does not match the identity key. Do NOT trust this identity.";
                result.Foreground = ok ? Accent : Brushes.IndianRed;
                if (ok && !_w.Contacts.Any(c => string.Equals(c.IdentityPub, r.IdentityPub, StringComparison.OrdinalIgnoreCase)))
                    result.Text += "\n(Tip: add them in Contacts as @" + r.Pseudonym + ")";
            }
            catch (Exception ex) { result.Text = "Could not parse/verify: " + ex.Message; result.Foreground = Brushes.IndianRed; }
        };
        win.ShowDialog();
    }

    /// <summary>Edit ONLY the optional details (country / website / bio) of an already-on-chain identity. The
    /// name, @handle, email and key are PERMANENT and immutable. Re-signs and updates the local + cached record;
    /// NO new on-chain transaction, NO fee. This is the ONLY editing allowed once registered.</summary>
    private void EditOptionalDetails()
    {
        if (_w.Identity is not { } id) return;
        var country = new TextBox { Width = 320, Text = id.Country }; ThemeOne(country);
        var website = new TextBox { Width = 320, Text = id.Website }; ThemeOne(website);
        var bio = new TextBox { Width = 320, Height = 50, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Text = id.Bio }; ThemeOne(bio);
        var save = new Button { Content = "Save details", Margin = new Thickness(0, 12, 8, 0), Padding = new Thickness(16, 6, 16, 6), IsDefault = true };
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock { Text = "🔒 Your identity is permanent", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Ink });
        sp.Children.Add(new TextBlock { Text = $"{id.DisplayName}  (@{id.Pseudonym})  ·  {id.Email}\nSet on-chain — this can NEVER be changed. You may update only the optional details below.", Foreground = SubInk, TextWrapping = TextWrapping.Wrap, MaxWidth = 340, Margin = new Thickness(0, 4, 0, 8) });
        sp.Children.Add(new TextBlock { Text = "Country (optional)", Foreground = SubInk }); sp.Children.Add(country);
        sp.Children.Add(new TextBlock { Text = "Website (optional)", Foreground = SubInk, Margin = new Thickness(0, 6, 0, 0) }); sp.Children.Add(website);
        sp.Children.Add(new TextBlock { Text = "Bio (optional)", Foreground = SubInk, Margin = new Thickness(0, 6, 0, 0) }); sp.Children.Add(bio);
        sp.Children.Add(save);
        var win = new Window { Title = "Edit optional details", Width = 400, Height = 430, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this), Background = WinBg, Content = new ScrollViewer { Content = sp } };
        save.Click += (_, _) =>
        {
            id.Country = country.Text.Trim(); id.Website = website.Text.Trim(); id.Bio = bio.Text.Trim();
            try { id.Signature = WalletExtras.SignMessage(_identityPriv, id.Canonical()); } catch { }
            Save(); PersistIdentityToCache(); Render();
            win.Close();
        };
        win.ShowDialog();
    }

    /// <summary>
    /// Mandatory identity REGISTRATION: the user fills in display name + pseudonym + email (validated) + optional
    /// country; we bind it to the Base ID key by SELF-SIGNING the fields (a verifiable identity certificate) and
    /// store it. Until this is done nothing in the wallet works. The pseudonym becomes the handle everything uses.
    /// </summary>
    private void RegisterDialog()
    {
        // YOUR IDENTITY IS PERMANENT. Once it is on-chain it can NEVER be changed or re-registered — it is who you
        // are (like a birth certificate). Any attempt to "register" again is redirected to editing only the
        // OPTIONAL details (country / website / bio); the name, @handle, email and key are immutable, forever.
        if (HasOnChainIdentity) { EditOptionalDetails(); return; }
        var name = new TextBox { Width = 320 }; ThemeOne(name);
        var pseud = new TextBox { Width = 320 }; ThemeOne(pseud);
        var email = new TextBox { Width = 320 }; ThemeOne(email);
        var country = new TextBox { Width = 320 }; ThemeOne(country);
        var website = new TextBox { Width = 320 }; ThemeOne(website);
        var bio = new TextBox { Width = 320, Height = 50, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap }; ThemeOne(bio);
        if (_w.Identity != null) { name.Text = _w.Identity.DisplayName; pseud.Text = _w.Identity.Pseudonym; email.Text = _w.Identity.Email; country.Text = _w.Identity.Country; website.Text = _w.Identity.Website; bio.Text = _w.Identity.Bio; }
        var note = new TextBlock { Foreground = SubInk, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 340 };
        var ok = new Button { Content = "Set up identity", Margin = new Thickness(0, 12, 8, 0), Padding = new Thickness(16, 6, 16, 6), IsEnabled = false };
        void Recheck()
        {
            bool nm = name.Text.Trim().Length > 0, ps = pseud.Text.Trim().Length > 0, em = ValidEmail(email.Text.Trim());
            note.Text = !nm ? "Enter your display name." : !ps ? "Choose a pseudonym/handle." : !em ? "Enter a valid email (name@domain.tld)." : "Ready — this is signed by your identity key.";
            note.Foreground = (nm && ps && em) ? Accent : Brushes.IndianRed;
            ok.IsEnabled = nm && ps && em;
        }
        name.TextChanged += (_, _) => Recheck(); pseud.TextChanged += (_, _) => Recheck(); email.TextChanged += (_, _) => Recheck();
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock { Text = "Set up your identity", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Ink });
        sp.Children.Add(new TextBlock { Text = "This creates your identity — everything (payments, chat, contacts, your bots, the game) is bound to it. The details are self-signed by your identity key so the claim is verifiable. Until your identity exists, this wallet can only receive funds.", Foreground = SubInk, TextWrapping = TextWrapping.Wrap, MaxWidth = 340, Margin = new Thickness(0, 4, 0, 8) });
        sp.Children.Add(new TextBlock { Text = "Display name *", Foreground = SubInk }); sp.Children.Add(name);
        sp.Children.Add(new TextBlock { Text = "Pseudonym / handle *", Foreground = SubInk, Margin = new Thickness(0, 6, 0, 0) }); sp.Children.Add(pseud);
        sp.Children.Add(new TextBlock { Text = "Email *", Foreground = SubInk, Margin = new Thickness(0, 6, 0, 0) }); sp.Children.Add(email);
        sp.Children.Add(new TextBlock { Text = "Country (optional)", Foreground = SubInk, Margin = new Thickness(0, 6, 0, 0) }); sp.Children.Add(country);
        sp.Children.Add(new TextBlock { Text = "Website (optional)", Foreground = SubInk, Margin = new Thickness(0, 6, 0, 0) }); sp.Children.Add(website);
        sp.Children.Add(new TextBlock { Text = "Bio (optional)", Foreground = SubInk, Margin = new Thickness(0, 6, 0, 0) }); sp.Children.Add(bio);
        sp.Children.Add(note);
        sp.Children.Add(ok);
        var win = new Window { Title = "Set up your identity", Width = 400, Height = 620, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this), Background = WinBg, Content = new ScrollViewer { Content = sp } };
        Recheck();
        ok.Click += async (_, _) =>
        {
            try
            {
                // ALREADY registered on-chain? DO NOT broadcast (and pay for) another identity tx — that is what
                // drained the wallet to zero across repeated "Register" clicks. Update the local profile fields,
                // re-sign, save. No new transaction, no fee. (Your on-chain identity NFT already exists.)
                if (HasOnChainIdentity && _w.Identity is { } cur)
                {
                    cur.DisplayName = name.Text.Trim(); cur.Pseudonym = pseud.Text.Trim().TrimStart('@'); cur.Email = email.Text.Trim();
                    cur.Country = country.Text.Trim(); cur.Website = website.Text.Trim(); cur.Bio = bio.Text.Trim();
                    cur.Signature = WalletExtras.SignMessage(_identityPriv, cur.Canonical());
                    _w.Handle = cur.Pseudonym; Save(); Render();
                    MessageBox.Show($"Your identity is already ON-CHAIN as @{cur.Pseudonym} (tx {cur.OnChainTxid}). Your profile was updated — NO new transaction and NO fee. You can play now.", "Identity already on-chain");
                    win.Close(); return;
                }
                var reg = new Registration
                {
                    DisplayName = name.Text.Trim(), Pseudonym = pseud.Text.Trim().TrimStart('@'), Email = email.Text.Trim(),
                    Country = country.Text.Trim(), Website = website.Text.Trim(), Bio = bio.Text.Trim(),
                    IdentityPub = Convert.ToHexString(_identityPub).ToLowerInvariant(),
                    CreatedAt = DateTime.UtcNow.ToString("o"),
                };
                reg.Signature = WalletExtras.SignMessage(_identityPriv, reg.Canonical());   // attestation over the claim

                // YOUR IDENTITY IS A ONE-SAT ON-CHAIN TOKEN — ALWAYS. A token is ONE sat. ONE. Not two. The identity
                // NFT output is 1 sat and there is NO extra fee on top of it (a token is not doubled). Everything in
                // this wallet is on-chain; nothing is free; a draft that never broadcast is NOT an identity. So
                // registration REQUIRES the wallet to hold the one sat. With no funds we do NOT fabricate a free local
                // identity — fund a sat first (regtest self-funds). The identity exists only once the tx is on-chain.
                const long idFee = 0;       // a token is ONE sat — the 1-sat NFT output IS the cost; never doubled
                if (Balance < 1)
                {
                    MessageBox.Show("Your identity is a ONE-SAT on-chain token (1 sat — never two). Nothing here is free or off-chain. Add one satoshi to this wallet (Receive tab → Fund (regtest) on regtest), then set it up and it is written on-chain, permanent and immutable.", "Fund one sat — identity is on-chain");
                    win.Close(); return;
                }
                var attPriv = Type42.UniqueKey(_seed, "bsvpoker/identity/attestation");
                var idScript = OnChainIdentity.BuildScript(_identityPub, attPriv, reg.Pseudonym, reg.Email);
                var (raw, status) = FundTx(idScript, 1, idFee);
                if (raw == null) { MessageBox.Show("Could not build the on-chain identity transaction: " + status, "Identity transaction failed"); win.Close(); return; }
                var (bok, binfo) = await BroadcastEverywhere(raw);
                if (!bok)
                {
                    MessageBox.Show("The identity transaction could not be broadcast to the network: " + binfo + "\n\nNO identity was set — an identity only counts once it is on-chain. Try again.", "Not on-chain");
                    win.Close(); return;
                }
                // ON-CHAIN CONFIRMED — only NOW does the identity exist.
                reg.OnChainTxid = binfo.Length >= 64 ? binfo : Chain.Txid(Chain.Deserialize(raw));
                _w.Identity = reg; _w.Handle = reg.Pseudonym;
                _w.NftMints[reg.OnChainTxid] = reg.OnChainTxid;
                AppendTx("identity", -1, $"Identity NFT set ON-CHAIN (1-sat token) — tx {reg.OnChainTxid}");
                Save(); PersistIdentityToCache(); Render();
                MessageBox.Show($"Identity set ON-CHAIN as @{reg.Pseudonym} (NFT tx {reg.OnChainTxid[..Math.Min(16, reg.OnChainTxid.Length)]}…). This is permanent and immutable. You can play now.", "Identity is on-chain");
                win.Close();
            }
            catch (Exception ex) { MessageBox.Show("Could not set up identity: " + ex.Message); }
        };
        win.ShowDialog();
    }

    /// <summary>ONE-TIME ENROLLMENT, at JOIN. If this wallet has no identity, run the registration flow ONCE
    /// (collect the handle, fund the 1-sat token, write it on-chain). Returns whether an identity now exists.
    /// This is the ONLY place registration is ever offered — the first time you join a game, once, ever. Once
    /// registered it is permanent and this never appears again.</summary>
    public bool EnsureRegistered()
    {
        if (!Dispatcher.CheckAccess()) return (bool)Dispatcher.Invoke(new Func<bool>(EnsureRegistered));
        if (HasIdentity) return true;                       // already registered — never ask again
        if (_locked || _seed.Length != 32) { _status.Text = "Open your wallet to enroll."; return false; }
        RegisterDialog();                                   // the one-time enrollment (modal): handle → fund 1 sat → on-chain
        return HasIdentity;
    }

    /// <summary>REGTEST SELF-FUND: a brand-new wallet has no peers to receive from, so on the local regtest chain
    /// it mines its OWN first coins — a real trivial-PoW block whose tx pays this wallet, verified by the same SPV
    /// merkle-proof + PoW path as any mined coin (no fake/optimistic credit). The coin is then spendable to register
    /// the identity ON-CHAIN. Regtest only — mainnet/testnet have real coins and never self-fund.</summary>
    private void RegtestSelfFund()
    {
        if (_net().Network != BsvNetwork.Regtest) { MessageBox.Show("Self-fund is only for the local Regtest chain. On Testnet use a faucet; on Mainnet receive real coins.", "Regtest only"); return; }
        if (_seed.Length != 32) { _status.Text = "Unlock your wallet first."; return; }
        try
        {
            var recv = WalletKeys.Account(_seed, 0, 0).Pub;                 // the wallet's spend key at chain0/index0
            var f = RegtestFunder.Fund(recv, 1_000_000, new byte[32]);      // mine a real regtest block paying us
            var chain = new HeadersChain(); chain.AddGenesis(f.Header);
            var utxo = SpvFunding.Verify(new SpvFunding.Proof(f.Tx, f.Vout, f.Header.HashHex(), f.Branch, f.TxIndex), chain, recv, 0, 0);
            if (utxo == null) { MessageBox.Show("The regtest funding did not verify.", "Self-fund failed"); return; }
            if (_w.Utxos.Any(u => u.Txid == utxo.Txid && u.Vout == utxo.Vout)) { _status.Text = "Already funded (regtest)."; return; }
            // credit the REAL, SPV-proven coin (its raw funding tx saved so it can be spent to register on-chain)
            _w.Utxos.Add(new UtxoRec
            {
                Txid = utxo.Txid, Vout = utxo.Vout, Value = utxo.Value, KeyChain = 0, KeyIndex = 0, Confirmed = true,
                RawTxHex = Convert.ToHexString(Chain.Serialize(f.Tx)).ToLowerInvariant(),
            });
            AppendTx("received", utxo.Value, $"Regtest self-fund — {utxo.Value:N0} sat (mined block {f.Header.HashHex()[..12]}…)");
            Save(); VaultBackup(); Render();
            MessageBox.Show($"Regtest self-fund: {utxo.Value:N0} sat mined to your wallet (a real local-chain block). You can now set up your identity ON-CHAIN (Identity tab) and then play.", "Funded (regtest)");
        }
        catch (Exception ex) { MessageBox.Show("Self-fund failed: " + ex.Message, "Self-fund failed"); }
    }

    /// <summary>The wizard welcome/splash page — shown before any choice (we never skip a step).</summary>
    private void WizardWelcome()
    {
        var sp = new StackPanel { Margin = new Thickness(24) };
        sp.Children.Add(new TextBlock { Text = "BSV Wallet", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Ink });
        sp.Children.Add(new TextBlock { Text = "A standalone SPV BSV wallet with identity (Base ID + Type-42), encrypted card NFTs, 2-of-2 vaults with recovery, and pure peer-to-peer play — modelled on ElectrumSVP and built far beyond it. Your keys are encrypted and password-protected; everything is on-chain and server-less.", Foreground = SubInk, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(0, 8, 0, 12) });
        var go = new Button { Content = "Get started", Padding = new Thickness(14, 8, 14, 8), HorizontalAlignment = HorizontalAlignment.Left, IsDefault = true };
        sp.Children.Add(go);
        var win = new Window { Title = "Welcome", Width = 480, Height = 280, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this), Background = WinBg, Content = new ScrollViewer { Content = sp } };
        go.Click += (_, _) => win.Close();
        win.ShowDialog();
    }

    /// <summary>The Standard new-wallet flow: show the seed for backup → confirm it was written down → set a
    /// password. Mirrors ElectrumSVP's create-seed / confirm-seed / password pages (BSV-native seed, not BIP39).</summary>
    private void StandardSeedFlow()
    {
        // STEP 1 (ElectrumSVP order): set the wallet password first (encrypt the keys at rest).
        SetPassword();
        var seed = WalletKeys.SeedToBackup(_seed);
        // page 1: show the seed
        var seedBox = new TextBox { Text = seed, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), Background = FieldBg, Foreground = Accent, BorderBrush = Line, BorderThickness = new Thickness(1), Padding = new Thickness(6), Width = 420 };
        var copySeed = new Button { Content = "Copy seed", Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        copySeed.Click += (_, _) => CopyToClipboard(seed, "Seed copied — store it somewhere safe.");
        var next = new Button { Content = "Continue", Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(12, 6, 12, 6) };   // never disabled — the wallet already exists
        var sp1 = new StackPanel { Margin = new Thickness(16) };
        sp1.Children.Add(new TextBlock { Text = "Back up your wallet seed", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Ink });
        sp1.Children.Add(new TextBlock { Text = "This single seed recovers your entire wallet. Anyone with it controls your coins. Copy it and store it safely. (Your wallet is already created — this is just your backup.)", Foreground = SubInk, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(0, 4, 0, 8) });
        sp1.Children.Add(seedBox); sp1.Children.Add(copySeed); sp1.Children.Add(next);
        var w1 = new Window { Title = "New wallet — back up your seed", Width = 480, Height = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this), Background = WinBg, Content = new ScrollViewer { Content = sp1 } };
        next.Click += (_, _) => w1.Close();
        w1.ShowDialog();

        // page 2: OPTIONAL confirmation. The wallet is ALREADY created and usable — confirming the seed is a
        // safety check, NEVER a gate that can fail the wallet. The user can confirm OR skip and back up later.
        var confirm = new TextBox { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), Background = FieldBg, Foreground = Ink, BorderBrush = Line, BorderThickness = new Thickness(1), Padding = new Thickness(6), Width = 420, Height = 60, AcceptsReturn = true };
        var ok = new Button { Content = "Confirm", Margin = new Thickness(0, 12, 8, 0), Padding = new Thickness(12, 6, 12, 6) };
        var skip = new Button { Content = "Skip — I've saved it", Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        var sp2 = new StackPanel { Margin = new Thickness(16) };
        sp2.Children.Add(new TextBlock { Text = "Confirm your seed (optional)", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Ink });
        sp2.Children.Add(new TextBlock { Text = "Optionally re-type the seed to confirm you saved it. Your wallet is already created — you can skip this and back up anytime from Account → Show seed backup.", Foreground = SubInk, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(0, 4, 0, 8) });
        sp2.Children.Add(confirm);
        sp2.Children.Add(new WrapPanel { Children = { ok, skip } });
        var w2 = new Window { Title = "New wallet — confirm your seed (optional)", Width = 480, Height = 280, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this), Background = WinBg, Content = new ScrollViewer { Content = sp2 } };
        ok.Click += (_, _) =>
        {
            if (confirm.Text.Trim() != seed) { MessageBox.Show("That does not match your seed — re-type it exactly, or press Skip if you've already saved it.", "Confirm seed"); return; }
            w2.Close();
        };
        skip.Click += (_, _) => w2.Close();   // NEVER block the wallet — skipping is always allowed
        w2.ShowDialog();

        Render();
        _status.Text = "New wallet ready — keys encrypted. Back up your seed (Account → Show seed backup) and fund it, then set up your identity.";
    }

    /// <summary>Live connection diagnostics: P2P peers + log, an ElectrumSVP reachability test, your current receive
    /// address, and a manual "connect to a node" — so a "no peers" problem is visible and fixable on YOUR screen.</summary>
    private void NetworkDialog()
    {
        var sp = new StackPanel { Margin = new Thickness(12) };
        var summary = new TextBlock { Foreground = Ink, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap };
        var addrLine = new TextBox { IsReadOnly = true, Background = FieldBg, Foreground = Accent, BorderThickness = new Thickness(0), FontFamily = new FontFamily("Consolas") };
        var log = new TextBox { Height = 200, Width = 560, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)), Foreground = Ink, BorderBrush = Line, BorderThickness = new Thickness(1), FontFamily = new FontFamily("Consolas"), FontSize = 11 };
        var elx = new TextBlock { Foreground = SubInk, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        void Refresh()
        {
            var n = _node();
            summary.Text = $"P2P peers: {(n?.PeerCount ?? 0)}   best height: {(n?.BestHeight ?? 0):N0}   network: {_net().Network}";
            addrLine.Text = _seed.Length == 32 ? ReceiveAddress() : "(locked)";
            log.Text = n != null ? string.Join("\n", n.RecentLog) : "(node not started)";
            log.ScrollToEnd();
        }
        sp.Children.Add(new TextBlock { Text = "Network & connectivity", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Ink });
        sp.Children.Add(summary);
        sp.Children.Add(new TextBlock { Text = "Your current receive address (the coin must pay THIS):", Foreground = SubInk, Margin = new Thickness(0, 6, 0, 0) });
        sp.Children.Add(addrLine);
        var btns = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var refresh = Btn("Refresh"); refresh.Click += (_, _) => Refresh();
        var addPeer = Btn("Connect to a public node…"); addPeer.Click += (_, _) =>
        {
            var box = new TextBox { Width = 260, Text = "host:8333" }; ThemeOne(box);
            var ok2 = new Button { Content = "Connect", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
            var w2 = new Window { Title = "Connect to a public BSV node", Width = 320, Height = 150, Owner = Window.GetWindow(this), Background = WinBg, Content = new StackPanel { Margin = new Thickness(12), Children = { new TextBlock { Text = "host:port (a public internet BSV node)", Foreground = Ink }, box, ok2 } } };
            ok2.Click += (_, _) => { var parts = box.Text.Trim().Split(':'); if (parts.Length == 2 && int.TryParse(parts[1], out var p)) _node()?.AddManualPeer(parts[0], p); w2.Close(); Refresh(); };
            w2.ShowDialog();
        };
        btns.Children.Add(refresh); btns.Children.Add(addPeer);   // no rescan button — discovery is automatic
        sp.Children.Add(btns);
        sp.Children.Add(elx);
        sp.Children.Add(new TextBlock { Text = "Connection log:", Foreground = SubInk, Margin = new Thickness(0, 8, 0, 2) });
        sp.Children.Add(log);
        Refresh();
        var win = new Window { Title = "Network", Width = 600, Height = 540, Owner = Window.GetWindow(this), Background = WinBg, Content = new ScrollViewer { Content = sp } };
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) }; t.Tick += (_, _) => Refresh(); t.Start();
        win.Closed += (_, _) => t.Stop();
        win.ShowDialog();
    }

    private string AccountPath(int i) => Path.Combine(_dataDir, i == 0 ? "wallet.json" : $"wallet-{i}.json");

    /// <summary>How many account files exist (0..N). Account 0 is the original wallet.</summary>
    private int AccountCount() { int n = 0; while (File.Exists(AccountPath(n))) n++; return Math.Max(1, n); }

    /// <summary>Switch the active account to a separate seed/wallet file (account 0 = the original wallet). Each
    /// account has its own seed, coins, and history; the identity (chat/game/NFT) stays the profile identity.</summary>
    /// <summary>STARTUP: a wallet is NEVER opened automatically (assuming a wallet = fraud). The user MUST pick:
    /// an existing wallet in this profile, open a wallet file, or create a new one. No selection = no program.</summary>

    /// <summary>Show the "Select your wallet" dialog as the FIRST thing on screen and open the chosen wallet.
    /// MUST be called AFTER the main window is shown (from MainWindow.Loaded) so the modal ShowDialog() actually
    /// runs its message loop and blocks. Returns true if a wallet was selected (and Load() ran); false if the
    /// user cancelled selection (in which case the app shuts down).</summary>
    /// <summary>The file that records the LAST FEW wallet paths the user opened (ElectrumSV-style MRU), so a
    /// wallet opened from ANYWHERE (a USB key, a backup, another drive) stays in the selector — independent of
    /// where its file lives. Stored next to the BsvPoker data root.</summary>
    private string RecentWalletsPath()
    {
        try
        {
            var profilesRoot = Path.GetDirectoryName(_dataDir);
            var bsvRoot = profilesRoot != null ? Path.GetDirectoryName(profilesRoot) : null;
            var dir = bsvRoot ?? _dataDir;
            return Path.Combine(dir, "recent-wallets.txt");
        }
        catch { return Path.Combine(_dataDir, "recent-wallets.txt"); }
    }

    private const int MaxRecentWallets = 5;

    private List<string> LoadRecentWallets()
    {
        try { var p = RecentWalletsPath(); return File.Exists(p) ? File.ReadAllLines(p).Where(l => l.Trim().Length > 0).ToList() : new(); }
        catch { return new(); }
    }

    /// <summary>Record <paramref name="path"/> as the most-recently-opened wallet, keeping the last 5 (newest
    /// first, de-duplicated). This is what makes "the last five wallets I opened stay in the list" work.</summary>
    private void RememberRecentWallet(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var list = LoadRecentWallets().Where(p => !string.Equals(Path.GetFullPath(p), full, StringComparison.OrdinalIgnoreCase)).ToList();
            list.Insert(0, full);
            if (list.Count > MaxRecentWallets) list = list.Take(MaxRecentWallets).ToList();
            File.WriteAllLines(RecentWalletsPath(), list);
        }
        catch { }
    }

    public bool SelectWalletAtStartup()
    {
        // List EVERY previously-used wallet across ALL profiles (never assume one). The user picks which one —
        // or opens any wallet file anywhere on the machine (a USB key, an external drive, a backup), or creates new.
        var existing = new System.Collections.Generic.List<string>();
        // FIRST: the last 5 wallets the user actually opened (ElectrumSV-style MRU), from ANY location, so a wallet
        // opened from a USB key/backup STAYS in the list. These come before the directory scan and in recency order.
        var recent = LoadRecentWallets().Where(File.Exists).ToList();
        existing.AddRange(recent);
        try
        {
            var profilesRoot = Path.GetDirectoryName(_dataDir);               // ...\BsvPoker\profiles
            var bsvRoot = profilesRoot != null ? Path.GetDirectoryName(profilesRoot) : null; // ...\BsvPoker
            if (profilesRoot != null && Directory.Exists(profilesRoot))
                foreach (var pdir in Directory.GetDirectories(profilesRoot).OrderBy(d => d))
                    existing.AddRange(Directory.GetFiles(pdir, "wallet*.json"));
            if (bsvRoot != null && Directory.Exists(bsvRoot))
                existing.AddRange(Directory.GetFiles(bsvRoot, "wallet*.json"));
        }
        catch { }
        // Show only REAL wallets — ones with a registered identity. Junk that earlier bugs created (empty
        // wallets, or old "play-money" wallets that have no identity) are NOT presented as choices. The files
        // are never deleted; they are just hidden from the picker. ("Open a wallet file…" can still open any.)
        bool IsReal(string f)
        {
            try
            {
                var t = File.ReadAllText(f);
                // Hide ONLY the explicit old play-money artifact. (Do NOT hide on the word "Balance" — the real
                // transaction history persists a per-row running Balance, and that was wrongly HIDING real wallets
                // from the selector. A wallet that has an identity/handle/coins/sends is ALWAYS shown.)
                if (t.Contains("opening play balance")) return false;
                // A wallet you actually USED has a handle, coins/history, or a (draft) identity. A pristine empty
                // wallet a bug created has none — hide it. Identity is NOT required (it only exists on-chain later).
                bool hasHandle = System.Text.RegularExpressions.Regex.IsMatch(t, "\"Handle\"\\s*:\\s*\"[^\"]+\"");
                bool hasIdentity = System.Text.RegularExpressions.Regex.IsMatch(t, "\"Identity\"\\s*:\\s*\\{");
                bool hasCoins = System.Text.RegularExpressions.Regex.IsMatch(t, "\"Txid\"\\s*:\\s*\"[0-9a-f]");
                bool hasSends = System.Text.RegularExpressions.Regex.IsMatch(t, "\"Sends\"\\s*:\\s*\\[\\s*\\{");
                return hasHandle || hasIdentity || hasCoins || hasSends;
            }
            catch { return false; }
        }
        // Keep RECENT wallets always (the user explicitly opened them — never hide them, even if brand-new with
        // no handle/coins yet); for the rest, show only REAL ones. Preserve order: recents first (recency order),
        // then the remaining real wallets alphabetically. De-dup by full path.
        var recentFull = new HashSet<string>(recent.Select(p => { try { return Path.GetFullPath(p); } catch { return p; } }), StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddOnce(string f) { string k; try { k = Path.GetFullPath(f); } catch { k = f; } if (seenPaths.Add(k)) ordered.Add(f); }
        foreach (var f in recent) AddOnce(f);                                   // recents first, in recency order
        foreach (var f in existing.Where(f => { try { return !recentFull.Contains(Path.GetFullPath(f)); } catch { return true; } }).Where(IsReal).OrderBy(f => f)) AddOnce(f);
        existing = ordered;
        // ALWAYS show the selection FIRST — the principal's rule: "the first thing I do is select my wallet, then
        // I log in." A wallet is NEVER opened automatically (even when only one exists); an immediate password
        // prompt for an existing wallet is a failure. The user SELECTS here, and only the SELECTED wallet is then
        // opened (Load → unlock/login). The network is a separate switch, never a wallet choice.
        string? chosen = null;
        var sp = new StackPanel { Margin = new Thickness(20) };
        sp.Children.Add(new TextBlock { Text = "Select your wallet", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Ink });
        sp.Children.Add(new TextBlock { Text = "Pick one of your wallets, open a wallet file anywhere (a USB key, a backup, another drive), or create a new one. A wallet is NEVER opened automatically — you choose.", Foreground = SubInk, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(0, 4, 0, 12) });
        if (existing.Count > 0) sp.Children.Add(new TextBlock { Text = "Your wallets:", Foreground = SubInk, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 2, 0, 2) });
        var win = new Window { Title = "Select wallet", SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = WinBg, Topmost = true, ShowInTaskbar = true };
        // GUARANTEE VISIBILITY for EVERY instance (you can run poker.exe many times — each a separate player): pop
        // the selector to the FRONT so a 2nd/3rd instance is never lost behind the first.
        win.Loaded += (_, _) => { try { win.Activate(); win.Topmost = true; win.Topmost = false; win.Focus(); } catch { } };
        Button Row(string label, Action act) { var b = new Button { Content = label, Margin = new Thickness(0, 3, 0, 3), Padding = new Thickness(12, 8, 12, 8), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left }; b.Click += (_, _) => act(); return b; }
        if (existing.Count > 0)
        {
            var recentSet = new HashSet<string>(recent.Select(p => { try { return Path.GetFullPath(p); } catch { return p; } }), StringComparer.OrdinalIgnoreCase);
            foreach (var f in existing)
            {
                string full; try { full = Path.GetFullPath(f); } catch { full = f; }
                bool isRecent = recentSet.Contains(full);
                var label = $"{(isRecent ? "🕘" : "📂")}  {Path.GetFileName(Path.GetDirectoryName(f))}\\{Path.GetFileName(f)}";
                var fc = f;
                sp.Children.Add(Row(label, () => { chosen = fc; win.DialogResult = true; }));
            }
        }
        sp.Children.Add(Row("📂  Open a wallet file…", () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Open wallet file", Filter = "Wallet (*.json)|*.json|All files (*.*)|*.*" };
            if (dlg.ShowDialog() == true) { chosen = dlg.FileName; win.DialogResult = true; }
        }));
        sp.Children.Add(Row("➕  Create a new wallet…", () =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Create a new wallet", Filter = "Wallet (*.json)|*.json", FileName = "poker-wallet.json", OverwritePrompt = false };
            if (dlg.ShowDialog() == true) { chosen = dlg.FileName; win.DialogResult = true; }
        }));
        win.Content = new ScrollViewer { Content = sp };
        var ok = win.ShowDialog();
        if (ok != true || chosen == null)
        {
            _locked = true; _seed = Array.Empty<byte>(); _path = AccountPath(0);   // nothing opened
            Application.Current?.Shutdown();                                       // no wallet selected => no program
            return false;
        }
        _path = chosen;
        RememberRecentWallet(chosen);   // keep it in the last-5 list so it ALWAYS reappears next time (any location)
        Load();   // opens (or, if the chosen file is new, creates) the SELECTED wallet — never a default
        // re-seal the card vault to the OPENED wallet's identity (NFTs belong to the wallet you opened, not a profile)
        if (_seed.Length == 32) _vault = new CardVault(_dataDir, _identityPriv, _identityPub);
        return true;
    }

    /// <summary>ElectrumSVP-style: open a wallet at an arbitrary FILE path the user chooses.</summary>
    private void OpenWalletFileDialog()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Open wallet file", Filter = "Wallet (*.json)|*.json|All files (*.*)|*.*" };
        if (dlg.ShowDialog() == true) OpenWalletAtPath(dlg.FileName);
    }

    /// <summary>Create a NEW wallet at a file path the user chooses (never overwrites an existing file — funds
    /// protection: if the chosen file already exists we OPEN it instead of clobbering it).</summary>
    private void NewWalletFileDialog()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Create new wallet file", Filter = "Wallet (*.json)|*.json", FileName = "poker-wallet.json", OverwritePrompt = false };
        if (dlg.ShowDialog() == true) OpenWalletAtPath(dlg.FileName);   // Load() creates a fresh wallet if the file is new
    }

    /// <summary>Load the wallet at <paramref name="path"/> (creating a fresh one there if the file does not yet
    /// exist). Mirrors SwitchAccount but for an arbitrary user-chosen file; never overwrites existing data.</summary>
    private void OpenWalletAtPath(string path)
    {
        try
        {
            if (_seed.Length == 32) Save();      // persist the currently open wallet first (non-destructive + backed up)
            _path = path;
            _locked = false; _w = new File_(); _seed = Array.Empty<byte>();
            Load();                               // opens (or creates) the chosen file; prompts unlock/wizard as needed
            if (_seed.Length == 32) _ring = new KeyRing(_seed, Math.Max(1, _w.RecvIndex));
            if (_freshWallet) { _freshWallet = false; AccountWizard(); }
            Render();
            OnUnlocked?.Invoke();
            _status.Text = $"Opened wallet: {Path.GetFileName(path)}";
        }
        catch (Exception ex) { _status.Text = "Open failed: " + ex.Message; }
    }

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
        // REGTEST self-fund: a brand-new empty wallet has no peers to receive from, so on the local regtest chain it
        // mines its own first coins (real trivial-PoW block + SPV proof). This is how a new player goes
        // fund → register identity on-chain → play without an external faucet. Regtest only.
        var fundRt = Btn("Fund (regtest)"); fundRt.Click += (_, _) => { if (Guard()) RegtestSelfFund(); };
        addrBtns.Children.Add(fundRt);
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

        sp.Children.Add(Lbl("How to fund this wallet (it holds REAL BSV — no play money)"));
        sp.Children.Add(_fundInfo);
        sp.Children.Add(Lbl("SPV funding (no server) — import a payment proof, find by txid, or claim a payment sent to your identity"));
        var fund = new WrapPanel();
        var importBtn = Btn("Import funding (SPV envelope)…"); importBtn.Click += (_, _) => { if (Guard()) ImportFunding(); };
        var makeEnv = Btn("Create funding envelope…"); makeEnv.Click += async (_, _) => { if (Guard()) await CreateEnvelope(); };
        var findTx = Btn("Find a payment by txid…"); findTx.Click += async (_, _) => { if (Guard()) await FindByTxid(); };
        var claim = Btn("Claim a payment to my identity…"); claim.Click += (_, _) => { if (Guard()) ClaimIdentityPayment(); };
        fund.Children.Add(importBtn); fund.Children.Add(makeEnv); fund.Children.Add(findTx); fund.Children.Add(claim);   // no rescan button — automatic
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
        _coinsGrid.MouseDoubleClick += (_, _) => { if (_coinsGrid.SelectedItem != null) TxDetails(PropOf(_coinsGrid.SelectedItem, "FullOutpoint").Split(':')[0]); };
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
        var freezeA = Btn("Freeze address"); freezeA.Click += (_, _) => FreezeAddress(true);
        var unfreezeA = Btn("Unfreeze address"); unfreezeA.Click += (_, _) => FreezeAddress(false);
        var more = Btn("Show 20 more addresses"); more.Click += (_, _) => { if (Guard()) { _w.RecvIndex += 20; Save(); Render(); } };
        row.Children.Add(copy); row.Children.Add(label); row.Children.Add(wif); row.Children.Add(freezeA); row.Children.Add(unfreezeA); row.Children.Add(more);
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
        _contactsGrid.Columns.Add(new DataGridTextColumn { Header = "✓", Binding = new System.Windows.Data.Binding("Verified"), IsReadOnly = true, Width = 36 });
        _contactsGrid.Columns.Add(new DataGridTextColumn { Header = "Handle", Binding = new System.Windows.Data.Binding("Handle"), IsReadOnly = true, Width = 140 });
        _contactsGrid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new System.Windows.Data.Binding("DisplayName"), IsReadOnly = true, Width = 160 });
        _contactsGrid.Columns.Add(new DataGridTextColumn { Header = "Email", Binding = new System.Windows.Data.Binding("Email"), IsReadOnly = true, Width = 200 });
        _contactsGrid.Columns.Add(new DataGridTextColumn { Header = "Identity public key", Binding = new System.Windows.Data.Binding("IdentityPub"), IsReadOnly = true, Width = 420 });
        _contactsGrid.Height = 360;
        sp.Children.Add(_contactsGrid);
        var add = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var hBox = new TextBox { Width = 160 }; var pBox = new TextBox { Width = 520, FontFamily = new FontFamily("Consolas") };
        add.Children.Add(new TextBlock { Text = "Handle ", Foreground = Brushes.Gainsboro, VerticalAlignment = VerticalAlignment.Center }); add.Children.Add(hBox);
        add.Children.Add(new TextBlock { Text = "  Identity pubkey (hex) ", Foreground = Brushes.Gainsboro, VerticalAlignment = VerticalAlignment.Center }); add.Children.Add(pBox);
        var addBtn = Btn("Add / update");
        addBtn.Click += (_, _) => { AddContact(hBox.Text.Trim(), pBox.Text.Trim()); hBox.Clear(); pBox.Clear(); };
        add.Children.Add(addBtn);
        sp.Children.Add(add);
        var ops = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var pay = Btn("Pay this contact…"); pay.Click += (_, _) => { if (_contactsGrid.SelectedItem != null) { _sendPayTo.Text = "@" + PropOf(_contactsGrid.SelectedItem, "Handle"); SelectTab("Send"); _amount.Focus(); } };
        var msg = Btn("Message (encrypt)…"); msg.Click += (_, _) => { if (Guard() && _contactsGrid.SelectedItem != null) EncryptMessageDialog("@" + PropOf(_contactsGrid.SelectedItem, "Handle")); };
        var copyKey = Btn("Copy identity key"); copyKey.Click += (_, _) => { if (_contactsGrid.SelectedItem != null) CopyToClipboard(PropOf(_contactsGrid.SelectedItem, "IdentityPub"), "Identity key copied."); };
        var importCert = Btn("Import identity certificate…"); importCert.Click += (_, _) => ImportIdentityCert();
        var del = Btn("Delete"); del.Click += (_, _) => { if (_contactsGrid.SelectedItem != null) { var h = PropOf(_contactsGrid.SelectedItem, "Handle"); _w.Contacts.RemoveAll(c => c.Handle == h); Save(); Render(); } };
        ops.Children.Add(pay); ops.Children.Add(msg); ops.Children.Add(copyKey); ops.Children.Add(importCert); ops.Children.Add(del);
        _contactsGrid.MouseDoubleClick += (_, _) => { if (_contactsGrid.SelectedItem != null) { _sendPayTo.Text = "@" + PropOf(_contactsGrid.SelectedItem, "Handle"); SelectTab("Send"); _amount.Focus(); } };
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
                var (raw, status) = FundTx(Chain.P2shLock(redeem), value, 1);   // tiny 1-sat fee, not 600
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

    /// <summary>The identity status card — FRESH controls only (no reused member controls), so it can be rebuilt on
    /// every Render without re-parenting anything. Shows ONLY the identity (if stored) or, if none, that the wallet
    /// can only receive funds. The word "registration"/"register" is BANNED here in EVERY state.</summary>
    private UIElement BuildIdentityCard()
    {
        var regBox = new Border { Background = PanelBg, BorderBrush = Line, BorderThickness = new Thickness(1), Padding = new Thickness(10), Margin = new Thickness(0, 8, 0, 8), CornerRadius = new CornerRadius(4) };
        var regSp = new StackPanel();
        if (_w.Identity is { } id && id.Pseudonym.Length > 0)
        {
            var idHex = _identityPub.Length == 33 ? Convert.ToHexString(_identityPub).ToLowerInvariant() : "";
            regSp.Children.Add(new TextBlock { Text = $"Your identity: {id.DisplayName}  (@{id.Pseudonym})", Foreground = Accent, FontWeight = FontWeights.Bold });
            regSp.Children.Add(new TextBlock { Text = $"Email: {id.Email}" + (id.Country.Length > 0 ? $"   Country: {id.Country}" : ""), Foreground = SubInk });
            if (id.Website.Length > 0) regSp.Children.Add(new TextBlock { Text = $"Website: {id.Website}", Foreground = SubInk });
            if (id.Bio.Length > 0) regSp.Children.Add(new TextBlock { Text = $"Bio: {id.Bio}", Foreground = SubInk, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 });
            regSp.Children.Add(new TextBlock { Text = $"Identity key (share this so people can pay/message you): {idHex}", Foreground = SubInk, TextWrapping = TextWrapping.Wrap, MaxWidth = 560, Margin = new Thickness(0, 4, 0, 0) });
            var share = Btn("Copy my identity (@handle + key)"); share.Click += (_, _) => CopyToClipboard($"@{id.Pseudonym} {idHex}", "Identity copied — share it with other players.");
            var edit = Btn("Edit profile (country / website / bio)…"); edit.Click += (_, _) => EditOptionalDetails();
            var row = new WrapPanel(); row.Children.Add(share); row.Children.Add(edit);
            regSp.Children.Add(row);
        }
        else
        {
            regSp.Children.Add(new TextBlock { Text = "No identity on this wallet yet.", Foreground = SubInk, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 });
            regSp.Children.Add(new TextBlock { Text = "Until your identity exists, this wallet can only RECEIVE funds (Receive tab). Your identity is set up automatically the first time you join a game.", Foreground = SubInk, TextWrapping = TextWrapping.Wrap, MaxWidth = 560, Margin = new Thickness(0, 2, 0, 6) });
        }
        regBox.Child = regSp;
        return regBox;
    }

    // ---- IDENTITY: your Base ID key (login/handle), master public key, what it is ----
    private UIElement BuildIdentityTab()
    {
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(H("Your identity (Base ID key)"));
        sp.Children.Add(new TextBlock { Text = "Your Base ID key is your identity — like an NFT you own. It is NEVER used as an address; it only derives one-time ECDH sub-keys (Type-42), all linked in an HMAC hash chain. Give others your handle or identity public key so they can pay and message you.", Foreground = Brushes.Gainsboro, TextWrapping = TextWrapping.Wrap, MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left });

        // The identity card lives in its OWN refreshable holder built from FRESH controls only (no reused member
        // controls), so Render can update it every time WITHOUT the "already the logical child of another element"
        // crash that previously left the whole tab frozen on its first (no-identity) build.
        _idCard = new ContentControl { Content = BuildIdentityCard() };
        sp.Children.Add(_idCard);

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
        var exportCert = Btn("Export my identity certificate"); exportCert.Click += (_, _) => ExportIdentityCert();
        var verifyCert = Btn("Verify someone's identity…"); verifyCert.Click += (_, _) => VerifyIdentityCert();
        idBtns.Children.Add(copyId); idBtns.Children.Add(exportCert); idBtns.Children.Add(verifyCert);
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
        var watch = Btn("Add a watch-only address…"); watch.Click += (_, _) => AddWatchAddress();
        imp.Children.Add(sweep); imp.Children.Add(watch);
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
    // SPENDABLE = REAL coins you can use NOW. A coin is REAL if it is confirmed (mined + saved proof verifies)
    // OR it is backed by an actual broadcast/received TRANSACTION (an SPV coin waiting to be mined is trusted
    // between moves — the principal's rule). Pure fabrications (NO tx AND NO proof) count for NOTHING. Balance
    // is the total spendable; Pending is the not-yet-mined PORTION of it (informational). Double-spent and
    // watch-only are excluded.
    private static bool IsRealCoin(UtxoRec u) => u.Confirmed || !string.IsNullOrEmpty(u.RawTxHex) || !string.IsNullOrEmpty(u.EnvelopeWire);
    private long Balance => _w.Utxos.Where(u => !u.Spent && !u.DoubleSpent && !u.WatchOnly && IsRealCoin(u)).Sum(u => u.Value);
    private long Pending => _w.Utxos.Where(u => !u.Spent && !u.Confirmed && !u.DoubleSpent && !u.WatchOnly && IsRealCoin(u)).Sum(u => u.Value);
    private long WatchedBalance => _w.Utxos.Where(u => !u.Spent && u.Confirmed && u.WatchOnly).Sum(u => u.Value);

    /// <summary>A coin is CONFIRMED only if its SAVED SPV proof re-verifies offline — the merkle branch folds to
    /// a header that meets proof-of-work (<see cref="BsvPoker.Net.Bsv.SpvEnvelope.Verify"/>). No saved proof, a
    /// bad one, or a proven double-spend => NOT confirmed. The proof travels WITH the coin: no network, no header
    /// store, no server needed to re-check it. This is the single source of truth for "confirmed".</summary>
    private static bool ReverifyProof(UtxoRec u)
    {
        if (u.DoubleSpent) return false;
        try
        {
            // Form A — an SpvEnvelope (raw tx + 80-byte header + merkle branch + index): self-verifies (PoW + branch).
            if (!string.IsNullOrEmpty(u.EnvelopeWire))
                return BsvPoker.Net.Bsv.SpvEnvelope.FromWire(u.EnvelopeWire).Verify();
            // Form B — a merkleblock (header + partial tree) carrying our tx: recompute root, check PoW, our tx matched.
            if (!string.IsNullOrEmpty(u.RawTxHex) && !string.IsNullOrEmpty(u.MerkleBlockHex))
            {
                var tx = Chain.Deserialize(Convert.FromHexString(u.RawTxHex));
                var parsed = BsvPoker.Net.Bsv.PartialMerkleTree.ParseMerkleBlock(Convert.FromHexString(u.MerkleBlockHex));
                if (!parsed.Header.MeetsPow()) return false;                                   // real proof-of-work
                if (!parsed.Root.AsSpan().SequenceEqual(parsed.Header.MerkleRoot)) return false; // partial tree folds to the header root
                var txid = BsvPoker.Crypto.Hashes.Sha256d(Chain.Serialize(tx));               // internal-order txid
                return parsed.Matched.Any(m => m.Txid.AsSpan().SequenceEqual(txid));          // our tx is among the proven leaves
            }
            return false;   // no saved proof => never confirmed
        }
        catch { return false; }
    }

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
                    {
                        // STORE THE ACTUAL TX BYTES: this coin is backed by a REAL received transaction, so it is a
                        // REAL coin — spendable NOW as 0-conf (at the holder's risk) and counted in the balance,
                        // not a fabrication. Saving the raw tx is also what lets it survive a restart (the Load()
                        // purge only drops coins with NO tx and NO proof) and what later gets a merkle proof added.
                        _w.Utxos.Add(new UtxoRec { Txid = txid, Vout = (uint)v, Value = tx.Outs[v].Value, KeyChain = 0, KeyIndex = i,
                            Confirmed = false, RawTxHex = Convert.ToHexString(Core.Chain.Serialize(tx)).ToLowerInvariant() });
                        added = true;
                    }
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
            {
                // SAVE a re-verifiable proof for this now-mined coin, then DERIVE Confirmed from it (never set blindly).
                try { u.RawTxHex = Convert.ToHexString(Core.Chain.Serialize(parsed.Txs[idx])).ToLowerInvariant();
                      u.MerkleBlockHex = Convert.ToHexString(BsvPoker.Net.Bsv.PartialMerkleTree.BuildMerkleBlock(parsed.Header, parsed.Txids, new HashSet<int> { idx })).ToLowerInvariant(); } catch { }
                u.Confirmed = ReverifyProof(u);
                if (u.Confirmed) { changed = true; AppendTx("confirmed", u.Value, $"{u.Txid[..12]}…:{u.Vout} mined"); }
            }
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
        foreach (var addr in _w.WatchAddresses)              // watch-only external addresses (balance only)
            try { var p = Base58.CheckDecode(addr); if (p.Length == 21) elems.Add(p[1..]); } catch { }
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
                        var rec = new UtxoRec { Txid = utxo.Txid, Vout = utxo.Vout, Value = utxo.Value, KeyChain = c, KeyIndex = i, RawTxHex = Convert.ToHexString(Chain.Serialize(tx)).ToLowerInvariant(), MerkleBlockHex = Convert.ToHexString(merkleBlockPayload).ToLowerInvariant() };
                        rec.Confirmed = ReverifyProof(rec);   // SAVE the proof with the coin; confirmed only if it re-verifies
                        _w.Utxos.Add(rec);
                        AppendTx("received", utxo.Value, $"SPV-confirmed {utxo.Txid[..12]}…:{utxo.Vout}");
                        Notify($"Received {utxo.Value:N0} sat (SPV-confirmed).");
                        changed = true;
                    }
                    else // already known (was pending) → SAVE the proof onto it, then re-verify to confirm
                        foreach (var u in _w.Utxos.Where(u => u.Txid == utxo.Txid && u.Vout == utxo.Vout && !u.Confirmed))
                        {
                            u.RawTxHex = Convert.ToHexString(Chain.Serialize(tx)).ToLowerInvariant();
                            u.MerkleBlockHex = Convert.ToHexString(merkleBlockPayload).ToLowerInvariant();
                            u.Confirmed = ReverifyProof(u);
                            if (u.Confirmed) { changed = true; AppendTx("confirmed", u.Value, $"{u.Txid[..12]}…:{u.Vout} mined"); }
                        }
                    break;
                }
        // watch-only: credit (as unspendable, balance-only) any output paying a watched address, proven by SPV
        foreach (var addr in _w.WatchAddresses)
        {
            byte[] h160; try { var p = Base58.CheckDecode(addr); if (p.Length != 21) continue; h160 = p[1..]; } catch { continue; }
            for (uint v = 0; v < (uint)tx.Outs.Count; v++)
            {
                var wu = SpvFunding.VerifyWatchFromMerkleBlock(tx, v, merkleBlockPayload, chain, h160);
                if (wu == null) continue;
                if (!_w.Utxos.Any(u => u.Txid == wu.Txid && u.Vout == wu.Vout))
                { var wrec = new UtxoRec { Txid = wu.Txid, Vout = wu.Vout, Value = wu.Value, WatchOnly = true, RawTxHex = Convert.ToHexString(Chain.Serialize(tx)).ToLowerInvariant(), MerkleBlockHex = Convert.ToHexString(merkleBlockPayload).ToLowerInvariant() }; wrec.Confirmed = ReverifyProof(wrec); _w.Utxos.Add(wrec); changed = true; Notify($"Watch-only: {wu.Value:N0} sat seen on {addr}."); }
            }
        }
        // sweep: if an output of this proven tx pays an external key we're sweeping, move it into THIS wallet now
        SweepFromProvenTx(tx);
        if (changed) { Save(); Render(); }
        return changed;
    }

    /// <summary>
    /// Credit a transaction taken from a FULLY-VALIDATED block (the caller verified the block: its recomputed
    /// merkle root matches a header in our genesis-validated chain, so every tx in it is PoW-proven mined). No
    /// merkleblock is needed — this is the path that WORKS on public mainnet nodes, which do not serve bloom
    /// filters. Any output paying one of our receive/change keys (a wide index window) or a watch address is
    /// credited as a confirmed coin. Idempotent. Returns true if anything was credited.
    /// </summary>
    // Cache of our derived P2PKH locking scripts (hex) → (chain, index). Scanning a block matches each output
    // against this with an O(1) lookup instead of re-deriving ~3×gap secp256k1 public keys per output — the
    // derivation is identical for every transaction, so doing it per-output pinned a CPU core during a rescan.
    // Rebuilt only when the unlocked seed changes or the scan window (gap) grows.
    private Dictionary<string, (uint Chain, uint Index)>? _scriptIndex;
    private byte[]? _scriptIndexSeed;
    private uint _scriptIndexGap;

    private Dictionary<string, (uint Chain, uint Index)> EnsureScriptIndex(uint gap)
    {
        if (_scriptIndex != null && _scriptIndexSeed != null
            && _scriptIndexSeed.AsSpan().SequenceEqual(_seed) && _scriptIndexGap >= gap)
            return _scriptIndex;
        var map = new Dictionary<string, (uint Chain, uint Index)>();
        for (uint c = 0; c <= 2; c++)
            for (uint i = 0; i <= gap; i++)
            {
                var key = Convert.ToHexString(Core.Chain.P2pkhLockForPub(WalletKeys.Account(_seed, c, i).Pub));
                if (!map.ContainsKey(key)) map[key] = (c, i);   // keep the lowest (chain, index) on the (impossible) collision
            }
        _scriptIndex = map; _scriptIndexSeed = (byte[])_seed.Clone(); _scriptIndexGap = gap;
        return _scriptIndex;
    }

    public bool ConfirmFromBlock(Core.Chain.Tx tx, BsvPoker.Net.Bsv.BlockHeader? header = null, IReadOnlyList<byte[]>? txids = null, int txIndex = -1)
    {
        if (!Dispatcher.CheckAccess()) { return (bool)Dispatcher.Invoke(new Func<bool>(() => ConfirmFromBlock(tx, header, txids, txIndex))); }
        if (_locked || _seed.Length != 32) return false;
        bool changed = false;
        var txid = Core.Chain.Txid(tx);
        uint gap = Math.Max((uint)_w.RecvIndex + 50, 500);   // scan a wide index window so a high-index funded address is still found
        // Build the SAVEABLE merkle proof for THIS tx from the full block, so a credited coin CARRIES its proof
        // and is re-verifiable offline forever. Only when the caller passed the block header + txids + this index.
        string rawHex = "", mbHex = "";
        if (header != null && txids != null && txIndex >= 0)
        {
            try { rawHex = Convert.ToHexString(Core.Chain.Serialize(tx)).ToLowerInvariant();
                  mbHex = Convert.ToHexString(BsvPoker.Net.Bsv.PartialMerkleTree.BuildMerkleBlock(header, txids, new HashSet<int> { txIndex })).ToLowerInvariant(); }
            catch { rawHex = ""; mbHex = ""; }
        }
        var scriptIndex = EnsureScriptIndex(gap);
        for (uint v = 0; v < (uint)tx.Outs.Count; v++)
        {
            var script = tx.Outs[(int)v].Script;
            bool credited = false;
            if (scriptIndex.TryGetValue(Convert.ToHexString(script), out var ci))
            {
                uint c = ci.Chain, i = ci.Index;
                var existing = _w.Utxos.FirstOrDefault(u => u.Txid == txid && u.Vout == v);
                if (existing == null)
                {
                    var rec = new UtxoRec { Txid = txid, Vout = v, Value = tx.Outs[(int)v].Value, KeyChain = c, KeyIndex = i, RawTxHex = rawHex, MerkleBlockHex = mbHex };
                    rec.Confirmed = ReverifyProof(rec);   // confirmed ONLY if the saved proof re-verifies
                    _w.Utxos.Add(rec);
                    AppendTx("received", tx.Outs[(int)v].Value, $"found on-chain {txid[..12]}…:{v}");
                    if (rec.Confirmed) Notify($"Found {tx.Outs[(int)v].Value:N0} sat on-chain (confirmed).");
                }
                else if (!existing.Confirmed)   // upgrade a known coin by SAVING its proof, then re-verify
                {
                    if (mbHex.Length > 0) { existing.RawTxHex = rawHex; existing.MerkleBlockHex = mbHex; }
                    existing.Confirmed = ReverifyProof(existing);
                }
                changed = true; credited = true;
            }
            if (credited) continue;
            foreach (var addr in _w.WatchAddresses)
            {
                byte[] h160; try { var p = Base58.CheckDecode(addr); if (p.Length != 21) continue; h160 = p[1..]; } catch { continue; }
                if (!script.AsSpan().SequenceEqual(Core.Chain.P2pkhLock(h160))) continue;
                if (!_w.Utxos.Any(u => u.Txid == txid && u.Vout == v))
                { var wrec = new UtxoRec { Txid = txid, Vout = v, Value = tx.Outs[(int)v].Value, WatchOnly = true, RawTxHex = rawHex, MerkleBlockHex = mbHex }; wrec.Confirmed = ReverifyProof(wrec); _w.Utxos.Add(wrec); changed = true; Notify($"Watch-only: {tx.Outs[(int)v].Value:N0} sat on {addr}."); }
            }
        }
        if (changed) { Save(); Render(); }
        return changed;
    }

    /// <summary>
    /// FAST SPV (the &lt;15s path the user requires): query the ElectrumSVP SPV servers (the same servers
    /// ElectrumSVP uses) for this wallet's addresses by scripthash and credit any unspent coins. Address-indexed
    /// servers answer in milliseconds, so balances appear within a couple of seconds — no genesis sync, no block
    /// download. Runs automatically; the user never asks. Our own clean client code (no ElectrumX library).
    /// </summary>
    public async System.Threading.Tasks.Task SpvServerDiscoverAsync()
    {
        if (_locked || _seed.Length != 32) return;
        if (_discoverBusy) return;                // never overlap two discover syncs (CPU + server hammering)
        _discoverBusy = true;
        try
        {
        using var cli = new ElectrumSvpClient();
        if (!await cli.ConnectAnyAsync(ElectrumSvpClient.ServersFor(_net().Network))) return;
        uint gap = Math.Min(Math.Max((uint)_w.RecvIndex + 30, 60), 120);   // bounded so it stays well under 15s
        var targets = new System.Collections.Generic.List<(byte[] script, uint c, uint i, bool watch)>();
        // Interleave by INDEX (idx0 receive, idx0 change, idx1 receive, …) so the most-likely addresses — incl. the
        // change address index 0, where the wallet's own change lands — are queried FIRST and the balance appears
        // in the first second, not only after all ~120 addresses have been scanned.
        for (uint i = 0; i <= gap; i++) for (uint c = 0; c <= 1; c++) targets.Add((Core.Chain.P2pkhLockForPub(WalletKeys.Account(_seed, c, i).Pub), c, i, false));
        foreach (var addr in _w.WatchAddresses) { try { var p = Base58.CheckDecode(addr); if (p.Length == 21) targets.Add((Core.Chain.P2pkhLock(p[1..]), 0, 0, true)); } catch { } }
        bool changed = false;
        long lastShown = -1;                                                     // last spendable total pushed to the UI
        var onChainUnspent = new System.Collections.Generic.HashSet<string>();   // CONFIRMED-unspent outpoints (phantom check)
        var serverUnspent = new System.Collections.Generic.HashSet<string>();    // EVERY outpoint the server lists (confirmed+mempool)
        var queriedScriptHex = new System.Collections.Generic.HashSet<string>(); // our scripts we got a definitive answer for
        foreach (var (script, c, i, watch) in targets)
        {
            System.Collections.Generic.List<ElectrumSvpClient.Utxo> us;
            try { us = await cli.ListUnspentAsync(ElectrumSvpClient.ScriptHashOf(script)); } catch { continue; }
            if (!watch) queriedScriptHex.Add(Convert.ToHexString(script));        // we KNOW this address's unspent set now
            foreach (var su in us) serverUnspent.Add(su.TxHashDisplay + ":" + su.Vout);
            // SPV-verify each server-reported coin by FETCHING its envelope (raw tx + header + merkle branch) and
            // checking it folds to a proof-of-work header. listunspent alone is never trusted — servers are assumed
            // compromised; trust is in the PROOF. We SAVE the envelope so the coin re-verifies offline forever.
            // EVERYTHING the server lists as unspent is SPENDABLE right now — whether confirmed (height>0) OR in
            // the MEMPOOL (height<=0, e.g. the change from a just-broadcast tx). The old code SKIPPED mempool
            // coins, which is exactly why a real 0-conf coin (your money) showed as 0 and could not be used.
            foreach (var u in us)
            {
                BsvPoker.Net.Bsv.SpvEnvelope? env = null;
                string rawHex = "";
                if (u.Height > 0)
                {
                    try { env = await cli.GetEnvelopeAsync(u.TxHashDisplay, u.Height); } catch { env = null; }
                    if (env == null) continue;                                  // confirmed but unprovable this round
                    rawHex = Convert.ToHexString(env.RawTx).ToLowerInvariant();
                    onChainUnspent.Add(u.TxHashDisplay + ":" + u.Vout);         // CONFIRMED-unspent → used for phantom detection
                }
                else
                {
                    try { rawHex = Convert.ToHexString(await cli.GetTransactionAsync(u.TxHashDisplay)).ToLowerInvariant(); } catch { rawHex = ""; }
                    if (rawHex.Length == 0) continue;                           // mempool coin we couldn't fetch — next tick
                }
                // THE CHAIN IS THE SOURCE OF TRUTH FOR SPENT-NESS. The server returned this outpoint as unspent, so
                // a local Spent/DoubleSpent flag is a LIE — clear it and make the coin spendable (0-conf at risk if
                // still in the mempool; fully confirmed once proven). This recovers real money the wallet hid.
                var existing = _w.Utxos.FirstOrDefault(x => x.Txid == u.TxHashDisplay && x.Vout == u.Vout);
                if (existing != null)
                {
                    bool was = existing.Spent || existing.DoubleSpent || string.IsNullOrEmpty(existing.RawTxHex)
                               || (env != null && (!existing.Confirmed || string.IsNullOrEmpty(existing.EnvelopeWire)));
                    existing.Spent = false; existing.DoubleSpent = false; existing.WatchOnly = watch;
                    if (string.IsNullOrEmpty(existing.RawTxHex)) existing.RawTxHex = rawHex;
                    if (env != null && string.IsNullOrEmpty(existing.EnvelopeWire)) existing.EnvelopeWire = env.ToWire();
                    existing.Value = u.Value; existing.KeyChain = c; existing.KeyIndex = i;
                    existing.Confirmed = ReverifyProof(existing);
                    if (was) changed = true;
                    continue;
                }
                var rec = new UtxoRec { Txid = u.TxHashDisplay, Vout = u.Vout, Value = u.Value, KeyChain = c, KeyIndex = i, WatchOnly = watch, RawTxHex = rawHex, EnvelopeWire = env != null ? env.ToWire() : "" };
                rec.Confirmed = ReverifyProof(rec);
                _w.Utxos.Add(rec);
                changed = true;
            }
            // RECONCILE THIS address authoritatively, NOW: any coin we hold on it that the server did NOT just list
            // as unspent has been spent (or is a stale/phantom local change coin) — mark it spent immediately so
            // the on-screen balance is the REAL on-chain amount, never an inflated sum of dead change coins.
            if (!watch)
            {
                var here = new System.Collections.Generic.HashSet<string>(us.Select(x => x.TxHashDisplay + ":" + x.Vout));
                foreach (var u in _w.Utxos)
                    if (!u.WatchOnly && !u.Spent && u.KeyChain == c && u.KeyIndex == i && !here.Contains(u.Txid + ":" + u.Vout))
                    { u.Spent = true; u.DoubleSpent = false; changed = true; }
            }
            // PUSH the balance to the screen the MOMENT it changes — don't make the user stare at 0 until the whole
            // ~120-address scan finishes. (Renders only when the spendable total actually moved.)
            long spendNow = _w.Utxos.Where(x => !x.Spent && !x.DoubleSpent && !x.WatchOnly && IsRealCoin(x)).Sum(x => x.Value);
            if (spendNow != lastShown) { lastShown = spendNow; try { await Dispatcher.InvokeAsync(() => Render()); } catch { } }
            // CONFIRMED comes ONLY from a coin's SAVED PROOF (ReverifyProof at load) — NEVER from whether THIS
            // network's server currently lists it. A real coin must NOT be un-confirmed just because this server
            // (wrong network, or one that doesn't index it) didn't return it: that was wiping real coins to zero.
            // Spent-detection is a separate concern (an outpoint check), not done by dropping a proven coin here.
        }
        // (Per-address authoritative reconcile already ran inside the loop, so the balance now equals the chain's
        // unspent set across every address we queried.) serverUnspent/onChainUnspent retained for clarity.
        _ = serverUnspent; _ = queriedScriptHex; _ = onChainUnspent;
        if (changed) await Dispatcher.InvokeAsync(() => { Save(); Render(); });
        }
        finally { _discoverBusy = false; }
    }

    /// <summary>FULL transaction history, ElectrumSVP-style: ask the SPV servers for EVERY transaction that ever
    /// touched ANY of this wallet's addresses (receive + change + watch), fetch each one, and show it — confirmed
    /// or mempool, money in or money out — with the net amount and a running balance. Not derived from local
    /// coins (which only shows what we still hold); this is the complete on-chain history like Electrum's. Each row
    /// <summary>Re-broadcast every PENDING (0-conf, unspent) coin's raw transaction to the network so it actually
    /// gets mined — the safe fix for coins stuck "unconfirmed" because a broadcast was missed. Idempotent: a tx
    /// already in the mempool/chain is simply re-accepted/ignored. Runs on the SPV tick and from the manual button.</summary>
    public void RebroadcastPending()
    {
        var node = _node(); if (node == null || _locked) return;
        foreach (var u in _w.Utxos.Where(u => !u.Confirmed && !u.Spent && !u.WatchOnly && !string.IsNullOrEmpty(u.RawTxHex)).ToList())
            try { node.Broadcast(Convert.FromHexString(u.RawTxHex)); } catch { }
    }

    /// carries its txid so a double-click opens the full input/output breakdown. Runs automatically.</summary>
    public async System.Threading.Tasks.Task FetchServerHistoryAsync()
    {
        if (_locked || _seed.Length != 32) return;
        if (_historyBusy) return;                 // a previous sync is still running — do NOT stack (CPU killer)
        _historyBusy = true;
        try
        {
        using var cli = new ElectrumSvpClient();
        if (!await cli.ConnectAnyAsync(ElectrumSvpClient.ServersFor(_net().Network))) return;
        uint gap = Math.Min(Math.Max((uint)_w.RecvIndex + 30, 60), 120);
        var ourScriptHex = new System.Collections.Generic.HashSet<string>();
        var scripts = new System.Collections.Generic.List<byte[]>();
        for (uint c = 0; c <= 1; c++) for (uint i = 0; i <= gap; i++) { var s = Core.Chain.P2pkhLockForPub(WalletKeys.Account(_seed, c, i).Pub); scripts.Add(s); ourScriptHex.Add(Convert.ToHexString(s)); }
        foreach (var addr in _w.WatchAddresses) { try { var p = Base58.CheckDecode(addr); if (p.Length == 21) { var s = Core.Chain.P2pkhLock(p[1..]); scripts.Add(s); ourScriptHex.Add(Convert.ToHexString(s)); } } catch { } }
        // SUBSCRIBE TO MY IDENTITY ADDRESS: chat/group messages pay a 1-sat discovery output to the recipient's
        // identity address (a fixed scripthash) — so messages sent while I was OFFLINE are found here on sync.
        byte[]? idScript = null;
        if (_identityPub.Length == 33) { idScript = Core.Chain.P2pkhLockForPub(_identityPub); scripts.Add(idScript); ourScriptHex.Add(Convert.ToHexString(idScript)); }
        // 1) collect EVERY txid touching any of our addresses (with its height)
        var heights = new System.Collections.Generic.Dictionary<string, int>();
        foreach (var s in scripts)
        {
            try { foreach (var (txid, h) in await cli.GetHistoryAsync(ElectrumSvpClient.ScriptHashOf(s))) heights[txid] = h; } catch { }
        }
        if (heights.Count == 0) return;
        // 2) fetch each transaction (bounded) and parse it
        var txCache = new System.Collections.Generic.Dictionary<string, Core.Chain.Tx>();
        foreach (var txid in heights.Keys.Take(500))
        { try { txCache[txid] = Core.Chain.Deserialize(await cli.GetTransactionAsync(txid)); } catch { } }
        // 3) value of every OUR output (so we can value the inputs that spent them)
        var ourOut = new System.Collections.Generic.Dictionary<string, long>();
        foreach (var (txid, tx) in txCache)
            for (int v = 0; v < tx.Outs.Count; v++)
                if (ourScriptHex.Contains(Convert.ToHexString(tx.Outs[v].Script))) ourOut[txid + ":" + (uint)v] = tx.Outs[v].Value;
        // 4) one row per tx: received-to-us minus spent-from-us = the net delta, like Electrum
        var rows = new System.Collections.Generic.List<Tx>();
        foreach (var (txid, tx) in txCache)
        {
            long recv = 0; for (int v = 0; v < tx.Outs.Count; v++) if (ourScriptHex.Contains(Convert.ToHexString(tx.Outs[v].Script))) recv += tx.Outs[v].Value;
            long sent = 0; foreach (var inp in tx.Ins) if (ourOut.TryGetValue(inp.PrevTxid + ":" + inp.Vout, out var val)) sent += val;
            int h = heights.TryGetValue(txid, out var hh) ? hh : 0;
            rows.Add(new Tx { Height = h, Time = h > 0 ? "on-chain" : "mempool", Type = (recv - sent) >= 0 ? "received" : "sent",
                Amount = recv - sent, Memo = txid + (_w.TxLabels.TryGetValue(txid, out var lb) ? "  — " + lb : "") });
        }
        // STORE-AND-FORWARD DELIVERY: scan every fetched tx for a chat/group message addressed to MY identity and
        // surface it to the chat. Deduped by txid so re-syncs never re-deliver. This is how an offline message
        // arrives when I come back online — the message has been sitting on-chain the whole time.
        if (_identityPub.Length == 33 && _identityPriv.Length == 32 && OnChatReceived != null)
        {
            foreach (var (txid, tx) in txCache)
            {
                if (!_deliveredChat.Add(txid)) continue;                                  // already surfaced
                try
                {
                    var dm = OnChainChat.TryReadTx(tx, _identityPriv, _identityPub);
                    if (dm != null && !dm.SenderPub.AsSpan().SequenceEqual(_identityPub))
                    { var s = Convert.ToHexString(dm.SenderPub).ToLowerInvariant(); var t = dm.Text;
                      await Dispatcher.InvokeAsync(() => OnChatReceived?.Invoke(s, t)); continue; }
                    var gm = OnChainChat.TryReadGroupTx(tx, _identityPriv, _identityPub);
                    if (gm != null && !gm.SenderPub.AsSpan().SequenceEqual(_identityPub))
                    { var s = Convert.ToHexString(gm.SenderPub).ToLowerInvariant(); var t = "[group] " + gm.Text;
                      await Dispatcher.InvokeAsync(() => OnChatReceived?.Invoke(s, t)); continue; }
                }
                catch { }
            }
        }
        await Dispatcher.InvokeAsync(() => { _w.History = rows; Save(); ApplyHistoryFilter(); });
        }
        finally { _historyBusy = false; }
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
    // Default fee is 1 sat — the everything-on-chain rule (tiny ~1-sat per-tx fees), NOT a fixed 500. Callers
    // that want a size-based fee pass EstimateFee(...); callers that omit it get the minimal 1-sat fee.
    public (byte[]? Raw, string Status) FundTx(byte[] outputScript, long value = 1000, long fee = 1)
    {
        if (_locked) return (null, "🔒 Unlock the wallet first.");
        var w = new OnChainWallet(_seed);
        // Select only REAL spendable coins — exclude phantom/double-spent and unbacked records (match Balance), so
        // we never try to spend a coin that does not exist on-chain.
        foreach (var u in _w.Utxos.Where(u => !u.Spent && !u.DoubleSpent && !u.WatchOnly && IsRealCoin(u))) w.Add(new OnChainWallet.Utxo(u.Txid, u.Vout, u.Value, u.KeyChain, u.KeyIndex));
        if (w.Balance < value + fee)
        {
            long watch = _w.Utxos.Where(u => !u.Spent && u.WatchOnly).Sum(u => u.Value);
            if (watch >= value + fee)
                return (null, $"Your {watch:N0} sat is WATCH-ONLY — it's on an external address this wallet doesn't hold the private key for, so it can't be SPENT (and an identity is a signed on-chain tx). Import that address's private key (Coins → Sweep a private key) to spend it.");
            return (null, $"Insufficient SPENDABLE sats: have {w.Balance:N0}, need {value + fee:N0}.");
        }
        try
        {
            var spend = w.SpendAction(outputScript, value, fee);
            // NON-DESTRUCTIVE — never delete a coin record. Mark the spent inputs Spent (they are KEPT in full,
            // not removed) and add the change as a new UNCONFIRMED coin (real only once SPV-proven on-chain).
            // Change is never stamped Confirmed optimistically — that fabrication, via the 45s Announce -> FundTx,
            // WAS the phantom-balance fraud. Nothing whatsoever is deleted here.
            foreach (var inp in spend.Inputs)
                foreach (var u in _w.Utxos.Where(u => u.Txid == inp.Txid && u.Vout == inp.Vout)) u.Spent = true;
            if (spend.Change > 0)
            {
                uint cvout = (uint)(spend.Tx.Outs.Count - 1);
                var ctxid = Chain.Txid(spend.Tx);
                if (!_w.Utxos.Any(u => u.Txid == ctxid && u.Vout == cvout))
                    // store the raw change tx we just built: change is a REAL coin (we made the tx), spendable as
                    // 0-conf between moves and kept across restarts — not a fabrication.
                    _w.Utxos.Add(new UtxoRec { Txid = ctxid, Vout = cvout, Value = spend.Change, KeyChain = 1, KeyIndex = 0,
                        Confirmed = false, RawTxHex = Convert.ToHexString(Chain.Serialize(spend.Tx)).ToLowerInvariant() });
            }
            AppendTx("on-chain action", -(value + fee), $"tx {Chain.Txid(spend.Tx)}");
            Save(); Render();
            return (Chain.Serialize(spend.Tx), "");
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    /// <summary>
    /// Fund a CHAT message so it is RETRIEVABLE OFFLINE (store-and-forward). The tx carries the encrypted data
    /// output PLUS a tiny 1-sat "discovery" output to EACH recipient's identity address — a fixed, indexable
    /// scripthash. Because the message is broadcast to miners (on-chain, permanent) and lands on each recipient's
    /// identity address, a recipient who was OFFLINE finds it in their SPV history on next sync and decrypts it.
    /// </summary>
    public (byte[]? Raw, string Status) FundChatTx(byte[] dataScript, IReadOnlyList<byte[]> recipientPubs, long fee = 1)
    {
        if (_locked) return (null, "🔒 Unlock the wallet first.");
        var w = new OnChainWallet(_seed);
        foreach (var u in _w.Utxos.Where(u => !u.Spent && !u.DoubleSpent && !u.WatchOnly && IsRealCoin(u))) w.Add(new OnChainWallet.Utxo(u.Txid, u.Vout, u.Value, u.KeyChain, u.KeyIndex));
        var outs = new List<(byte[] Script, long Value)> { (dataScript, 1) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rp in recipientPubs)
            try { if (rp != null && rp.Length == 33 && seen.Add(Convert.ToHexString(rp))) outs.Add((Chain.P2pkhLockForPub(rp), 1)); } catch { }
        long need = outs.Sum(o => o.Value) + fee;
        if (w.Balance < need) return (null, $"Insufficient SPENDABLE sats: have {w.Balance:N0}, need {need:N0}.");
        try
        {
            var spend = w.BuildActionMany(outs, fee);
            foreach (var inp in spend.Inputs)
                foreach (var u in _w.Utxos.Where(u => u.Txid == inp.Txid && u.Vout == inp.Vout)) u.Spent = true;
            if (spend.Change > 0)
            {
                uint cvout = (uint)(spend.Tx.Outs.Count - 1);
                var ctxid = Chain.Txid(spend.Tx);
                if (!_w.Utxos.Any(u => u.Txid == ctxid && u.Vout == cvout))
                    _w.Utxos.Add(new UtxoRec { Txid = ctxid, Vout = cvout, Value = spend.Change, KeyChain = 1, KeyIndex = 0,
                        Confirmed = false, RawTxHex = Convert.ToHexString(Chain.Serialize(spend.Tx)).ToLowerInvariant() });
            }
            AppendTx("message sent", -need, $"chat tx {Chain.Txid(spend.Tx)}");
            Save(); Render();
            return (Chain.Serialize(spend.Tx), "");
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    /// <summary>
    /// Publish THIS node's seed to the on-chain NODE-SEED REGISTRY (the well-known address): build a real tx that
    /// pays 1 sat to the registry address (so the record is discoverable by scanning that address) AND carries a
    /// typed PUSHDATA record output {nodePub, endpoint, timestamp, ttl} (no OP_RETURN). Returns the raw tx to
    /// broadcast, or null + a reason. The node re-publishes periodically (before its ttl) to stay listed.
    /// </summary>
    public (byte[]? Raw, string Status) BuildNodeSeedPublish(byte[] nodePub33, string endpoint, int ttlSeconds, long fee = 1)
    {
        if (_locked) return (null, "🔒 Unlock the wallet first.");
        var w = new OnChainWallet(_seed);
        foreach (var u in _w.Utxos.Where(u => !u.Spent && !u.DoubleSpent && !u.WatchOnly && IsRealCoin(u))) w.Add(new OnChainWallet.Utxo(u.Txid, u.Vout, u.Value, u.KeyChain, u.KeyIndex));
        byte[] recordOut, registryOut;
        try
        {
            recordOut = NodeSeedRegistry.BuildRecordOutput(nodePub33, endpoint, ttlSeconds);
            registryOut = NodeSeedRegistry.RegistryMarkerLock(NodeSeedRegistry.RegistryAddressMainnet);
        }
        catch (Exception ex) { return (null, "bad node-seed record: " + ex.Message); }
        var outs = new List<(byte[] Script, long Value)>
        {
            (registryOut, NodeSeedRegistry.RegistryMarkerSats),   // 1-sat marker to the registry address (discoverable)
            (recordOut, 1),                                       // the typed record output (the data), 1 sat
        };
        long need = outs.Sum(o => o.Value) + fee;
        if (w.Balance < need) return (null, $"Insufficient SPENDABLE sats to publish node seed: have {w.Balance:N0}, need {need:N0}.");
        try
        {
            var spend = w.BuildActionMany(outs, fee);
            foreach (var inp in spend.Inputs)
                foreach (var u in _w.Utxos.Where(u => u.Txid == inp.Txid && u.Vout == inp.Vout)) u.Spent = true;
            if (spend.Change > 0)
            {
                uint cvout = (uint)(spend.Tx.Outs.Count - 1);
                var ctxid = Chain.Txid(spend.Tx);
                if (!_w.Utxos.Any(u => u.Txid == ctxid && u.Vout == cvout))
                    _w.Utxos.Add(new UtxoRec { Txid = ctxid, Vout = cvout, Value = spend.Change, KeyChain = 1, KeyIndex = 0,
                        Confirmed = false, RawTxHex = Convert.ToHexString(Chain.Serialize(spend.Tx)).ToLowerInvariant() });
            }
            AppendTx("node seed published", -need, $"registry {NodeSeedRegistry.RegistryAddressMainnet} · tx {Chain.Txid(spend.Tx)}");
            Save(); Render();
            return (Chain.Serialize(spend.Tx), "");
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    /// <summary>Broadcast a raw tx the reliable way (ElectrumSVP SPV server + P2P backup). Public so game engines
    /// (e.g. the Blackjack pot funding/settlement) can land their real on-chain transactions.</summary>
    public System.Threading.Tasks.Task<(bool Ok, string Info)> BroadcastRaw(byte[] raw) => BroadcastEverywhere(raw);

    /// <summary>ASK THE MINER (BSV first-seen): submit the tx to an SPV server / miner and return true ONLY if it is
    /// accepted (the server returns its txid) — i.e. the miner has THIS spend of those inputs. If a conflicting tx
    /// reached the miner first, this one is rejected (a real txid is not returned) and we know it is a DOUBLE-SPEND.
    /// This is how a pot stake is verified for real: not "is it well-formed" but "does the miner actually hold it".</summary>
    public async System.Threading.Tasks.Task<bool> VerifyAcceptedByMiner(byte[] raw)
    {
        try
        {
            using var cli = new ElectrumSvpClient();
            if (!await cli.ConnectAnyAsync(ElectrumSvpClient.ServersFor(_net().Network))) return false;   // no miner reachable ⇒ cannot verify ⇒ do not risk it
            var returned = await cli.BroadcastAsync(Convert.ToHexString(raw).ToLowerInvariant());
            return !string.IsNullOrWhiteSpace(returned) && returned.Trim().Length >= 64;   // a 64-hex txid = accepted/first-seen; any error string = conflict/double-spend
        }
        catch { return false; }
    }

    /// <summary>A best-effort current chain height: the highest confirmation this wallet has seen, floored at a
    /// 2026 mainnet baseline. Used to set a future nLockTime for the Blackjack pot's pre-signed refund.</summary>
    public uint ApproxTipHeight => (uint)Math.Max(920_000, _w.History.Select(h => h.Height).DefaultIfEmpty(0).Max());

    /// <summary>Build the node-seed registry tx AND record it in this wallet (so it appears in Transactions
    /// immediately), returning the raw tx + its txid. Broadcast it with <see cref="PublishNodeSeedBroadcastAsync"/>.</summary>
    public (byte[]? Raw, string Txid, string Status) BuildAndRecordNodeSeed(byte[] nodePub33, string endpoint, int ttlSeconds)
    {
        var (raw, status) = BuildNodeSeedPublish(nodePub33, endpoint, ttlSeconds);   // builds, marks spent, adds change, AppendTx, Save
        if (raw == null) return (null, "", status);
        return (raw, Chain.Txid(Chain.Deserialize(raw)), "");
    }

    /// <summary>Actually land the node-seed tx the SAME reliable way every send goes out — the ElectrumSVP SPV
    /// server (returns the txid), with the P2P node as backup — and report success/failure (never silent).</summary>
    public async System.Threading.Tasks.Task<(bool Ok, string Info)> PublishNodeSeedBroadcastAsync(byte[] raw)
    {
        var (ok, info) = await BroadcastEverywhere(raw);
        try { await Dispatcher.InvokeAsync(() => Notify(ok ? $"Node seed broadcast on-chain (tx {info[..Math.Min(12, info.Length)]}…) to registry {NodeSeedRegistry.RegistryAddressMainnet}." : "Node seed broadcast failed: " + info + " (the wallet will keep retrying automatically).")); } catch { }
        return (ok, info);
    }

    /// <summary>ONE-CLICK bot funding from THIS wallet: build, sign and broadcast a real on-chain payment of
    /// <paramref name="amountSat"/> to the bot's address, and return the funding transaction so the caller credits
    /// the bot from it immediately (no SPV-envelope cut-and-paste). The funds are real and on-chain; the bot
    /// refunds the balance back to this wallet when its window is closed.</summary>
    public async System.Threading.Tasks.Task<Core.Chain.Tx?> FundBotAsync(string botAddress, long amountSat)
    {
        if (!Guard()) return null;
        byte[] h160;
        try { var p = Base58.CheckDecode(botAddress); if (p.Length != 21) throw new Exception("bad address"); h160 = p[1..]; }
        catch { _status.Text = "The bot address is not a valid P2PKH address."; return null; }
        var (raw, status) = FundTx(Chain.P2pkhLock(h160), amountSat, EstimateFee(1));
        if (raw == null) { MessageBox.Show("Could not fund the bot: " + status, "Fund bot"); return null; }
        var (ok, info) = await BroadcastEverywhere(raw);
        Notify(ok ? $"Funded your bot with {amountSat:N0} sat (tx {Chain.Txid(Chain.Deserialize(raw))[..12]}…)." : "Bot funding broadcast issue: " + info);
        Save(); Render();
        return Chain.Deserialize(raw);
    }

    /// <summary>
    /// Play a heads-up Texas Hold'em hand vs YOUR BOT entirely ON-CHAIN — no off-chain deck or messaging of any
    /// kind. Builds the full <see cref="OnChainHandTape"/> (table/game/hand genesis, the funded 2-of-2 pot, the
    /// mental-poker shuffle, every card dealt, every board street, every bet, the showdown reveals, and the real
    /// settlement) and BROADCASTS every step. The bot is the second seat (its key derived from your seed, like the
    /// Blackjack dealer). Returns the tape so the UI can REPLAY it move-by-move, plus a plain win/loss result.
    /// </summary>
    public async System.Threading.Tasks.Task<(string Message, BsvPoker.Core.Games.OnChainHandTape.Tape? Tape, bool YouWon)> RunOnChainHoldemVsBot(long pot)
    {
        if (_locked || _seed.Length != 32) return ("Unlock your wallet first.", null, false);
        if (pot <= 0) pot = 20_000;
        var w = new OnChainWallet(_seed);
        foreach (var u in _w.Utxos.Where(u => !u.Spent && !u.DoubleSpent && !u.WatchOnly && IsRealCoin(u)))
            w.Add(new OnChainWallet.Utxo(u.Txid, u.Vout, u.Value, u.KeyChain, u.KeyIndex));
        long need = pot + 4000;   // the pot + ~20 step txs at ~1-sat fees + buffer
        if (w.Balance < need) return ($"Need ≥ {need:N0} sat spendable to play your bot ON-CHAIN (have {w.Balance:N0}). Fund your wallet first — all bot play is on-chain, no off-chain anything.", null, false);
        // SEAT 0 = you (your identity key); SEAT 1 = your bot (a key derived from your seed). Both real keys.
        var me = (_identityPriv, _identityPub);
        var botK = WalletKeys.Account(_seed, 8, 0);
        var bot = (botK.Priv, botK.Pub);
        var deck = MentalPoker.ShuffledFrom(new[] { MentalPoker.FreshEntropy(), MentalPoker.FreshEntropy() }, Variants.CardSet(Variant.TexasHoldem));

        BsvPoker.Core.Games.OnChainHandTape.Tape tape;
        try { tape = BsvPoker.Core.Games.OnChainHandTape.BuildHoldem(w, me, bot, deck, pot, new byte[16], stepValue: 1, fee: 1); }
        catch (Exception ex) { return ("Could not build the on-chain hand: " + ex.Message, null, false); }
        LastTape = tape;   // make this on-chain hand replayable in the Replay tab

        // your hole cards become encrypted NFTs sealed to your identity (NFTs tab)
        try
        {
            foreach (var c in new[] { deck[0], deck[1] })
                _vault.AddSealed(CardNft.SealToPub(c.Index, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), _identityPub));
            await Dispatcher.InvokeAsync(() => { RefreshCards(); Render(); });
        }
        catch { }

        int sent = 0;
        foreach (var step in tape.Steps) { try { var (ok, _) = await BroadcastEverywhere(Chain.Serialize(step.Tx)); if (ok) sent++; } catch { } }
        _ = SpvServerDiscoverAsync();   // re-sync the balance from the chain (authoritative)

        bool youWon = tape.WinnerSeat == 0;
        string verdict = tape.Split ? "SPLIT POT" : youWon ? "YOU WIN" : "your bot wins";
        var msg = $"On-chain hand vs your bot: {tape.Steps.Count} transactions broadcast ({sent} accepted) — every move on-chain, no off-chain anything.\nResult: {verdict} · pot {tape.Pot:N0} sat. Open the Replay tab to step through it move by move.";
        return (msg, tape, youWon);
    }

    /// <summary>Human-readable label for a card (rank + suit), e.g. "A♠" — used when surfacing a dealt hand.</summary>
    private static string CardLabel(BsvPoker.Core.Card c)
    {
        string r = c.Rank switch { 14 => "A", 13 => "K", 12 => "Q", 11 => "J", 10 => "T", _ => c.Rank.ToString() };
        string s = c.Suit switch { BsvPoker.Core.Suit.Spades => "♠", BsvPoker.Core.Suit.Hearts => "♥", BsvPoker.Core.Suit.Diamonds => "♦", _ => "♣" };
        return r + s;
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
                var (raw, status) = FundTx(script, 1, 1);                       // 1-sat NFT + 1-sat fee (not 500)
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

    /// <summary>Broadcast a raw tx over our own P2P node to the public BSV peers (and to miners' nodes). Pure
    /// peer-to-peer — no third-party server. Returns (ok, info).</summary>
    private async System.Threading.Tasks.Task<(bool Ok, string Info)> BroadcastEverywhere(byte[] raw)
    {
        var rawHex = Convert.ToHexString(raw).ToLowerInvariant();
        var txid = Chain.Txid(Chain.Deserialize(raw));
        // PRIMARY: broadcast via an ElectrumSVP SPV server (reliable, returns the txid; the same servers that
        // serve our balances). This is how a send actually LANDS in seconds.
        string serverInfo = "";
        try
        {
            using var cli = new ElectrumSvpClient();
            if (await cli.ConnectAnyAsync(ElectrumSvpClient.ServersFor(_net().Network)))
            {
                var returned = await cli.BroadcastAsync(rawHex);
                if (!string.IsNullOrWhiteSpace(returned) && returned.Length >= 64) return (true, returned);
                serverInfo = returned;   // server returned an error string
            }
        }
        catch (Exception e) { serverInfo = e.Message; }
        // BACKUP: relay over our own P2P node to public BSV peers + miners.
        var node = _node();
        if (node != null && node.PeerCount > 0)
        {
            try { node.Broadcast(raw); return (true, txid); } catch (Exception e) { serverInfo = serverInfo + " | p2p: " + e.Message; }
        }
        return (false, serverInfo.Length > 0 ? serverInfo : "no SPV server and no BSV peers available to broadcast");
    }

    /// <summary>The receive key for the given receive index (chain 0).</summary>
    private WalletKeys RecvKey(uint index) => WalletKeys.Account(_seed, 0, index);

    /// <summary>The lowest receive index (chain 0) that has NOT already received a coin — addresses are SINGLE-USE,
    /// so a funded address is never shown or reused again.</summary>
    private uint NextUnusedRecvIndex()
    {
        var used = new HashSet<uint>(_w.Utxos.Where(u => u.KeyChain == 0).Select(u => u.KeyIndex));
        uint i = (uint)Math.Max(0, _w.RecvIndex);
        while (used.Contains(i)) i++;
        return i;
    }

    private string ReceiveAddress()
    {
        // single-use: if the current receive address has already been funded, advance to a fresh one (and persist
        // that advance once — not on every render, because once advanced the new address is unused until funded).
        uint idx = NextUnusedRecvIndex();
        if (idx != (uint)_w.RecvIndex) { _w.RecvIndex = (int)idx; Save(); }
        var pub = RecvKey(idx).Pub;
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
        sp.Children.Add(new TextBlock { Text = "Funding transaction (raw hex):", Foreground = Brushes.Gainsboro }); sp.Children.Add(txBox);
        sp.Children.Add(new TextBlock { Text = "Merkleblock proof (raw hex):", Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 8, 0, 0) }); sp.Children.Add(mbBox);
        sp.Children.Add(new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Children = { new TextBlock { Text = "Output index paying me: ", Foreground = Brushes.Gainsboro, VerticalAlignment = VerticalAlignment.Center }, voutBox } });
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
                var rec = new UtxoRec { Txid = utxo.Txid, Vout = utxo.Vout, Value = utxo.Value, KeyChain = utxo.KeyChain, KeyIndex = utxo.KeyIndex, RawTxHex = txBox.Text.Trim().ToLowerInvariant(), MerkleBlockHex = mbBox.Text.Trim().ToLowerInvariant() };
                rec.Confirmed = ReverifyProof(rec);   // SAVE the proof; confirmed only if it re-verifies
                _w.Utxos.Add(rec);
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
        var blkBox = new TextBox { Width = 520, FontFamily = new FontFamily("Consolas") }; ThemeOne(blkBox);
        var go = new Button { Content = "Request SPV proof from peers", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = "Hash of the block that confirmed your payment:", Foreground = Ink }); sp.Children.Add(blkBox);
        sp.Children.Add(new TextBlock { Text = "We ask our connected public BSV node(s) for a merkleblock (compact SPV proof) of that block, matching your addresses. The coin is credited when the proof arrives and verifies — pure peer-to-peer, no server. (No block hash? Use 'Import funding (SPV envelope)'.)", Foreground = SubInk, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, Margin = new Thickness(0, 6, 0, 6) });
        sp.Children.Add(go);
        var win = new Window { Title = "Find a payment (SPV proof from peers)", Width = 580, Height = 300, Owner = Window.GetWindow(this), Background = WinBg, Content = new ScrollViewer { Content = sp } };
        go.Click += (_, _) =>
        {
            var blk = blkBox.Text.Trim().ToLowerInvariant();
            if (blk.Length != 64) { MessageBox.Show("Enter a 64-hex-character block hash.", "Find payment"); return; }
            var node = _node();
            if (node == null || node.PeerCount == 0) { MessageBox.Show("No BSV peers connected yet — wait for the SPV node to connect, then retry.", "No peers"); return; }
            RescanRequested?.Invoke();                                    // ensure our bloom filter is loaded on peers
            node.RequestFilteredBlocks(new[] { blk });                    // ask peers for the merkleblock of that block
            _status.Text = "Requested the SPV proof for that block from peers — your coin will appear when the merkleblock arrives and verifies.";
            win.Close();
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
        sp.Children.Add(new TextBlock { Text = "Funding transaction (raw hex):", Foreground = Brushes.Gainsboro }); sp.Children.Add(txBox);
        sp.Children.Add(new TextBlock { Text = "Confirming block hash (the block that mined it):", Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 8, 0, 0) }); sp.Children.Add(blkBox);
        sp.Children.Add(new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Children = { new TextBlock { Text = "Output index paying the recipient: ", Foreground = Brushes.Gainsboro, VerticalAlignment = VerticalAlignment.Center }, voutBox } });
        sp.Children.Add(go);
        sp.Children.Add(new TextBlock { Text = "Envelope to hand to the recipient (merkleblock hex):", Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 8, 0, 0) }); sp.Children.Add(outBox);
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
            foreach (var u in _w.Utxos.Where(u => !u.Spent && !u.WatchOnly)) wallet.Add(new OnChainWallet.Utxo(u.Txid, u.Vout, u.Value, u.KeyChain, u.KeyIndex));
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
        var win = new Window { Title = "Sign a message with your key", Width = 500, Height = 220, Owner = Window.GetWindow(this), Content = new StackPanel { Margin = new Thickness(12), Children = { new TextBlock { Text = "Message:", Foreground = Brushes.Gainsboro }, msg, go } } };
        go.Click += (_, _) =>
        {
            // Sign AS YOUR IDENTITY — the SAME key the Identity tab shows as "Identity public key (share this)"
            // and that everything else (chat, the game, certificates) treats as you. Previously this signed with
            // a different account sub-key, so a message you signed "as your identity" would NOT verify against
            // your identity key — that is the "verify does not work for identity" failure. Now they match.
            var sig = WalletExtras.SignMessage(_identityPriv, msg.Text);
            MessageBox.Show($"identity pubkey (verify against this):\n{Convert.ToHexString(_identityPub).ToLowerInvariant()}\n\nsignature (base64):\n{sig}", "Signed by your identity");
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
        sp.Children.Add(new TextBlock { Text = "Signer pubkey (hex):", Foreground = Brushes.Gainsboro }); sp.Children.Add(pub);
        sp.Children.Add(new TextBlock { Text = "Message:", Foreground = Brushes.Gainsboro }); sp.Children.Add(msg);
        sp.Children.Add(new TextBlock { Text = "Signature (base64):", Foreground = Brushes.Gainsboro }); sp.Children.Add(sig);
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

    /// <summary>The login a wallet REQUIRES before it can be used — a pure, headless-testable decision so the
    /// "no wallet without a login" rule can be verified 100×. There is NO outcome that opens a wallet with no
    /// login: an encrypted seed needs Unlock (password); a plaintext existing seed MUST set a password
    /// (SetPassword); an absent/invalid seed is a brand-new wallet that runs the create wizard (NewWizard).</summary>
    public enum StartupLogin { Unlock, SetPassword, NewWizard }
    public static StartupLogin DecideLogin(string? seedField)
    {
        if (WalletExtras.IsEncryptedSeed(seedField ?? "")) return StartupLogin.Unlock;
        try { if (WalletKeys.BackupToSeed(seedField ?? "").Length == 32) return StartupLogin.SetPassword; } catch { }
        return StartupLogin.NewWizard;
    }

    private void Load()
    {
        try { if (File.Exists(_path)) _w = JsonSerializer.Deserialize<File_>(File.ReadAllText(_path)) ?? new File_(); } catch { _w = new File_(); }
        // #1 RULE — PROTECT REGISTERED ACCOUNTS: a registered identity is NEVER deleted, downgraded, or
        // un-registered, EVER. We do NOT drop it even if its stored signature fails to re-verify — a version change
        // to the canonical form or key derivation can invalidate an OLD signature without the identity being fake,
        // and silently wiping a registered account (e.g. CsTominaga) is an abject breach. Once registered, forever
        // registered. If the wallet is UNLOCKED and the signature is stale, we REFRESH it with this wallet's own
        // identity key (it is our identity) instead of ever deleting it; while locked we simply keep it untouched.
        if (_w.Identity is { } existing && existing.Pseudonym.Length > 0)
        {
            try
            {
                if (_seed.Length == 32 && _identityPriv.Length == 32
                    && !WalletExtras.VerifyMessage(_identityPub, existing.Canonical(), existing.Signature))
                    existing.Signature = WalletExtras.SignMessage(_identityPriv, existing.Canonical());
            }
            catch { /* never drop a real identity on an error */ }
        }
        // A coin is CONFIRMED only if its SAVED SPV proof re-verifies (offline). Set that first.
        foreach (var u in _w.Utxos) u.Confirmed = ReverifyProof(u);
        // FRAUD MUST NOT EXIST: the optimistic Announce/FundTx machinery fabricated "coins" with NO transaction
        // and NO proof — pure local fraud, never on-chain. Remove them entirely so they DO NOT EXIST. This is
        // NOT deleting a real coin, a key, or the seed: anything with on-chain data (a raw tx OR a merkle/envelope
        // proof) or a watch address is KEPT, and every prior state remains in the read-only claude\backups vault.
        _w.Utxos.RemoveAll(u => !u.WatchOnly && !u.Confirmed
            && string.IsNullOrEmpty(u.RawTxHex) && string.IsNullOrEmpty(u.MerkleBlockHex) && string.IsNullOrEmpty(u.EnvelopeWire));
        // PERSIST the fraud-detection cleanup (dropped draft identity + removed phantom UTXOs) NOW: the Unlock /
        // SetPassword paths below return early, so without this Save() the mutation would be lost and re-done every
        // run — and the backup-every-write rule would be skipped. Safe even while locked (Save only writes _w).
        if (File.Exists(_path)) { try { Save(); } catch { } }
        // EVERY path requires a login — there is no no-login outcome (DecideLogin proves this, tested 100×).
        switch (DecideLogin(_w.Seed))
        {
            case StartupLogin.Unlock:
                _locked = true;            // encrypted on disk — must be unlocked with the password before use
                Unlock();                  // prompt now (modal); if cancelled, the wallet stays locked
                return;
            case StartupLogin.SetPassword:
                // SECURITY (no-login is fraud): an EXISTING plaintext wallet MUST be password-protected before
                // use. Force a password now and encrypt the SAME seed in place — seed/addresses/coins RETAINED.
                _seed = WalletKeys.BackupToSeed(_w.Seed);
                RequirePasswordForExistingWallet();
                return;
            default: // NewWizard — brand-new wallet: a fresh seed, EMPTY (no play money, no opening balance).
                _seed = WalletKeys.NewSeed();
                _w = new File_ { Seed = WalletKeys.SeedToBackup(_seed), RecvIndex = 0 };
                _freshWallet = true;       // first run → run the ElectrumSVP-style account wizard
                Save();
                return;
        }
    }

    /// <summary>Mandatory at startup for an existing plaintext wallet: set a password and encrypt the EXISTING
    /// seed in place (funds/keys retained). The user cannot reach the wallet without doing this; Cancel exits
    /// the app rather than leaving an unprotected wallet open.</summary>
    private void RequirePasswordForExistingWallet()
    {
        var pb = new PasswordBox { Width = 320 };
        var pb2 = new PasswordBox { Width = 320 };
        var err = new TextBlock { Foreground = Brushes.IndianRed, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        var ok = new Button { Content = "Set password", Margin = new Thickness(0, 12, 8, 0), Padding = new Thickness(16, 6, 16, 6), IsDefault = true };
        var cancel = new Button { Content = "Quit", Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(16, 6, 16, 6), IsCancel = true };
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock { Text = "🔒 Secure your wallet", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Ink });
        sp.Children.Add(new TextBlock { Text = "Your wallet has no password. Set one now to encrypt your keys — your existing seed and all your funds are kept; nothing is lost. You'll enter this password each time you open the wallet.", Foreground = SubInk, TextWrapping = TextWrapping.Wrap, MaxWidth = 320, Margin = new Thickness(0, 4, 0, 8) });
        sp.Children.Add(new TextBlock { Text = "new password", Foreground = SubInk, FontSize = 12 }); sp.Children.Add(pb);
        sp.Children.Add(new TextBlock { Text = "confirm password", Foreground = SubInk, FontSize = 12 }); sp.Children.Add(pb2);
        sp.Children.Add(err);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(ok); row.Children.Add(cancel); sp.Children.Add(row);
        var win = new Window { Title = "Set wallet password", SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = (Brush)FindResource("Bg"), Content = sp };
        ok.Click += (_, _) =>
        {
            if (pb.Password.Length < 1) { err.Text = "enter a password"; return; }
            if (pb.Password != pb2.Password) { err.Text = "passwords do not match"; return; }
            // encrypt the EXISTING seed in place — same seed, same funds
            _w.Seed = WalletExtras.EncryptSeed(WalletKeys.SeedToBackup(_seed), pb.Password);
            Save();
            win.DialogResult = true;
        };
        var result = win.ShowDialog();
        if (result != true)
        {
            // refused to set a password → do NOT leave an unprotected wallet accessible
            _locked = true; _seed = Array.Empty<byte>();
            Application.Current?.Shutdown();
        }
    }

    /// <summary>Prompt for the wallet password and decrypt the seed into memory. Loops until correct or cancelled.</summary>
    /// <summary>The login screen: a styled password prompt with inline retry (ElectrumSVP message), shown for an
    /// encrypted wallet. Stays open until the right password is entered or the user cancels.</summary>
    private void Unlock()
    {
        if (!_locked) return;
        var who = _w.Identity is { } id ? $"  ({id.DisplayName} · @{id.Pseudonym})" : "";
        var pb = new PasswordBox { Width = 320 };
        var err = new TextBlock { Foreground = Brushes.IndianRed, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        var ok = new Button { Content = "Unlock", Margin = new Thickness(0, 12, 8, 0), Padding = new Thickness(16, 6, 16, 6), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(16, 6, 16, 6), IsCancel = true };
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock { Text = "🔒 Unlock your wallet", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Ink });
        sp.Children.Add(new TextBlock { Text = $"Your wallet has a password — enter it to access it{who}.", Foreground = SubInk, TextWrapping = TextWrapping.Wrap, MaxWidth = 320, Margin = new Thickness(0, 4, 0, 8) });
        sp.Children.Add(pb); sp.Children.Add(err);
        sp.Children.Add(new WrapPanel { Children = { ok, cancel } });
        var win = new Window { Title = "Unlock wallet", Width = 380, Height = 230, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = WinBg, Content = sp, ResizeMode = ResizeMode.NoResize };
        if (Window.GetWindow(this) is { } owner && owner.IsLoaded) win.Owner = owner;
        ok.Click += (_, _) =>
        {
            try { _seed = WalletKeys.BackupToSeed(WalletExtras.DecryptSeed(_w.Seed, pb.Password)); _locked = false; RestoreIdentityFromCache(); OnUnlocked?.Invoke(); win.DialogResult = true; Render(); }
            catch { err.Text = "Wrong password — try again."; pb.Clear(); pb.Focus(); }
        };
        pb.Loaded += (_, _) => pb.Focus();
        win.ShowDialog();
        // NO WALLET, NO PROGRAM: if the wallet was not unlocked (cancelled / closed), the app ends — there is no
        // application without an open wallet (ElectrumSVP model).
        if (_locked) Application.Current?.Shutdown();
    }

    // ===================== PERMANENT identity (registered once on-chain = registered FOREVER) =====================
    // The identity is the seed-derived key (Type-42 "bsvpoker/identity"); registration anchors it ON-CHAIN. Once
    // registered it can NEVER be un-registered and the optional profile fields are the only thing that may change.
    // We cache the registration in a SHARED file keyed by the identity public key (NOT per wallet-file, NOT per
    // network), so a re-login — or the same seed in any profile, on mainnet OR testnet — ALWAYS finds it and shows
    // registered. This guarantees the principal's rule: set once, registered forever, everywhere.
    private static string IdentityCacheDir()
    {
        var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BsvPoker", "identities");
        Directory.CreateDirectory(d); return d;
    }
    private string IdentityCachePath() => Path.Combine(IdentityCacheDir(), Convert.ToHexString(_identityPub).ToLowerInvariant() + ".json");
    private void PersistIdentityToCache()
    {
        // Cache ANY set identity (on-chain OR self-signed local) keyed by the identity key, so the SAME seed always
        // finds its identity again — set once, yours forever. (Previously only on-chain identities were cached.)
        try { if (_seed.Length == 32 && _w.Identity is { Pseudonym.Length: > 0 } id)
            File.WriteAllText(IdentityCachePath(), JsonSerializer.Serialize(id, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }
    /// <summary>On unlock: if the wallet-file lost the registration but this identity key set one before (cached),
    /// RESTORE it — set once, yours forever, on-chain or not. If the wallet already has it, refresh the cache.</summary>
    private void RestoreIdentityFromCache()
    {
        try
        {
            if (_seed.Length != 32) return;
            if (_w.Identity is { Pseudonym.Length: > 0 }) { PersistIdentityToCache(); return; }
            var path = IdentityCachePath();
            if (!File.Exists(path)) return;
            var id = JsonSerializer.Deserialize<Registration>(File.ReadAllText(path));
            if (id is { Pseudonym.Length: > 0 }) { _w.Identity = id; if (string.IsNullOrEmpty(_w.Handle)) _w.Handle = id.Pseudonym; Save(); }
        }
        catch { }
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

    /// <summary>Password strength, ElectrumSV-style: entropy from length + digits + mixed case + symbols.</summary>
    private static (string Label, Brush Colour) PasswordStrength(string pw)
    {
        if (pw.Length == 0) return ("", Brushes.Gainsboro);
        double bits = 0; int sets = 0;
        if (pw.Any(char.IsLower)) { sets += 26; }
        if (pw.Any(char.IsUpper)) { sets += 26; }
        if (pw.Any(char.IsDigit)) { sets += 10; }
        if (pw.Any(c => !char.IsLetterOrDigit(c))) { sets += 33; }
        if (sets > 0) bits = pw.Length * Math.Log2(sets);
        if (bits < 60) return ("Weak", Brushes.IndianRed);
        if (bits < 90) return ("Medium", Brushes.Goldenrod);
        if (bits < 120) return ("Strong", Brushes.MediumSeaGreen);
        return ("Very strong", Brushes.LimeGreen);
    }

    /// <summary>
    /// Set / change the wallet password — ElectrumSV's ChangePasswordDialog UX: New + Confirm fields, a live
    /// strength meter, and an OK that stays disabled until the two entries match. Encrypts the seed at rest
    /// (AES-GCM via WalletExtras). Leaving both blank removes the password.
    /// </summary>
    private void SetPassword()
    {
        if (_locked) { MessageBox.Show("Unlock the wallet first.", "Wallet locked"); return; }
        bool has = WalletExtras.IsEncryptedSeed(_w.Seed);
        var pw1 = new PasswordBox { Width = 320 };
        var pw2 = new PasswordBox { Width = 320 };
        var strength = new TextBlock { Margin = new Thickness(0, 4, 0, 0), FontWeight = FontWeights.Bold };
        var ok = new Button { Content = "OK", Margin = new Thickness(0, 12, 8, 0), Padding = new Thickness(16, 6, 16, 6), IsDefault = true, IsEnabled = false };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(16, 6, 16, 6), IsCancel = true };
        void Recheck()
        {
            var (lbl, col) = PasswordStrength(pw1.Password);
            strength.Text = pw1.Password.Length == 0 ? "(blank = remove password)" : $"Strength: {lbl}";
            strength.Foreground = pw1.Password.Length == 0 ? SubInk : col;
            ok.IsEnabled = pw1.Password == pw2.Password;   // OK disabled until they match (ElectrumSV behaviour)
        }
        pw1.PasswordChanged += (_, _) => Recheck(); pw2.PasswordChanged += (_, _) => Recheck();
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock { Text = has ? "Your wallet is password protected. Use this dialog to change your password." : "Your wallet needs a password. Use this dialog to set your password.", Foreground = Ink, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 });
        sp.Children.Add(new TextBlock { Text = "New password:", Foreground = SubInk, Margin = new Thickness(0, 10, 0, 2) }); sp.Children.Add(pw1);
        sp.Children.Add(new TextBlock { Text = "Confirm password:", Foreground = SubInk, Margin = new Thickness(0, 8, 0, 2) }); sp.Children.Add(pw2);
        sp.Children.Add(strength);
        sp.Children.Add(new WrapPanel { Children = { ok, cancel } });
        var win = new Window { Title = "Wallet password", Width = 380, Height = 280, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = WinBg, Content = sp, ResizeMode = ResizeMode.NoResize };
        if (Window.GetWindow(this) is { } owner && owner.IsLoaded) win.Owner = owner;
        Recheck();
        ok.Click += (_, _) => win.DialogResult = true;
        if (win.ShowDialog() != true) return;
        if (pw1.Password.Length == 0) { _w.Seed = WalletKeys.SeedToBackup(_seed); Save(); _status.Text = "Wallet password removed — seed stored in the clear."; return; }
        _w.Seed = WalletExtras.EncryptSeed(WalletKeys.SeedToBackup(_seed), pw1.Password); Save();
        _status.Text = "Wallet encrypted — your seed is now password-protected at rest.";
    }

    private void Restore()
    {
        // ElectrumSVP-style restore: the seed is validated LIVE as you type; Restore stays disabled until the
        // seed is a valid wallet backup. After restoring, the identity must still be registered (every step).
        var box = new TextBox { AcceptsReturn = true, Height = 80, Width = 460, TextWrapping = TextWrapping.Wrap }; ThemeOne(box);
        var status = new TextBlock { Margin = new Thickness(0, 6, 0, 0), FontWeight = FontWeights.Bold };
        var ok = new Button { Content = "Restore", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 6, 12, 6), IsEnabled = false };
        void Check()
        {
            try { var s = WalletKeys.BackupToSeed(box.Text.Trim()); var good = s.Length == 32; ok.IsEnabled = good; status.Text = good ? "✔ valid wallet seed" : "not a valid seed"; status.Foreground = good ? Accent : Brushes.IndianRed; }
            catch { ok.IsEnabled = false; status.Text = box.Text.Trim().Length == 0 ? "" : "✖ not a valid wallet seed backup"; status.Foreground = Brushes.IndianRed; }
        }
        box.TextChanged += (_, _) => Check();
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = "Enter your wallet seed backup (Base58Check):", Foreground = Ink });
        sp.Children.Add(box); sp.Children.Add(status); sp.Children.Add(ok);
        var win = new Window { Title = "Restore wallet — enter your seed backup", Width = 500, Height = 240, Owner = Window.GetWindow(this), Background = WinBg, Content = new ScrollViewer { Content = sp } };
        ok.Click += (_, _) =>
        {
            try
            {
                _seed = WalletKeys.BackupToSeed(box.Text.Trim());
                _w = new File_ { Seed = WalletKeys.SeedToBackup(_seed), RecvIndex = 0 };
                _locked = false; RestoreIdentityFromCache(); OnUnlocked?.Invoke(); Save(); Render(); win.Close();
                // Do NOT force registration. A restored wallet's identity is re-discovered from its seed/cache (and
                // re-confirmed on-chain); if it was registered before, it stays registered. If not, the user
                // registers when funded — never forced before they can do anything.
                _status.Text = "Wallet restored from seed.";
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Invalid seed"); }
        };
        win.ShowDialog();
    }

    // EVERY action this wallet performs is recorded here the INSTANT it happens, so the user SEES it immediately
    // in the Transactions tab — chat sent, a bet, a deal step, identity, a payment sent, a coin received. These
    // are all REAL events (each call site has built/broadcast a real tx or detected a real coin), so nothing
    // fake is stored. The confirmed-balance History tab is still derived from SPV (DerivedHistory); this is the
    // live activity log that answers "I'm not seeing transactions occurring".
    private void AppendTx(string type, long amount, string memo)
    {
        try
        {
            // light dedup: SPV scans are edge-triggered, but never log the exact same event twice in a row
            if (_w.Activity.Count > 0 && _w.Activity[0].Type == type && _w.Activity[0].Memo == memo && _w.Activity[0].Amount == amount) return;
            _w.Activity.Insert(0, new Tx { Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Type = type, Amount = amount, Memo = memo });
            if (_w.Activity.Count > 2000) _w.Activity.RemoveRange(2000, _w.Activity.Count - 2000);
            Save();
            if (Dispatcher.CheckAccess()) RefreshActivityGrid();
            else Dispatcher.BeginInvoke(new Action(RefreshActivityGrid));
        }
        catch { }
    }

    /// <summary>Populate the Transactions tab: the live ACTIVITY log (every action, newest first) followed by any
    /// pending/unconfirmed received coins. This is what makes every action visible immediately.</summary>
    private void RefreshActivityGrid()
    {
        try
        {
            string TxidIn(string memo) { foreach (var tok in memo.Split(new[] { ' ', ':', '…', '(', ')', '—', '/' }, StringSplitOptions.RemoveEmptyEntries)) if (tok.Length == 64 && tok.All(Uri.IsHexDigit)) return tok; return ""; }
            var rows = new List<PendingRow>();
            foreach (var a in _w.Activity)
                rows.Add(new PendingRow { Status = a.Type, Amount = (a.Amount > 0 ? "+" : "") + a.Amount.ToString("N0"), Memo = $"{a.Time}  {a.Memo}", Txid = TxidIn(a.Memo) });
            foreach (var u in _w.Utxos.Where(u => !u.Confirmed && !u.Spent))
                rows.Add(new PendingRow { Status = "unconfirmed coin", Amount = "+" + u.Value.ToString("N0"), Memo = $"{u.Txid} :{u.Vout}", Txid = u.Txid });
            _pendingGrid.ItemsSource = rows;
        }
        catch { }
    }

    /// <summary>
    /// The wallet history, derived ONLY from REAL on-chain movements the wallet actually saw: every received
    /// coin (SPV-confirmed or pending), and every transaction this wallet actually broadcast (recorded in
    /// _w.Sends). A fresh/unfunded wallet has no coins and no sends, so it shows NOTHING — never invented.
    /// </summary>
    private List<Tx> DerivedHistory()
    {
        // PREFER the FULL server-fetched history (every tx that ever touched our addresses, ElectrumSVP-style) when
        // we have it: ordered oldest→newest (mempool last) with a running balance. Falls back to the local
        // coin/send derivation only before the first server fetch completes.
        if (_w.History.Count > 0)
        {
            var full = _w.History.OrderBy(r => r.Height <= 0 ? int.MaxValue : r.Height).ToList();
            long bal = 0; foreach (var r in full) { bal += r.Amount; r.Balance = bal; }
            return full;
        }
        var rows = new List<Tx>();
        foreach (var u in _w.Utxos)
        {
            // EVERY REAL coin shows in history — confirmed OR 0-conf (a coin backed by an actual tx is real and
            // spendable at risk; it is shown as "pending" until mined). Only pure fabrications (no tx, no proof)
            // are excluded. This is why a freshly received coin appears in history immediately, not only once mined.
            if (!IsRealCoin(u) || u.DoubleSpent) continue;   // never list a fabrication / phantom-change coin
            rows.Add(new Tx {
                Time = u.Confirmed ? "on-chain" : "pending",
                Type = u.WatchOnly ? "watch" : "received",
                Amount = u.Value,
                Balance = 0,
                Memo = $"{u.Txid} :{u.Vout}" + (_w.TxLabels.TryGetValue(u.Txid, out var rl) ? "  — " + rl : ""),
            });
        }
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
        VaultBackup();   // EVERY write produces a new immutable (read-only) backup — never deleted
    }

    /// <summary>The claude\backups vault: the immutable wallet backup root. Walks up from the running location to
    /// the directory named "claude"; falls back to D:\claude\backups.</summary>
    internal static string VaultRoot()
    {
        try
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null) { if (string.Equals(d.Name, "claude", StringComparison.OrdinalIgnoreCase)) return Path.Combine(d.FullName, "backups"); d = d.Parent; }
        }
        catch { }
        return @"D:\claude\backups";
    }

    private static string SafeName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "wallet";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '-');
        return s;
    }

    /// <summary>Copy the current wallet file to the claude\backups vault as a NEW, READ-ONLY, named, never-deleted
    /// file. Called on every write and every run. We would rather a million small files than lose one wallet, so
    /// each backup is unique (100ns timestamp) and never overwritten or removed.</summary>
    internal void VaultBackup()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var root = VaultRoot(); Directory.CreateDirectory(root);
            var name = SafeName(string.IsNullOrWhiteSpace(_w.Handle) ? (_w.Identity?.Pseudonym ?? "wallet") : _w.Handle);
            var dst = Path.Combine(root, $"poker-wallet-{name}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.dat");
            if (File.Exists(dst)) return;                 // never touch an existing (read-only) backup
            File.Copy(_path, dst, overwrite: false);
            try { new FileInfo(dst).IsReadOnly = true; } catch { }   // immutable: never to be written or deleted again
        }
        catch { /* a backup attempt must never crash the wallet, but we attempt it on every write/run */ }
    }

    private bool IsRegistered => _w.Identity != null;

    /// <summary>Block an operation when the wallet is locked OR the identity is not yet registered. Nothing in the
    /// wallet works until the user has registered an identity (the foundation everything links to).</summary>
    private bool Guard()
    {
        if (_locked) { _status.Text = "🔒 Wallet is locked — press “Unlock…” and enter your password."; return false; }
        // Do NOT pop the register dialog here. Order is Wallet → Fund → Identity → Game; a freshly-opened wallet
        // must be usable (receive, back up, view) WITHOUT being forced to register first. Identity is required only
        // for the GAME (CanPlay) and the user registers it from the Identity tab when funded — never auto-forced.
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
        // No usable seed yet → show a safe placeholder and DO NOT derive any address (WalletKeys.Account throws on
        // a non-32-byte seed). Two cases land here: an encrypted wallet before Unlock (_locked), and the brief
        // pre-selection state before the user has picked a wallet (selection is deferred to MainWindow.Loaded).
        if (_locked || _seed.Length != 32)
        {
            _bal.Text = "🔒 locked";
            _sbBalance.Text = "🔒 locked"; _sbLock.Text = "🔒 locked"; _sbNetwork.Text = $"{_net().Network}";
            _recv.Text = _locked
                ? "Wallet is encrypted — press “Unlock…” (Wallet menu) to enter your password."
                : "No wallet open — select a wallet to begin.";
            _historyGrid.ItemsSource = null; _coinsGrid.ItemsSource = null; _addrGrid.ItemsSource = null;
            return;
        }
        if (_ring == null && _seed.Length == 32) _ring = new KeyRing(_seed, Math.Max(1, _w.RecvIndex));
        // BULLETPROOF identity restore + PROTECT REGISTERED ACCOUNTS: if this wallet file lost its identity (wiped by
        // an old bug) but this identity KEY registered before (cached, keyed by the identity pubkey), bring it back
        // NOW — a registered account is never left showing "not registered".
        if (_seed.Length == 32 && (_w.Identity == null || _w.Identity.Pseudonym.Length == 0)) RestoreIdentityFromCache();
        // ALWAYS refresh the identity card so its state can NEVER be stale — a set identity must never keep
        // showing "no identity". Only the card (fresh controls) is rebuilt, so nothing is ever re-parented.
        try { if (_idCard != null) _idCard.Content = BuildIdentityCard(); } catch { }
        try { if (_nftTab != null && !_nftTab.IsKeyboardFocusWithin) _nftTab.Content = BuildNftTab(); } catch { }
        _bal.Text = Balance.ToString("N0") + " sat" + (Pending > 0 ? $"   (+{Pending:N0} pending)" : "");
        _recv.Text = ReceiveAddress() + $"   (#{_w.RecvIndex})";
        // always show a scannable QR of the current receiving address (or the active payment-request URI)
        try { _reqQr.Source = RenderQr(_reqUri.Text.Length > 0 ? _reqUri.Text : "bitcoin:" + ReceiveAddress()); } catch { }

        ApplyHistoryFilter();
        _coinsGrid.ItemsSource = _w.Utxos.Select(u => new {
            Outpoint = $"{u.Txid[..Math.Min(12, u.Txid.Length)]}…:{u.Vout}",
            Address = AddressForKey(u.KeyChain, u.KeyIndex),
            Value = u.Value.ToString("N0"),
            Status = u.WatchOnly ? "watch-only" : (u.Spent ? "spent" : (u.Confirmed ? "confirmed" : "pending")),
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

        _contactsGrid.ItemsSource = _w.Contacts.Select(c => new { Verified = c.Verified ? "✓" : "", c.Handle, c.DisplayName, c.Email, c.IdentityPub }).ToList();
        _vaultsGrid.ItemsSource = _w.Vaults.Select(v => new { v.Name, v.Address, Funded = v.Funded ? v.FundValue.ToString("N0") : "—", v.LockHeight }).ToList();
        _requestsGrid.ItemsSource = _w.Requests.AsEnumerable().Reverse().Select(q => new {
            q.Time, q.Amount, q.Memo, q.Expires, q.Address,
            Status = (string.IsNullOrEmpty(q.Expires) ? "active" : (DateTime.TryParse(q.Expires, out var e) && e < DateTime.Now ? "expired" : "active")),
        }).ToList();

        // Transactions tab: the LIVE activity log (every action) + pending coins
        RefreshActivityGrid();

        if (_ring != null) _idPub.Text = Convert.ToHexString(_identityPub).ToLowerInvariant();
        if (string.IsNullOrEmpty(_idHandle.Text)) _idHandle.Text = _w.Handle;

        UpdateStatusBar();

        // Honest funding guidance + live SPV state (why funds may not show yet, and how to get them).
        var node = _node(); int peers = node?.PeerCount ?? 0; int hdrs = _store()?.Count ?? 0; int tip = node?.BestHeight ?? 0;
        var net = _net().Network;
        _fundInfo.Text =
            $"Send real BSV to your address above (copy it). It appears here once the payment is SPV-verified against block headers this client has synced.\n" +
            $"SPV state: network {net} · peers {peers} · headers synced {hdrs:N0}" + (tip > 0 ? $" of ~{tip:N0}" : "") + ".\n" +
            (peers == 0 ? "⚠ No peers yet — waiting to connect; nothing can sync or be detected until peers connect.\n" : "") +
            (net.ToString() == "Mainnet" ? "Note: mainnet headers sync from genesis and can take a while; a freshly-sent coin shows as PENDING until headers reach its block. For instant testing use the Testnet/Regtest selector in the toolbar (Testnet has free faucet coins).\n" : "") +
            "No real BSV yet? You can also receive via Import funding (SPV envelope) or Find a payment by txid below. This wallet never shows play money.";

        RefreshCards();
    }

    private void UpdateStatusBar()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(UpdateStatusBar)); return; }
        if (_locked) { _sbBalance.Text = "🔒 locked"; _sbLock.Text = "🔒 locked"; _sbNetwork.Text = $"{_net().Network}"; return; }
        // HEADLINE = CONFIRMED (proof-verified, mined) money ONLY — never an unproven/optimistic total. The
        // unconfirmed and double-spent coins live in their own tab; they are never shown as the balance.
        _sbBalance.Text = $"{Balance:N0} sat" + (WatchedBalance > 0 ? $"  ·  watch {WatchedBalance:N0}" : "");
        _sbLock.Text = "🔓 unlocked";
        var node = _node();
        var who = string.IsNullOrWhiteSpace(_w.Handle) ? "" : $"@{_w.Handle} · ";
        _sbNetwork.Text = $"{who}acct #{_accountIndex} · {_net().Network} · SPV node peers: {(node?.PeerCount ?? 0)} · tip {(node?.BestHeight ?? 0):N0}";
    }

    /// <summary>Resolve a peer's identity public key (hex) to a saved contact handle, or null. Lets the chat /
    /// game show @handles instead of raw keys — the wallet's Contacts are the app-wide address book.</summary>
    public string? HandleFor(string identityPubHex)
    {
        // My OWN identity resolves to MY pseudonym/handle (I am never in my own contact list).
        if (string.Equals(identityPubHex, Convert.ToHexString(_identityPub).ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
            return _w.Identity is { Pseudonym.Length: > 0 } me ? me.Pseudonym : (string.IsNullOrWhiteSpace(_w.Handle) ? null : _w.Handle);
        var c = _w.Contacts.FirstOrDefault(x => string.Equals(x.IdentityPub, identityPubHex, StringComparison.OrdinalIgnoreCase));
        return c?.Handle;
    }

    /// <summary>The whole address book as (label, identity pubkey-hex): every saved identity/handle/bot the user
    /// can pick as a chat recipient or group member — the Telegram-style contact list, not just discovered peers.</summary>
    public IReadOnlyList<(string Label, string PubHex)> ContactList()
        => _w.Contacts.Select(c => ((c.Verified && c.DisplayName.Length > 0 ? $"✓ {c.DisplayName} (@{c.Handle})" : "@" + c.Handle), c.IdentityPub.ToLowerInvariant())).ToList();

    /// <summary>All named chat GROUPS the user created — each one a (name, member pubkey-hex list) for the
    /// broadcast-encryption group send mode. Persisted in the wallet so groups survive across sessions.</summary>
    public IReadOnlyList<(string Name, IReadOnlyList<string> Members)> GroupList()
        => _w.Groups.Select(g => (g.Name, (IReadOnlyList<string>)g.MemberPubs.ToList())).ToList();

    /// <summary>Create or replace a named group (members = identity pubkey hex). Persisted to the wallet file.</summary>
    public void SaveGroup(string name, IReadOnlyList<string> memberPubs)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SaveGroup(name, memberPubs)); return; }
        name = name.Trim();
        if (name.Length == 0 || memberPubs.Count == 0) return;
        _w.Groups.RemoveAll(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
        _w.Groups.Add(new ChatGroupRec { Name = name, MemberPubs = memberPubs.Select(p => p.ToLowerInvariant()).Distinct().ToList() });
        Save();
    }

    /// <summary>Delete a named group.</summary>
    public void DeleteGroup(string name)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => DeleteGroup(name)); return; }
        _w.Groups.RemoveAll(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    /// <summary>A friendly, identity-aware label for a peer key: "✓ Bob Smith (@bob)" for a verified contact,
    /// "@bob" for an unverified contact, or null if unknown. Used by chat, the game, and the opponent chooser.</summary>
    public string? IdentityLabelFor(string identityPubHex)
    {
        // My OWN identity → my registered pseudonym (with display name), so the game/chat never show me as raw hex.
        if (string.Equals(identityPubHex, Convert.ToHexString(_identityPub).ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
        {
            if (_w.Identity is { Pseudonym.Length: > 0 } me)
                return me.DisplayName.Length > 0 ? $"{me.DisplayName} (@{me.Pseudonym})" : "@" + me.Pseudonym;
            return string.IsNullOrWhiteSpace(_w.Handle) ? null : "@" + _w.Handle;
        }
        var c = _w.Contacts.FirstOrDefault(x => string.Equals(x.IdentityPub, identityPubHex, StringComparison.OrdinalIgnoreCase));
        if (c == null) return null;
        if (c.Verified && c.DisplayName.Length > 0) return $"✓ {c.DisplayName} (@{c.Handle})";
        return "@" + c.Handle;
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
        try
        {
            var w = new OnChainWallet(_seed);
            foreach (var u in _w.Utxos.Where(u => !u.Spent && !u.Frozen && !u.WatchOnly && IsRealCoin(u))) w.Add(new OnChainWallet.Utxo(u.Txid, u.Vout, u.Value, u.KeyChain, u.KeyIndex));
            var spend = w.BuildActionMany(outputs.Select(o => (o.Lock, o.Value)).ToList(), fee);
            if (!w.VerifySpend(spend)) { _sendStatus.Text = "Built transaction failed self-verification — not broadcast."; return; }
            _sendStatus.Text = "Broadcasting…";
            var (bok, binfo) = await BroadcastEverywhere(Chain.Serialize(spend.Tx));  // broadcast over our own P2P node to public BSV peers + miners
            if (!bok) { _sendStatus.Text = "Broadcast rejected: " + binfo + " (not spent)."; return; }
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
            foreach (var u in _w.Utxos.Where(u => !u.Spent && !u.Frozen && !u.WatchOnly && IsRealCoin(u))) w.Add(new OnChainWallet.Utxo(u.Txid, u.Vout, u.Value, u.KeyChain, u.KeyIndex));
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
        sp.Children.Add(new TextBlock { Text = "Recipient identity (@handle or identity pubkey hex):", Foreground = Brushes.Gainsboro }); sp.Children.Add(to);
        sp.Children.Add(new TextBlock { Text = "Message:", Foreground = Brushes.Gainsboro }); sp.Children.Add(msg);
        sp.Children.Add(go);
        sp.Children.Add(new TextBlock { Text = "Encrypted (base64) — give this to the recipient:", Foreground = Brushes.Gainsboro }); sp.Children.Add(outp);
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
        sp.Children.Add(new TextBlock { Text = "Encrypted message (base64) addressed to your identity:", Foreground = Brushes.Gainsboro }); sp.Children.Add(inp);
        sp.Children.Add(go);
        sp.Children.Add(new TextBlock { Text = "Decrypted:", Foreground = Brushes.Gainsboro }); sp.Children.Add(outp);
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
        var send = _w.Sends.FirstOrDefault(s => s.Txid == txid);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Transaction id: " + txid);
        if (_w.TxLabels.TryGetValue(txid, out var lbl)) sb.AppendLine("Label: " + lbl);
        var hrow = _w.History.FirstOrDefault(r => r.Memo.StartsWith(txid, StringComparison.OrdinalIgnoreCase));
        if (hrow != null) sb.AppendLine($"Status: {(hrow.Height > 0 ? "confirmed at height " + hrow.Height : "in mempool (unconfirmed)")}   Net to you: {hrow.Amount:N0} sat");
        if (send != null) sb.AppendLine($"Sent {send.Amount:N0} sat (fee {send.Fee:N0}) at {send.Time}  →  {send.To}");

        // RESOLVE the raw transaction from: our send record, a stored coin, or the SPV server (so EVERY tx — even
        // one we only saw on-chain — opens its full input/output breakdown, like ElectrumSVP).
        string rawHex = send?.RawHex ?? "";
        if (rawHex.Length == 0) rawHex = _w.Utxos.FirstOrDefault(u => u.Txid == txid && !string.IsNullOrEmpty(u.RawTxHex))?.RawTxHex ?? "";
        if (rawHex.Length == 0)
        {
            try
            {
                var t = System.Threading.Tasks.Task.Run(async () =>
                {
                    using var cli = new ElectrumSvpClient();
                    if (!await cli.ConnectAnyAsync(ElectrumSvpClient.ServersFor(_net().Network))) return "";
                    return Convert.ToHexString(await cli.GetTransactionAsync(txid)).ToLowerInvariant();
                });
                if (t.Wait(8000)) rawHex = t.Result;
            }
            catch { }
        }

        if (rawHex.Length > 0)
        {
            try
            {
                var tx = Chain.Deserialize(Convert.FromHexString(rawHex));
                bool Mine(byte[] s) { if (s.Length != 25 || s[0] != 0x76) return false; var a = Base58.CheckEncode(Prefix(_net().AddressVersion, s[3..23])); return AddressIsMine(a); }
                sb.AppendLine($"\nVersion {tx.Version}, locktime {tx.LockTime}, size {rawHex.Length / 2} bytes");
                sb.AppendLine($"\nInputs ({tx.Ins.Count}):");
                foreach (var i in tx.Ins) sb.AppendLine($"  {i.PrevTxid}:{i.Vout}");
                sb.AppendLine($"\nOutputs ({tx.Outs.Count}):");
                for (int o = 0; o < tx.Outs.Count; o++)
                {
                    var os = tx.Outs[o];
                    string who = os.Script.Length == 25 && os.Script[0] == 0x76 ? Base58.CheckEncode(Prefix(_net().AddressVersion, os.Script[3..23])) : $"script[{os.Script.Length}B]";
                    sb.AppendLine($"  :{o} = {os.Value:N0} sat → {who}{(Mine(os.Script) ? "   (yours)" : "")}");
                }
            }
            catch { sb.AppendLine("\n(could not parse the raw transaction)"); }
        }
        else
        {
            sb.AppendLine("\nOur outputs in this tx:");
            foreach (var u in _w.Utxos.Where(u => u.Txid == txid)) sb.AppendLine($"  :{u.Vout} = {u.Value:N0} sat → {AddressForKey(u.KeyChain, u.KeyIndex)} [{(u.Spent ? "spent" : u.Confirmed ? "confirmed" : "pending")}]");
            sb.AppendLine("\n(full input/output detail needs a network connection — reopen when online.)");
        }
        var box = new TextBox { Text = sb.ToString(), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), Background = FieldBg, Foreground = Ink, BorderThickness = new Thickness(0) };
        var win = new Window { Title = "Transaction details", Width = 600, Height = 460, Owner = Window.GetWindow(this), Background = WinBg, Content = new ScrollViewer { Content = box, Margin = new Thickness(10) } };
        win.ShowDialog();
    }

    /// <summary>True if the address belongs to this wallet (a receive/change key in range, or a watch address).</summary>
    private bool AddressIsMine(string addr)
    {
        try
        {
            if (_w.WatchAddresses.Contains(addr)) return true;
            if (_seed.Length != 32) return false;
            var h160 = Base58.CheckDecode(addr); if (h160.Length != 21) return false;
            var want = Convert.ToHexString(h160[1..]);
            uint gap = (uint)Math.Max(_w.RecvIndex + 50, 120);
            for (uint c = 0; c <= 1; c++) for (uint i = 0; i <= gap; i++)
                if (Convert.ToHexString(Hashes.Hash160(WalletKeys.Account(_seed, c, i).Pub)) == want) return true;
        }
        catch { }
        return false;
    }

    private void SetCoinLabel(string outpoint)
    {
        var box = new TextBox { Width = 420, Text = _w.CoinLabels.TryGetValue(outpoint, out var l) ? l : "" };
        var ok = new Button { Content = "Save", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        var win = new Window { Title = "Label for coin " + outpoint[..Math.Min(18, outpoint.Length)] + "…", Width = 470, Height = 150, Owner = Window.GetWindow(this), Background = WinBg, Content = new StackPanel { Margin = new Thickness(12), Children = { new TextBlock { Text = "Coin label:", Foreground = Ink }, box, ok } } };
        ok.Click += (_, _) => { var t = box.Text.Trim(); if (t.Length == 0) _w.CoinLabels.Remove(outpoint); else _w.CoinLabels[outpoint] = t; Save(); Render(); win.Close(); };
        win.ShowDialog();
    }

    private void FreezeAddress(bool frozen)
    {
        if (_addrGrid.SelectedItem == null) { _status.Text = "Select an address."; return; }
        var addr = PropOf(_addrGrid.SelectedItem, "Address");
        int n = 0;
        foreach (var u in _w.Utxos.Where(u => AddressForKey(u.KeyChain, u.KeyIndex) == addr)) { u.Frozen = frozen; n++; }
        Save(); Render();
        _status.Text = $"{(frozen ? "Froze" : "Unfroze")} {n} coin(s) on {addr}.";
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
        var win = new Window { Title = "Label for " + txid[..Math.Min(16, txid.Length)] + "…", Width = 470, Height = 150, Owner = Window.GetWindow(this), Content = new StackPanel { Margin = new Thickness(12), Children = { new TextBlock { Text = "Label:", Foreground = Brushes.Gainsboro }, box, ok } } };
        ok.Click += (_, _) => { var t = box.Text.Trim(); if (t.Length == 0) _w.TxLabels.Remove(txid); else _w.TxLabels[txid] = t; Save(); Render(); win.Close(); };
        win.ShowDialog();
    }

    // ============================ Sweep an external private key (WIF) ============================

    private void AddWatchAddress()
    {
        var box = new TextBox { Width = 380, FontFamily = new FontFamily("Consolas") }; ThemeOne(box);
        var ok = new Button { Content = "Watch", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        var win = new Window { Title = "Add a watch-only address", Width = 440, Height = 160, Owner = Window.GetWindow(this), Background = WinBg, Content = new StackPanel { Margin = new Thickness(12), Children = { new TextBlock { Text = "Address to watch (balance only, never spendable):", Foreground = Ink }, box, ok } } };
        ok.Click += (_, _) =>
        {
            try
            {
                var a = box.Text.Trim(); var p = Base58.CheckDecode(a);
                if (p.Length != 21) { MessageBox.Show("Invalid address."); return; }
                if (!_w.WatchAddresses.Contains(a)) _w.WatchAddresses.Add(a);
                Save(); RescanRequested?.Invoke(); Render();
                _status.Text = $"Watching {a} (rescanning).";
                win.Close();
            }
            catch (Exception ex) { MessageBox.Show("Bad address: " + ex.Message); }
        };
        win.ShowDialog();
    }

    private void SweepWif()
    {
        var box = new TextBox { Width = 420, FontFamily = new FontFamily("Consolas") };
        var ok = new Button { Content = "Sweep", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = "Private key (WIF) to sweep — its coins are sent to THIS wallet:", Foreground = Brushes.Gainsboro });
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
            sp.Children.Add(new TextBlock { Text = "Paste a BIP270 payment URL or invoice (Anypay, Centi, …):", Foreground = Brushes.Gainsboro });
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
            foreach (var u in _w.Utxos.Where(u => !u.Spent && !u.Frozen && !u.WatchOnly && IsRealCoin(u))) w.Add(new OnChainWallet.Utxo(u.Txid, u.Vout, u.Value, u.KeyChain, u.KeyIndex));
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

    /// <summary>Import a contact from a signed identity certificate: verify the signature against the embedded
    /// key, then save the contact WITH the verified real name + email + a ✓. This is a trusted, verified contact.</summary>
    private void ImportIdentityCert()
    {
        var box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), Height = 180, Width = 520 }; ThemeOne(box);
        var go = new Button { Content = "Verify & add", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        var res = new TextBlock { Margin = new Thickness(0, 8, 0, 0), FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap };
        var win = new Window { Title = "Import identity certificate", Width = 580, Height = 340, Owner = Window.GetWindow(this), Background = WinBg, Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(12), Children = { new TextBlock { Text = "Paste a person's signed identity certificate (JSON):", Foreground = Ink }, box, go, res } } } };
        go.Click += (_, _) =>
        {
            try
            {
                var r = JsonSerializer.Deserialize<Registration>(box.Text.Trim());
                if (r == null || r.IdentityPub.Length == 0) { res.Text = "Not a certificate."; res.Foreground = Brushes.IndianRed; return; }
                var pub = Convert.FromHexString(r.IdentityPub);
                if (!WalletExtras.VerifyMessage(pub, r.Canonical(), r.Signature)) { res.Text = "✖ INVALID signature — not added."; res.Foreground = Brushes.IndianRed; return; }
                _w.Contacts.RemoveAll(c => string.Equals(c.IdentityPub, r.IdentityPub, StringComparison.OrdinalIgnoreCase));
                _w.Contacts.Add(new Contact { Handle = r.Pseudonym, IdentityPub = r.IdentityPub.ToLowerInvariant(), DisplayName = r.DisplayName, Email = r.Email, Verified = true });
                Save(); Render();
                res.Text = $"✓ Added verified contact {r.DisplayName} (@{r.Pseudonym})."; res.Foreground = Accent;
            }
            catch (Exception ex) { res.Text = "Could not import: " + ex.Message; res.Foreground = Brushes.IndianRed; }
        };
        win.ShowDialog();
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
        sp.Children.Add(new TextBlock { Text = "Paste the claim receipt the payer gave you (identity-payment|payerIdPub|invoice|txid):", Foreground = Brushes.Gainsboro });
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
        sp.Children.Add(new TextBlock { Text = "Paste a raw transaction (hex) to broadcast over your BSV peers:", Foreground = Brushes.Gainsboro });
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
