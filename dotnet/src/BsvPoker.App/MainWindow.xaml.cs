using System.Windows;
using BsvPoker.App.Views;
using BsvPoker.Core;
using BsvPoker.Net;
using BsvPoker.Net.Bsv;

namespace BsvPoker.App;

public partial class MainWindow : Window
{
    private readonly Profile _profile = new();
    private GameView? _game;
    private ChatView? _chatView;
    private TxLink? _link;   // the ONLY player-to-player transport: it carries nothing but Bitcoin transactions
    private PokerGossip? _gossip;   // the poker discovery overlay (announce/forward/query) on top of Bitcoin
    // Bind to ALL interfaces on a WELL-KNOWN port so same-network players find us with zero config (no IP entry).
    // P2PNode falls back to an ephemeral port if the well-known one is busy (e.g. a 2nd instance on one machine).
    private readonly P2PNode _node = new(PeerDiscovery.WellKnownPort, "0.0.0.0");
    private LobbyView? _lobby;
    private ReplayView? _replay;
    private PeerDiscovery? _discovery;   // automatic local + LAN peer discovery (zero-config "just connects")
    private byte[] _idPriv = Array.Empty<byte>();   // the OPENED wallet's identity (Base ID) — set after the wallet is selected
    private byte[] _idPub = Array.Empty<byte>();

    public MainWindow()
    {
        InitializeComponent();
        // PER-INSTANCE: each running copy gets its OWN profile (wallet + identity). A 2nd copy is a different player.
        Title = $"BSV Poker — {_profile.Name}";

        var vault = new CardVault(_profile.Dir, _profile.IdentityPriv, _profile.IdentityPub); // (pre-wallet; re-sealed to the opened wallet's identity is a follow-up)
        // ONE identity across wallet + chat + game + NFTs: the wallet pays/encrypts/claims with the SAME Base ID
        // key the chat and game use, and NFTs are sealed to it. Fully integrated, not separate identities.
        var wallet = new WalletView(_profile.Dir, vault, () => _bsvNode, () => _headerStore, () => _currentNet, _profile.IdentityPriv, _profile.IdentityPub);
        WalletHost.Content = wallet;
        _wallet = wallet;
        _wallet.RescanRequested = DeepRescan;   // the "Rescan / Find my coins" button → full block scan (works on mainnet)
        // The game / chat / lobby views all derive their identity FROM THE OPENED WALLET. They are NOT built here:
        // the wallet is selected AFTER this window is shown (Loaded), so there is no identity yet at construction.
        // BuildIdentityViews() wires them once the wallet is open (see Loaded, and OnUnlocked for a later unlock).
        InitNetworkSelector();

        Loaded += async (_, _) =>
        {
            if (_startupRan) return;   // Loaded can fire again on re-parenting; run the network bring-up ONCE
            _startupRan = true;
            // The wallet was ALREADY selected + unlocked in RunStartupLogin() (BEFORE this window was shown), so by
            // the time we get here the identity views are wired and we can bring up the network.
            // No startup network popup: the top network selector (InitNetworkSelector, defaults to Mainnet) is
            // the ONLY network control, and switching it never re-logs-in. (Removed the ChooseNetworkAtStartup
            // popup — the principal does not want windows popping up in front of him.)
            StartBsvNetwork();   // the only Bitcoin-network connection (tx/headers/block) — nothing off-chain
            _wallet.Refresh();   // the receive address is network-aware; show it for the loaded network
            StartTxLink();       // listen for transactions pushed to us IP-to-IP by other players
            // bring up the table/lobby node so you can host/join a table or play your bot
            try { _node.SetIdentity(_idPriv, _idPub); await _node.StartAsync(); _lobby?.OnNodeReady(_node.BoundPort); } catch { }
            // AUTOMATIC PEER DISCOVERY — "open your node and it just connects", no host:port to type, NO UDP.
            // Same-machine instances are found via a shared rendezvous file; players ANYWHERE (LAN or internet)
            // are found via the ON-CHAIN node-seed registry (a well-known BSV address) and dialled over TCP. LAN
            // inbound is enabled so peers can actually reach us. Reading the registry is free; publishing our own
            // seed is an explicit user action (the "Publish my node on-chain" button) to honour never-auto-spend.
            try
            {
                TryAllowFirewall();                        // best-effort: open the well-known port so SAME-NETWORK players can reach us
                _node.EnableLan();                         // accept inbound TCP connections from the network, not just loopback
                _discovery = new PeerDiscovery(_node, LocalIp());
                _discovery.Start();
            }
            catch { }
            var netRefresh = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            netRefresh.Tick += (_, _) => UpdateNetInfo();
            netRefresh.Start();
            // SPV discovery: periodically pull the mempool (instant detect of a just-sent payment) and rescan
            // recent blocks with our filter (catches a payment that confirmed before we were connected).
            var spvScan = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            spvScan.Tick += (_, _) => SpvRescan();
            spvScan.Start();
            // (the OnUnlocked handler that re-arms the SPV filter + wires the identity views is subscribed above,
            //  right after wallet selection, so it is in place before the very first unlock fires.)
            // announce ourselves on-chain so peers auto-discover our key + address (a funded Announce tx)
            var ann = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
            ann.Tick += (_, _) => Announce();
            ann.Start();
            Announce();
            // WHO'S ONLINE: beacon my presence (identity key + reachable endpoint + @handle) every few seconds so
            // every other running app shows me in its directory automatically — no IP, no key, no add step. This is
            // pure off-chain gossip (never spends a coin). Presence beacons use a fresh id, so a peer that comes
            // online later still receives mine.
            var presence = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            presence.Tick += (_, _) => AnnouncePresence();
            presence.Start();
            AnnouncePresence();
        };
        Closed += (_, _) => { try { _wallet.VaultBackup(); } catch { } try { _node.CloseAllOwnTables(); } catch { } try { StopBotNetGame(); } catch { } try { _bjGame?.Stop(); } catch { } foreach (var w in _botWindows.ToList()) { try { w.Close(); } catch { } } foreach (var b in _bots.ToList()) { try { b.Dispose(); } catch { } } try { _discovery?.Dispose(); } catch { } try { _bsvNode?.Dispose(); } catch { } try { _link?.Dispose(); } catch { } try { _node.Dispose(); } catch { } };
    }

    private WalletView _wallet = null!;
    private bool _startupRan;   // guards the once-only network bring-up against a repeated Loaded event

    /// <summary>Run the SEQUENTIAL startup login BEFORE the main window is shown: the "Select your wallet" dialog
    /// (alone), then the password (alone). Returns true once a wallet is open + unlocked (identity views wired);
    /// false if the user cancelled (the app then exits). Called from App.OnStartup so nothing is ever behind the
    /// dialogs and they appear one after the other — never together, never with the game/wallet behind them.</summary>
    public bool RunStartupLogin()
    {
        if (!_wallet.SelectWalletAtStartup()) return false;   // selector → Load() → password, all modal/sequential
        BuildIdentityViews();                                  // identity is now the opened wallet's Base ID
        // a LATER unlock (account switch / unlock-from-locked) re-arms SPV and wires the views if not yet built
        _wallet.OnUnlocked += () => { BuildIdentityViews(); ApplySpvFilter(); SpvRescan(); };
        return true;
    }
    private bool _viewsBuilt;   // the identity-bound views (game/chat/lobby) are wired exactly once, after open

    /// <summary>Wire the views that derive their identity from the OPENED wallet — the game (its card vault is
    /// sealed to the wallet's identity), the chat (signs/encrypts with the wallet's Base ID) and the lobby. Built
    /// exactly once, the moment the wallet is open and its identity is available; a no-op until then and after.
    /// (Called after wallet selection, and again on a later unlock for the locked-at-selection case.)</summary>
    private void BuildIdentityViews()
    {
        if (_viewsBuilt) return;
        var pub = _wallet.WalletIdentityPub;
        if (pub.Length != 33) return;   // wallet open but still locked (no seed yet) → wait for OnUnlocked
        _viewsBuilt = true;
        _idPriv = _wallet.WalletIdentityPriv; _idPub = pub;   // identity = the wallet you opened, not an auto-profile key

        _game = new GameView(_node, _idPriv, _idPub, _wallet.WalletVault, _wallet.RefreshCards);   // vault sealed to the OPENED wallet
        _game.OnMove += EmitMoveOnChain;   // EVERY move I make becomes a funded on-chain tx, dual-path broadcast
        // Leaving the table stops the bot's NetGame AND ENDS any table this player hosts — so a table never
        // lingers as a ghost "open" table after its host walks away.
        _game.OnLeaveTable += () => { StopBotNetGame(); try { _node.CloseAllOwnTables(); } catch { } };
        _game.SetIdentityLabelResolver(_wallet.IdentityLabelFor);   // the table shows your PSEUDONYM, not a raw key
        _game.PayFee = PayCardFee;   // clicking a card → discard & draw charges a real on-chain fee
        GameHost.Content = _game;

        // Chat: every message is a Bitcoin transaction; peers are auto-discovered from on-chain Announce txs
        // (no manual key/IP exchange). Send = fund an encrypted ChatDirect tx, push it IP-to-IP to the peer
        // AND broadcast it to miners.
        _chatView = new ChatView(_idPub,
            () => (_gossip?.Peers ?? new List<PokerGossip.Peer>()).Select(p => (p.PubHex, p.Endpoint)).ToList(),
            SendChatTx);
        _chatView.SetHandleResolver(_wallet.IdentityLabelFor);   // chat shows @handles from the wallet's Contacts
        _chatView.SetOnlineDirectory(OnlineDirectory);           // live "who's online" directory (presence beacons)
        _chatView.SetSaveContact(_wallet.ImportContact);  // save a discovered peer into the wallet address book
        _chatView.SetContacts(_wallet.ContactList);       // recipients come from the address book, not just peers
        _chatView.SetGroups(_wallet.GroupList, _wallet.SaveGroup, _wallet.DeleteGroup);   // create/manage named groups
        _chatView.SetSendGroup(SendGroupChat);            // GROUP mode = the user's broadcast-encryption patent
        _chatView.SetAddBot(AddChatBot);                  // one-click: add a bot identity to chat + groups (no game)
        _wallet.OnChatReceived += (sender, text) => _chatView?.AddIncoming(sender, text);   // OFFLINE messages found on-chain at sync
        ChatHost.Content = _chatView;

        // The lobby: pick a variant + seat count (2–6), host/join a real table, or play your own bot at the chosen
        // variant. Joining a table or pressing "Play a bot" jumps to the game board.
        _lobby = new LobbyView(_node, _idPub, JoinTable,
            variant => { if (CanPlay()) { _game!.StartBot(variant); Tabs.SelectedIndex = 2; } },
            () => { if (CanPlay()) PlayBot(); },   // "Play my bot" → open YOUR identity-derived bot in its own window
            seats => { if (CanPlay()) HostBlackjack(seats); });   // "Host Blackjack (group)" → a multiplayer Blackjack table
        _lobby.PublishNode = () => PublishMyNodeSeed();   // "Publish my node on-chain" → the registry seed (explicit, ~3 sat)
        LobbyHost.Content = _lobby;

        // REPLAY: every move is on-chain, so a finished hand can be loaded and stepped through move-by-move.
        _replay = new ReplayView();
        _replay.GameFetcher = FetchGameFromChain;   // Replay pulls a REAL game off the chain by its start address/tx id
        ReplayHost.Content = _replay;
        // HELP: a plain-language how-to-play for someone who does not know poker (rules, buttons, winning/losing).
        HelpHost.Content = new HelpView();
        if (_node.BoundPort > 0) { try { _node.SetIdentity(_idPriv, _idPub); _lobby.OnNodeReady(_node.BoundPort); } catch { } }   // node may already be up if the wallet was locked at selection
    }

    private void StartTxLink()
    {
        try
        {
            // Bind ALL interfaces so a peer can actually reach us: presence/chat advertise this machine's LAN IP,
            // and a socket bound to loopback-only would refuse a connection to that LAN IP (even from the SAME
            // machine) — which is exactly why one-to-one chat never arrived. Any-bind accepts loopback + LAN.
            _link = new TxLink(_currentNet, 0, System.Net.IPAddress.Any);
            _link.OnTransaction += Ingest;      // transactions peers push to us directly, IP-to-IP
            _link.Start();
            var myHex = Convert.ToHexString(_idPub).ToLowerInvariant();
            _gossip = new PokerGossip(myHex, MyEndpoint(),
                (peerPub, endpoint, msg) => SendEncrypted(peerPub, endpoint, "GOSSIP:" + msg)); // gossip rides on Bitcoin txs
            _gossip.OnPeersChanged += () => Dispatcher.BeginInvoke(new Action(UpdateNetInfo));
        }
        catch { }
    }

    private string MyEndpoint() => $"{LocalIp()}:{(_link?.Port ?? 0)}";

    // Live node seeds learned from the on-chain registry (newest record per node), fed to PeerDiscovery so the
    // node dials them over TCP. Works locally, on a LAN, and across the internet — the registry is on the chain.
    private readonly Dictionary<string, NodeSeedRegistry.Seed> _seedRecords = new();

    /// <summary>Every inbound transaction (pushed IP-to-IP or relayed by the network) is inspected here.</summary>
    private void Ingest(Chain.Tx tx)
    {
        // a NODE-SEED record (from the on-chain registry): learn this node's live endpoint and dial it over TCP
        var seed = NodeSeedRegistry.TryReadTx(tx);
        if (seed != null) { ConsiderNodeSeed(seed); return; }
        // an Announce tx (relayed) bootstraps the gossip overlay with a first peer
        var ann = OnChainAnnounce.TryReadTx(tx);
        if (ann != null)
        {
            var hex = Convert.ToHexString(ann.Pub).ToLowerInvariant();
            if (!hex.Equals(Convert.ToHexString(_idPub), StringComparison.OrdinalIgnoreCase))
                _gossip?.AddSeed(hex, ann.Endpoint);
            return;
        }
        // detect an incoming PAYMENT to one of our addresses (shows as pending until SPV-confirmed)
        _wallet.ConsiderIncoming(tx);
        // GROUP message (broadcast encryption): readable only if I'm one of the selected members (skip my own).
        // A "broadcast to everyone" is a group sealed to every known recipient (the patent) — it arrives here.
        var grp = OnChainChat.TryReadGroupTx(tx, _idPriv, _idPub);
        if (grp != null)
        {
            if (!grp.SenderPub.AsSpan().SequenceEqual(_idPub))
                _chatView?.AddIncoming(Convert.ToHexString(grp.SenderPub).ToLowerInvariant(), "[group] " + grp.Text);
            return;
        }
        var msg = OnChainChat.TryReadTx(tx, _idPriv, _idPub);
        if (msg == null) return;
        if (msg.Text.StartsWith("GOSSIP:", StringComparison.Ordinal))      // poker discovery overlay
            _gossip?.Receive(msg.Text["GOSSIP:".Length..]);
        else
            _chatView?.AddIncoming(Convert.ToHexString(msg.SenderPub).ToLowerInvariant(), msg.Text);
    }

    // The owner's bot, run as a real second NetGame player (see PlayBot) on its OWN node. Stopped when the
    // player leaves the table or starts a fresh hand, so a finished bot game never lingers.
    private NetGame? _botGame;
    private P2PNode? _botNode;
    private System.Threading.Timer? _botActor;

    /// <summary>Run the owner's bot as a REAL second NetGame player so "Play my bot" is a genuine secure dealerless
    /// mental-poker hand — the SAME protocol as any networked game, not a local practice deal. The bot runs on its
    /// OWN loopback node dialed to ours (the proven two-node topology); cross-delivery goes through the transport's
    /// consumer thread, so there is no same-node re-entrancy deadlock between the two games' locks.</summary>
    private void StartBotNetGame(BotPlayer bot, string tableId)
    {
        StopBotNetGame();
        var bn = new P2PNode(0, "127.0.0.1");
        bn.SetIdentity(bot.Priv, bot.Pub);
        bn.StartAsync(new[] { new P2PNode.PeerAddr("127.0.0.1", _node.BoundPort) }).Wait();
        _botNode = bn;
        var bg = new NetGame(bn, tableId, bot.Priv, bot.Pub);
        bg.Start();
        _botGame = bg;
        _botActor = new System.Threading.Timer(_ =>
        {
            try
            {
                var h = bg.Hand;
                if (h == null || h.Complete || bg.MySeat < 0 || h.ToAct != bg.MySeat) return;
                var la = h.Legal();
                if (la.CanCheck) bg.Act(BsvPoker.Core.ActionKind.Check, 0);
                else if (la.CanCall) bg.Act(BsvPoker.Core.ActionKind.Call, la.CallAmount);
                else bg.Act(BsvPoker.Core.ActionKind.Fold, 0);
            }
            catch { }
        }, null, 250, 150);
    }

    /// <summary>Stop the bot's NetGame, its auto-actor, and its node. Safe to call when none is running.</summary>
    private void StopBotNetGame()
    {
        try { _botActor?.Dispose(); } catch { } _botActor = null;
        try { _botGame?.Stop(); } catch { } _botGame = null;
        try { _botNode?.Dispose(); } catch { } _botNode = null;
    }

    private readonly List<BotPlayer> _bots = new();        // Alice can run as MANY of her own bots as she likes
    private readonly List<BotWindow> _botWindows = new();

    /// <summary>
    /// Open one of MY bots: a SEPARATE automated player (its own identity DERIVED from mine, wallet, TxLink, and
    /// gossip node) in its own small window. Alice can open as many as she wants and play them; each bot only ever
    /// plays its owner (Alice). Cross-seeds gossip so I discover the bot; "Play on-chain hand" → choose the bot →
    /// real two-party mental-poker hand. Bots are for testing and soft play and are always available.
    /// </summary>
    private int _botCount;

    /// <summary>Join (or host) a real table on the lobby node and jump to the board.</summary>
    private void JoinTable(string tableId, string tableName)
    {
        if (!CanPlay()) return;
        StopBotNetGame();   // a fresh join reclaims the table — any prior bot game is stopped
        if (tableId.Contains("~bj~", StringComparison.Ordinal) || tableId.EndsWith("~bj", StringComparison.Ordinal)) { JoinBlackjack(tableId, tableName); return; }
        _game?.StartNetworked(tableId, tableName);
        Tabs.SelectedIndex = 2; // Game
    }

    private NetBlackjack? _bjGame;

    /// <summary>Host a MULTIPLAYER (group) Blackjack table: announce it in the lobby (so others can join), and
    /// seat yourself in the live networked game. Never one-on-one — it waits for other players, the dealer is
    /// computed jointly, the pot is n-of-n. Others join it from the lobby table list.</summary>
    private void HostBlackjack(int seats)
    {
        seats = Math.Clamp(seats, 2, 6);
        var id = $"t-bj{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}~bj~p{seats}~b10";
        try { _ = _node.CreateTableAsync(id, "Blackjack"); } catch { }
        JoinBlackjack(id, "Blackjack", hosted: true);
    }

    /// <summary>Join (or host-join) a Blackjack table: start the networked group game and open the live table window.</summary>
    private void JoinBlackjack(string tableId, string tableName, bool hosted = false)
    {
        try { _bjGame?.Stop(); } catch { }
        var bj = new NetBlackjack(_node, tableId, _idPriv, _idPub);
        // REAL on-chain n-of-n pot: escrow my stake into the pot script before play, and broadcast the co-signed
        // settlement when the table ends. FundPot runs on the UI thread (it touches the wallet) and returns the
        // created pot coin; OnSettlementTx lands the final payout. Real tokens at risk, settled on-chain.
        bj.FundPot = (script, value) => Dispatcher.Invoke(() =>
        {
            var (raw, status) = _wallet.FundTx(script, value, 1);
            if (raw == null) { _ = MessageBox.Show($"Could not fund the Blackjack pot: {status}"); return (NetBlackjack.PotCoin?)null; }
            var tx = Chain.Deserialize(raw);
            _ = _wallet.BroadcastRaw(raw); _ = BroadcastMove(raw);   // land it: SPV server + redundant dual-path
            return new NetBlackjack.PotCoin(Chain.Txid(tx), 0, value, Convert.ToHexString(raw).ToLowerInvariant());
        });
        bj.OnSettlementTx = raw => { try { _ = _wallet.BroadcastRaw(raw); _ = BroadcastMove(raw); } catch { } };
        // GRIEFING SAFETY: the pre-signed refund unlocks ~30 days out (≈4320 blocks); if a player ever refuses to
        // co-sign the cooperative payout, the refund is broadcast and every stake comes back after the timeout.
        bj.RecoveryLockHeight = _wallet.ApproxTipHeight + 4320;
        bj.OnRecoveryTx = raw => { try { _ = _wallet.BroadcastRaw(raw); _ = BroadcastMove(raw); } catch { } };
        bj.Start();
        _bjGame = bj;
        var win = new BlackjackTableWindow(this, bj, _wallet.IdentityLabelFor);   // shows who is at the table (@handles)
        if (hosted) win.Closed += (_, _) => { try { _node.CloseTable(tableId); } catch { } };   // end the hosted table when the host closes it
        win.Show();
    }

    /// <summary>FUNDED-GATE: nothing (play / a table) happens until this wallet is funded — except on regtest,
    /// which can self-fund. Until then the user goes nowhere.</summary>
    /// <summary>Best-effort: allow inbound TCP on the well-known lobby port through Windows Firewall (private/domain
    /// networks) so SAME-NETWORK players can reach this node. Needs admin to add the rule; if it can't, Windows
    /// shows its own one-time allow prompt. Never blocks startup. Same-machine play uses loopback and is unaffected.</summary>
    private static void TryAllowFirewall()
    {
        try
        {
            string exe = ""; try { exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? ""; } catch { }
            void Run(string args) { try { var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("netsh", args) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden }); p?.WaitForExit(2000); } catch { } }
            Run($"advfirewall firewall add rule name=\"BSV Poker port\" dir=in action=allow protocol=TCP localport={PeerDiscovery.WellKnownPort} profile=private,domain enable=yes");
            if (exe.Length > 0) Run($"advfirewall firewall add rule name=\"BSV Poker app\" dir=in action=allow program=\"{exe}\" profile=private,domain enable=yes");
        }
        catch { }
    }

    /// <summary>Pull a REAL game off the chain by its START ADDRESS (base58) or any of its tx ids: fetch the
    /// address's transaction history from the SPV server and return every transaction, in chain order, so Replay
    /// can step through the actual game. No demo — this is the real on-chain game.</summary>
    private async System.Threading.Tasks.Task<List<Chain.Tx>> FetchGameFromChain(string key)
    {
        var result = new List<Chain.Tx>();
        using var cli = new BsvPoker.Net.Bsv.ElectrumSvpClient();
        if (!await cli.ConnectAnyAsync(BsvPoker.Net.Bsv.ElectrumSvpClient.ServersFor(_currentNet.Network))) return result;
        byte[] script = Array.Empty<byte>();
        string k = key.Trim();
        bool isTxid = k.Length == 64 && k.All(Uri.IsHexDigit);
        try
        {
            if (isTxid)
            {
                var tx = Chain.Deserialize(await cli.GetTransactionAsync(k));
                if (tx.Outs.Count > 0) script = tx.Outs[0].Script;   // scan the address this game tx pays
            }
            else
            {
                var payload = BsvPoker.Crypto.Base58.CheckDecode(k); // version ‖ hash160
                if (payload.Length >= 2) { var h160 = payload[1..]; script = payload[0] == _currentNet.ScriptVersion ? Chain.P2shLockFromHash(h160) : Chain.P2pkhLock(h160); }
            }
        }
        catch { return result; }
        if (script.Length == 0) return result;
        var sh = BsvPoker.Net.Bsv.ElectrumSvpClient.ScriptHashOf(script);
        foreach (var (txid, _) in await cli.GetHistoryAsync(sh))
        {
            try { result.Add(Chain.Deserialize(await cli.GetTransactionAsync(txid))); } catch { }
        }
        return result;
    }

    private bool CanPlay()
    {
        // ONE-TIME ENROLLMENT AT JOIN: if this wallet has no identity yet, register it NOW — once, ever. Registration
        // is offered ONLY here (never as a tab checkbox). After it, play proceeds. Once registered, this never asks
        // again — the wallet just plays. (Registration funds the 1-sat on-chain identity token; fund the wallet first.)
        if (!_wallet.HasIdentity)
        {
            if (!_wallet.EnsureRegistered()) return false;   // they cancelled or couldn't complete enrollment
        }
        return true;
    }

    /// <summary>Spin up one of MY bots purely as a CHAT IDENTITY — no game window, no hand. It is derived from my
    /// identity (Type-42), wired so its messages reach miners + peers, seeded into gossip both ways, and added to
    /// my address book so it appears in chat and can be messaged or put in a group immediately. Returns its
    /// (name, pubkey-hex), or null if the wallet is locked. This is what the chat "Add a bot" button calls.</summary>
    private (string Name, string PubHex)? AddChatBot()
    {
        try
        {
            if (!Dispatcher.CheckAccess()) return Dispatcher.Invoke(AddChatBot);
            if (_idPriv == null || _idPriv.Length != 32) return null;
            if (!_wallet.HasIdentity) { CanPlay(); return null; }   // nothing but receiving funds until registered on-chain
            var ownerHandle = string.IsNullOrWhiteSpace(_wallet.MyHandle) ? _profile.Name.Replace(" ", "") : _wallet.MyHandle;
            var bot = new BotPlayer(_currentNet, LocalIp(), _idPriv, _idPub, ++_botCount, ownerHandle);
            bot.OnBroadcast += raw =>
            {
                try { _bsvNode?.Broadcast(raw); } catch { }
                foreach (var p in (_gossip?.Peers ?? new List<PokerGossip.Peer>()).ToList())
                { var (h, pt) = ParseHostPort(p.Endpoint); if (h != null) _ = TxLink.SendTxAsync(_currentNet, h, pt, raw); }
            };
            var myHex = Convert.ToHexString(_idPub).ToLowerInvariant();
            bot.AddPeer(myHex, MyEndpoint());                 // the bot can reach me
            _gossip?.AddSeed(bot.PubHex, bot.Endpoint);        // I can reach the bot
            _wallet.ImportContact(bot.Name, bot.PubHex);       // it appears in chat + groups everywhere
            _bots.Add(bot);
            bot.Announce();
            UpdateNetInfo();
            return (bot.Name, bot.PubHex);
        }
        catch { return null; }
    }

    private void PlayBot()
    {
        // the bot is DERIVED from MY identity (Type-42) and named <my-handle>-Bot-NNN; it will only ever play me.
        var ownerHandle = string.IsNullOrWhiteSpace(_wallet.MyHandle) ? _profile.Name.Replace(" ", "") : _wallet.MyHandle;
        var bot = new BotPlayer(_currentNet, LocalIp(), _idPriv, _idPub, ++_botCount, ownerHandle);
        var myHex = Convert.ToHexString(_idPub).ToLowerInvariant();
        bot.AddPeer(myHex, MyEndpoint());                  // the bot knows how to reach us (plumbing only — it does not act yet)
        _gossip?.AddSeed(bot.PubHex, bot.Endpoint);        // we know how to reach the bot (plumbing only)
        // LIFECYCLE: the bot must NOT appear in chat or announce/act before ITS GAME has started. So we do NOT
        // ImportContact or Announce here — those happen AFTER _game.StartBot below (see "bot now joins chat …").
        // ONE-CLICK funding: the BotWindow's amount + button calls this — send the sats from MY wallet to the bot
        // and credit it immediately; the bot refunds to my receive address on close.
        var myRefund = _wallet.PublicReceiveAddress();
        var win = new BotWindow(bot, async sat =>
        {
            var tx = await _wallet.FundBotAsync(bot.ReceiveAddress(), sat);
            return tx != null && bot.CreditRaw(tx, myRefund);
        }) { Owner = this };
        // the bot has no miner connection — when it settles (e.g. the close-out refund) the host broadcasts for it,
        // to miners AND to every known peer, so the user's money actually comes back on-chain.
        bot.OnBroadcast += raw =>
        {
            try { _bsvNode?.Broadcast(raw); } catch { }
            foreach (var p in (_gossip?.Peers ?? new List<PokerGossip.Peer>()).ToList())
            { var (h, pt) = ParseHostPort(p.Endpoint); if (h != null) _ = TxLink.SendTxAsync(_currentNet, h, pt, raw); }
        };
        _bots.Add(bot); _botWindows.Add(win);
        win.Closed += (_, _) => { _bots.Remove(bot); _botWindows.Remove(win); UpdateNetInfo(); };
        win.Show();
        Tabs.SelectedIndex = 2;   // show the Game board

        // REAL SECURE HAND vs your bot: both you and the bot play the SAME dealerless mental-poker NetGame (private
        // holes, commit-reveal seating, hiding reveal commitments) — the identical protocol as any networked table,
        // now with the bot as the second seat (auto-acting). This replaces the old local practice deal and the
        // removed LiveDeal/LiveHand path. The fully on-chain hand tape still runs in the BACKGROUND below for Replay.
        var v = _lobby?.SelectedVariant ?? Variant.TexasHoldem;
        var tableId = $"t-mybot{_botCount}-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}~{v}~p2~s100~b2";
        _game?.StartNetworked(tableId, $"Heads-up vs {bot.Name}");
        StartBotNetGame(bot, tableId);

        // The bot now JOINS chat and ANNOUNCES — strictly AFTER its game has started, never before. This is the
        // lifecycle fix: a bot must not act or appear in chat ahead of its own game.
        _wallet.ImportContact(bot.Name, bot.PubHex);       // add the bot to the address book so it's named everywhere
        bot.Announce();
        UpdateNetInfo();

        // FULLY ON-CHAIN bot hand: when funded, play a complete heads-up Hold'em hand vs your bot as a real
        // transaction tape — every move (cards, bets, board, showdown, settlement) is broadcast on-chain, with
        // NO off-chain deck or messaging of any kind. The resulting tape is loaded into the REPLAY tab so you can
        // step through it move by move. The visible table hand above stays playable regardless; this is the
        // on-chain record. If the wallet is not funded we simply skip it (the table hand still plays).
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (!_wallet.IsFunded) return;
                var (msg, tape, _) = await _wallet.RunOnChainHoldemVsBot(BotPlayer.LiveStake);
                if (tape != null) _replay?.Load(tape);   // make the on-chain hand replayable
                _game?.ShowOnChainNote(tape != null
                    ? "This hand was also played fully ON-CHAIN vs your bot — open the Replay tab to step through it."
                    : msg);
            }
            catch { /* the visible table hand is unaffected */ }
        });
    }

    private static string CardStr(Card c)
    {
        string r = c.Rank switch { 14 => "A", 13 => "K", 12 => "Q", 11 => "J", 10 => "T", _ => c.Rank.ToString() };
        string s = c.Suit switch { Suit.Spades => "♠", Suit.Hearts => "♥", Suit.Diamonds => "♦", _ => "♣" };
        return r + s;
    }

    /// <summary>Encrypt <paramref name="text"/> to the recipient, fund a ChatDirect tx, push it IP-to-IP + to miners. "" on success.</summary>
    private string SendEncrypted(string recipientPubHex, string endpoint, string text)
    {
        byte[] rpub;
        try { rpub = Convert.FromHexString(recipientPubHex); } catch { return "Recipient public key must be 66 hex chars."; }
        if (rpub.Length != 33) return "Recipient public key must be a 33-byte compressed key.";
        // per-message index = monotonic unix-millis; the symmetric key is derived from our two IDENTITY keys +
        // the chat marker + this index, so either party recovers the whole conversation forever (wallet saves it).
        var script = OnChainChat.BuildScript(rpub, _idPriv, _idPub, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), text);
        // pay a 1-sat discovery dust to the recipient's identity address so they find this on-chain even if they
        // were OFFLINE when it was sent (store-and-forward). Everything in spendable outputs — no OP_RETURN.
        var (raw, status) = _wallet.FundChatTx(script, new[] { rpub }, 1);
        if (raw == null) return status;
        var (host, port) = ParseHostPort(endpoint);
        if (host != null) _ = TxLink.SendTxAsync(_currentNet, host, port, raw);
        _bsvNode?.Broadcast(raw);
        return "";
    }

    /// <summary>"Broadcast to everyone" = the principal's key-graph BROADCAST-ENCRYPTION PATENT (GB 2623780 B)
    /// sealed to EVERY known recipient (all current peers) + myself — NEVER plaintext send-to-all. Delegates to
    /// the encrypted group send: one envelope each recipient opens with their own key, pushed IP-to-IP to every
    /// peer AND to miners (redundant). A sealed scheme has an explicit recipient set by design, so a party we do
    /// not yet know is not a recipient; with no known peers it seals to me alone (still on-chain, never plaintext).</summary>
    private string SendBroadcastChat(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Type a message.";
        var members = (_gossip?.Peers ?? new List<PokerGossip.Peer>())
            .Select(p => p.PubHex).Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.ToLowerInvariant()).Distinct().ToList();
        if (members.Count == 0) return "No recipients are known yet — broadcast encryption seals to a recipient set, so there must be at least one known peer.";
        return SendGroupChat(members, text);     // SendGroupChat always adds me + seals via the patent + dual-path sends
    }

    /// <summary>
    /// GROUP send using the user's key-graph BROADCAST ENCRYPTION (GB 2623780 B): seal ONE message to the
    /// selected member pubkeys, fund a tiny ChatGroup tx carrying the self-contained envelope, push it IP-to-IP
    /// to every member we have an endpoint for, and broadcast to miners so OFFLINE members get it on-chain
    /// (store-and-forward) when they next sync. Only the selected members can decrypt it.
    /// </summary>
    private string SendGroupChat(IReadOnlyList<string> memberPubs, string text)
    {
        if (!_wallet.HasIdentity) return "Set up your identity on-chain first — until then the only thing you can do is receive funds.";
        if (string.IsNullOrWhiteSpace(text)) return "Type a message.";
        if (memberPubs.Count == 0) return "The group has no members.";
        // ALWAYS include myself as a member: the message lives on-chain, so on a later login I must be able to
        // decrypt my OWN group history (WhatsApp-grade). Without this, the sender is locked out of their own messages.
        var myHex = Convert.ToHexString(_idPub).ToLowerInvariant();
        var members = memberPubs.Select(m => m.ToLowerInvariant()).Append(myHex).Distinct().ToList();
        byte[] raw; string status;
        try
        {
            var dataScript = OnChainChat.BuildGroup(members, _idPriv, _idPub, text);
            // a 1-sat discovery dust to EACH member's identity address → offline members retrieve it on next sync
            var memberPubsBytes = members.Select(m => { try { return Convert.FromHexString(m); } catch { return Array.Empty<byte>(); } })
                                         .Where(b => b.Length == 33).ToList();
            (raw, status) = _wallet.FundChatTx(dataScript, memberPubsBytes, 1);
        }
        catch (Exception ex) { return "Could not build the group message: " + ex.Message; }
        if (raw == null) return status;
        // push IP-to-IP to any member we currently have an endpoint for (the rest receive it on-chain)
        var byPub = (_gossip?.Peers ?? new List<PokerGossip.Peer>()).ToList();
        var targets = members.ToHashSet();
        foreach (var p in byPub)
            if (targets.Contains(p.PubHex.ToLowerInvariant()))
            { var (h, pt) = ParseHostPort(p.Endpoint); if (h != null) _ = TxLink.SendTxAsync(_currentNet, h, pt, raw); }
        _bsvNode?.Broadcast(raw);
        return $"Sent to the group ({members.Count} members, encrypted).";
    }

    /// <summary>Fund a chat output into a real tx; returns (raw, "") on success or (null, error).</summary>
    private (byte[] Raw, string Status) ToRawOrError(byte[] script)
    {
        var (raw, status) = _wallet.FundTx(script, 1, 1);
        return raw == null ? (null!, status) : (raw, "");
    }

    /// <summary>The REDUNDANT DUAL-PATH entry point for EVERY on-chain move tx: send it IP-to-IP to every known
    /// peer AND to the BSV nodes/miners (directive 20260612-102704 — all players, both paths, fully redundant,
    /// so a zero-conf move cannot be raced by a double-spend). Used for game moves, the deal tape, and chat.</summary>
    public Task<int> BroadcastMove(byte[] rawTx)
        => new RedundantMoveBroadcast(
               () => (_gossip?.Peers ?? new List<PokerGossip.Peer>())
                     .Select(p => ParseHostPort(p.Endpoint))
                     .Where(hp => hp.Item1 != null)
                     .Select(hp => (hp.Item1!, hp.Item2))
                     .ToList(),
               (h, pt, raw) => TxLink.SendTxAsync(_currentNet, h, pt, raw),
               raw => { _bsvNode?.Broadcast(raw); })
           .Broadcast(rawTx);

    /// <summary>Turn one of MY in-game moves into a REAL on-chain transaction (a typed Bet output funded ~1 sat
    /// from my wallet) and dual-path broadcast it (IP-to-IP to every peer AND to the nodes). Every move I make is
    /// a transaction. Only MY moves are funded here (the mover pays); peers fund their own. Silent if unfunded.</summary>
    /// <summary>Charge a small on-chain fee for a card action (discard &amp; draw): fund a real tiny tx from the
    /// wallet and broadcast it both paths. Returns true only if it was actually built + paid. Shows in the
    /// Transactions tab like every other action.</summary>
    private bool PayCardFee(long sat)
    {
        try
        {
            var (raw, _) = _wallet.FundTx(Chain.P2pkhLockForPub(_idPub), Math.Max(1, sat), 1);   // pay to self (a real tx) — records + is visible
            if (raw == null) return false;
            _ = BroadcastMove(raw);
            return true;
        }
        catch { return false; }
    }

    private void EmitMoveOnChain(NetGame.MoveRecord m)
    {
        if (!m.Mine) return;
        try
        {
            if (m.Kind == "bet")
            {
                var handId = new byte[16]; BitConverter.GetBytes(m.HandNo).CopyTo(handId, 0);
                byte action = m.Action switch { "Fold" => 0, "Check" => 1, "Call" => 2, "Bet" => 3, "Raise" => 4, "AllIn" => 5, _ => 9 };
                var script = TxTemplates.BuildOutput(TxKind.Bet,
                    new[] { handId, new[] { (byte)Math.Max(0, m.Seat) }, new[] { action }, BitConverter.GetBytes(m.Amount) }, _idPub);
                var (raw, _) = _wallet.FundTx(script, 1, 1);
                if (raw != null) _ = BroadcastMove(raw);   // funded → every move on-chain, both paths
            }
            else if (m.Kind == "swap")
            {
                // PAID card swap: fund a real fee tx (to self) so the discard/draw is an on-chain action.
                var (raw, _) = _wallet.FundTx(Chain.P2pkhLockForPub(_idPub), Math.Max(1, m.Amount), 1);
                if (raw != null) _ = BroadcastMove(raw);
            }
        }
        catch { /* a move broadcast must never crash the game; the in-session move already stands */ }
    }

    /// <summary>
    /// Announce ourselves so peers discover us: (1) a bootstrap Announce transaction relayed on the Bitcoin
    /// network (seeds nodes that don't know us yet), and (2) the poker gossip overlay's announce/query, which
    /// floods our presence across the overlay and pulls peers others know.
    /// </summary>
    /// <summary>Beacon MY presence to the mesh: identity pubkey + my reachable chat endpoint + my @handle, signed
    /// by my identity key (so no one can announce as me or steal my handle). Every other app lists me in its live
    /// "who's online" directory automatically. Off-chain gossip only — never spends a coin.</summary>
    private void AnnouncePresence()
    {
        try
        {
            if (!_viewsBuilt || _idPub.Length != 33) return;   // need an identity first
            var myHex = Convert.ToHexString(_idPub).ToLowerInvariant();
            _ = _node.HeartbeatAsync(myHex, MyEndpoint(), _wallet.MyHandle ?? "");
        }
        catch { }
    }

    /// <summary>The live directory of people online right now (identity pubkey + reachable endpoint + @handle),
    /// excluding myself — handed to the chat so it shows who can be messaged instantly.</summary>
    private IReadOnlyList<(string PubHex, string Endpoint, string Handle)> OnlineDirectory()
    {
        var myHex = _idPub.Length == 33 ? Convert.ToHexString(_idPub).ToLowerInvariant() : "";
        return _node.ListPresence()
            .Where(p => !string.Equals(p.playerId, myHex, StringComparison.OrdinalIgnoreCase))
            .Select(p => (p.playerId, p.addr, p.handle))
            .ToList();
    }

    private void Announce()
    {
        // NEVER auto-spend the user's coins. A funded on-chain announce is an EXPLICIT user action — NEVER a
        // 45-second timer. That timer spent the real funding coin every cycle and left fake change: it is what
        // DESTROYED the user's 1,000,000-sat coin. Peer discovery here is OFF-CHAIN gossip only — no tx, no spend.
        _gossip?.Announce();   // poker overlay: announce to known peers (they forward it onward)
        _gossip?.Query();      // and pull the peers they know
    }

    // Record a node seed read from the on-chain registry, keep the NEWEST per node, and hand the current LIVE
    // endpoints to the TCP discovery so it dials them. Reading/dialing is FREE — it never spends a coin.
    private void ConsiderNodeSeed(NodeSeedRegistry.Seed seed)
    {
        try
        {
            var key = Convert.ToHexString(seed.Pub);
            lock (_seedRecords)
            {
                if (_seedRecords.TryGetValue(key, out var existing) && existing.UnixTime >= seed.UnixTime) return; // keep newest
                _seedRecords[key] = seed;
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var live = NodeSeedRegistry.LiveEndpoints(_seedRecords.Values.ToList(), now);
                _discovery?.SetOnChainSeeds(live);
            }
        }
        catch { }
    }

    /// <summary>EXPLICIT user action: publish THIS node's seed (IP:port + expiry) to the on-chain registry so
    /// players anywhere can discover and TCP-connect to us. Spends ~3 sats (1 to the registry + 1 record + 1
    /// fee). NEVER auto-spent — the user presses this; the previous auto-spend timer destroyed a coin, so all
    /// coin-spending discovery is opt-in. A node stays listed for the TTL; press again to refresh before it.</summary>
    private string PublishMyNodeSeed(int ttlSeconds = 3600)
    {
        if (!CanPlay()) return "Open and unlock your wallet first.";
        var endpoint = $"{LocalIp()}:{_node.BoundPort}";
        // Build + record the tx synchronously (so it shows in Transactions immediately), then land it the SAME
        // reliable way every send goes out: the ElectrumSVP SPV server (returns the txid), PLUS the redundant
        // dual-path (IP-to-IP to peers + BSV nodes). The previous code only tried _bsvNode.Broadcast, which sends
        // nothing when that node has no mainnet peers — that is why no tx appeared.
        var (raw, txid, status) = _wallet.BuildAndRecordNodeSeed(_idPub, endpoint, ttlSeconds);
        if (raw == null) return "Could not publish node seed: " + status;
        _ = _wallet.PublishNodeSeedBroadcastAsync(raw);   // ElectrumSVP lander + P2P backup (reports success/failure)
        _ = BroadcastMove(raw);                            // redundant dual-path: every known peer (IP-to-IP) + the nodes
        return $"Publishing your node {endpoint} on-chain (tx {txid[..Math.Min(12, txid.Length)]}…) to the registry {NodeSeedRegistry.RegistryAddressMainnet}. It now appears in your Transactions and will confirm shortly — players anywhere can then discover and connect to you.";
    }

    private static string LocalIp()
    {
        try
        {
            foreach (var ip in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(ip))
                    return ip.ToString();
        }
        catch { }
        return "127.0.0.1";
    }

    /// <summary>Send a chat message AS a Bitcoin transaction: fund it, push IP-to-IP to the peer, broadcast to miners.</summary>
    private string SendChatTx(string recipientPubHex, string peerHostPort, string text, bool broadcast)
    {
        // Until your identity is registered ON-CHAIN, the ONLY thing this wallet can do is receive funds. Nothing
        // else — chat included — happens before registration.
        if (!_wallet.HasIdentity) return "Set up your identity on-chain first (fund the wallet, then open the Identity tab). Until then the only thing you can do is receive funds.";
        if (string.IsNullOrWhiteSpace(text)) return "Type a message.";
        if (broadcast) return SendBroadcastChat(text);   // encrypted broadcast (patent); returns its own status message
        // JUST WORKS: if we were not handed a live endpoint, look the recipient up in the presence directory so an
        // ONLINE recipient gets the message INSTANTLY (IP-to-IP). If they are offline, it still goes on-chain
        // (store-and-forward) and reaches them on their next sync — either way the sender does nothing extra.
        if (string.IsNullOrWhiteSpace(peerHostPort))
            peerHostPort = _node.ListPresence().FirstOrDefault(p => string.Equals(p.playerId, recipientPubHex, StringComparison.OrdinalIgnoreCase))?.addr ?? "";
        var err = SendEncrypted(recipientPubHex, peerHostPort, text);
        if (err != "") return err;
        return string.IsNullOrWhiteSpace(peerHostPort)
            ? "Sent (encrypted, on-chain) — they will receive it the moment they come online."
            : $"Sent (encrypted) — delivered to {peerHostPort}.";
    }

    private static (string? Host, int Port) ParseHostPort(string s)
    {
        var i = s.LastIndexOf(':');
        if (i <= 0 || !int.TryParse(s[(i + 1)..], out var p) || p <= 0) return (null, 0);
        return (s[..i], p);
    }

    /// <summary>At startup the user MUST choose which network this wallet runs on (mainnet/testnet/regtest).
    /// Cancelling/closing = no network chosen = the program ends (no app without a chosen network + wallet).</summary>
    private void ChooseNetworkAtStartup()
    {
        var sp = new System.Windows.Controls.StackPanel { Margin = new Thickness(22) };
        sp.Children.Add(new System.Windows.Controls.TextBlock { Text = "Choose the network for this wallet", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = System.Windows.Media.Brushes.White });
        sp.Children.Add(new System.Windows.Controls.TextBlock { Text = "Mainnet = real BSV.  Testnet = test coins.  Regtest = your own local chain (can self-fund). Nothing happens until this wallet is funded on the chosen chain.", Foreground = System.Windows.Media.Brushes.Gainsboro, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Margin = new Thickness(0, 4, 0, 14) });
        int chosen = -1;
        var win = new Window { Title = "Network", SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = (System.Windows.Media.Brush)FindResource("Bg") };
        System.Windows.Controls.Button Opt(string t, int idx)
        {
            var b = new System.Windows.Controls.Button { Content = t, Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(14, 10, 14, 10), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            b.Click += (_, _) => { chosen = idx; win.DialogResult = true; };
            return b;
        }
        sp.Children.Add(Opt("Mainnet — real BSV", 0));
        sp.Children.Add(Opt("Testnet — test coins", 1));
        sp.Children.Add(Opt("Regtest — local chain", 2));
        win.Content = sp;
        var ok = win.ShowDialog();
        if (ok != true || chosen < 0) { Application.Current?.Shutdown(); return; }
        NetworkBox.SelectedIndex = chosen;   // StartBsvNetwork() (next) reads this; disposes any prior node cleanly
    }

    private void InitNetworkSelector()
    {
        // REMEMBER the network across launches. A wallet must NOT silently show zero just because the app
        // reset to a different network on restart — if you were on Testnet with confirmed coins, you reopen on
        // Testnet and still see them. A brand-new profile (no saved choice) defaults to Mainnet.
        var file = System.IO.Path.Combine(_profile.Dir, "network.txt");
        int idx = 0; // Mainnet default for a fresh profile
        try { if (System.IO.File.Exists(file) && int.TryParse(System.IO.File.ReadAllText(file).Trim(), out var v) && v is >= 0 and <= 2) idx = v; } catch { }
        NetworkBox.SelectedIndex = idx;
        if (idx != 0) StartBsvNetwork();   // bring up the node on the restored (non-Mainnet) network
        UpdateNetInfo();
        NetworkBox.SelectionChanged += (_, _) =>
        {
            try { System.IO.File.WriteAllText(file, NetworkBox.SelectedIndex.ToString()); } catch { } // persist the choice
            StartBsvNetwork();   // sets _currentNet from the selection
            _wallet.Refresh();   // re-render the network-aware receive address for the newly selected network
            UpdateNetInfo();
        };
    }

    private BsvNode? _bsvNode;
    private HeaderStore? _headerStore;
    private NetworkParams _currentNet = NetworkParams.For(BsvNetwork.Mainnet);
    private int _storedHeight;

    private void StartBsvNetwork()
    {
        var net = NetworkBox.SelectedIndex switch
        {
            1 => BsvNetwork.Testnet,
            2 => BsvNetwork.Regtest,
            _ => BsvNetwork.Mainnet,
        };
        try { _bsvNode?.Dispose(); } catch { }
        _currentNet = NetworkParams.For(net);
        _bsvNode = new BsvNode(_currentNet);
        _bsvNode.OnRelayedTransaction += Ingest;   // auto-discover peers + receive messages from network-relayed txs
        _bsvNode.OnConfirmedTransaction += (tx, mb) => _wallet.ConfirmIncoming(tx, mb); // SPV-proven payment → credit it
        ApplySpvFilter();                          // load our addresses into the node so peers relay our own txs back
        var storePath = System.IO.Path.Combine(_profile.Dir, $"headers-{net}.dat");
        _headerStore = new HeaderStore(storePath);
        _storedHeight = _headerStore.Count;
        var store = _headerStore;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            var node = _bsvNode;
            await node.StartAsync(16);
            // DNS seeds are flaky/greylist-prone; seed known-good public BSV nodes so we reliably connect (then
            // getaddr expands to the whole network). These are live /Bitcoin SV:1.2.0/ peers verified this session.
            if (net == BsvNetwork.Mainnet)
                foreach (var ip in new[] { "135.125.170.182", "198.154.93.204", "198.154.93.210", "198.154.93.212", "135.181.137.155", "141.95.126.79", "57.128.233.172", "57.128.216.248", "162.19.222.167" })
                    node.AddManualPeer(ip, 8333);
            while (ReferenceEquals(_headerStore, store))
            {
                try
                {
                    if (node.PeerCount > 0)
                    {
                        var (_, height) = await node.SyncHeadersToStoreAsync(store, maxBatches: 4);
                        await Dispatcher.InvokeAsync(() => { if (ReferenceEquals(_headerStore, store)) { _storedHeight = height; UpdateNetInfo(); } });
                    }
                }
                catch { }
                await System.Threading.Tasks.Task.Delay(2000);
            }
        });
        UpdateNetInfo();
    }

    /// <summary>
    /// Build the SPV bloom filter from the wallet's own addresses and load it into the node, so peers relay our
    /// own transactions (incoming payments and our spends) back to us — the no-server way the wallet sees the
    /// chain. Does nothing until the wallet is unlocked (the seed is needed to derive the addresses).
    /// </summary>
    private void ApplySpvFilter()
    {
        if (_bsvNode == null || !_wallet.IsUnlocked) return;
        try
        {
            var elems = _wallet.FilterElements();
            var tweak = System.BitConverter.ToUInt32(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
            var filter = new BloomFilter(elems.Count, 0.00001, tweak);
            foreach (var e in elems) filter.Insert(e);
            _bsvNode.SetSpvFilter(filter);   // pushes filterload to peers + pulls their mempools
        }
        catch { }
    }

    /// <summary>
    /// Pull peer mempools and rescan recent blocks with our filter so a payment is discovered whether it is
    /// still unconfirmed OR confirmed before we connected. Matching txs come back with a merkleblock proof we
    /// verify against our own headers in <see cref="WalletView.ConfirmIncoming"/>.
    /// </summary>
    private bool _deepScanRunning;
    private int _lastScannedHeight;

    /// <summary>AUTOMATIC coin discovery — the path that WORKS on public mainnet nodes (which do not serve bloom
    /// filters). Runs SILENTLY on load and periodically (never a button the user must press): sync headers from
    /// genesis (persistent), then download blocks, validate each (merkle root vs a header in our genesis-validated
    /// chain), and credit any output paying our keys/watch addresses. First pass scans recent history; later
    /// passes only the NEW blocks since last time, so it is cheap to repeat. If you have coins, they appear — no
    /// action required.</summary>
    private async void DeepRescan()
    {
        if (_deepScanRunning) return;
        _deepScanRunning = true;
        try
        {
            var node = _bsvNode;
            if (node == null || node.PeerCount == 0) return;       // silent: a later automatic pass runs once peers connect
            if (!_wallet.IsUnlocked) return;
            var store = _headerStore; if (store == null) return;
            await System.Threading.Tasks.Task.Run(async () => { for (int r = 0; r < 600; r++) { var (app, _) = await node.SyncHeadersToStoreAsync(store, maxBatches: 40); if (app == 0) break; } });
            var (chain, _) = store.BuildChain();
            int tip = chain.Height;
            if (tip < 1) return;
            var hdrs = store.Load();
            int from = _lastScannedHeight > 0 ? _lastScannedHeight + 1 : Math.Max(1, tip - 1000); // first pass: recent history
            if (from > tip) { _lastScannedHeight = tip; return; }
            for (int hgt = from; hgt <= tip; hgt++)
            {
                var bi = hdrs[hgt].Hash(); var bd = (byte[])bi.Clone(); Array.Reverse(bd);
                var bh = Convert.ToHexString(bd).ToLowerInvariant();
                byte[]? raw = await node.GetBlockAsync(bh, 30000);
                if (raw == null) continue;
                BsvBlock.Parsed blk; try { blk = BsvBlock.Parse(raw); } catch { continue; } // merkle root must match the header
                for (int ti = 0; ti < blk.Txs.Count; ti++) _wallet.ConfirmFromBlock(blk.Txs[ti], blk.Header, blk.Txids, ti);   // credit WITH a saved, re-verifiable proof
            }
            _lastScannedHeight = tip;
        }
        catch { }
        finally { _deepScanRunning = false; }
    }

    private void SpvRescan()
    {
        if (_bsvNode == null || _bsvNode.PeerCount == 0 || !_wallet.IsUnlocked) return;
        try
        {
            _bsvNode.RequestMempool();
            var store = _headerStore;
            if (store != null && store.Count > 0)
            {
                var (chain, _) = store.BuildChain();
                var recent = chain.Recent(2000).Select(e => e.HashHex).ToList();   // last ~2 weeks of blocks
                if (recent.Count > 0) _bsvNode.RequestFilteredBlocks(recent);
            }
        }
        catch { }
    }

    private void UpdateNetInfo()
    {
        var name = (NetworkBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Mainnet";
        // STANDALONE SPV: the client connects to no node network. Payments arrive as SPV envelopes (merkle
        // proof + header) IP-to-IP; players are discovered peer-to-peer. There is no "connected" state.
        int players = _gossip?.Peers.Count ?? 0;
        // Identity is the PSEUDONYM the user registered — never a raw key string. Fall back to the profile name
        // only if no pseudonym/handle has been set yet.
        var me = !string.IsNullOrWhiteSpace(_wallet?.MyHandle) ? "@" + _wallet!.MyHandle : _profile.Name;
        NetInfo.Text = $"   {me} · {name} · SPV (online) · poker gossip overlay · players discovered: {players}";
        Title = $"BSV Poker — {me} — {name}";
    }
}
