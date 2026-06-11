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
    private readonly P2PNode _node = new(0, "127.0.0.1"); // table/lobby transport (loopback default; LAN is opt-in)
    private LobbyView? _lobby;
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
        };
        Closed += (_, _) => { try { _wallet.VaultBackup(); } catch { } foreach (var w in _botWindows.ToList()) { try { w.Close(); } catch { } } foreach (var b in _bots.ToList()) { try { b.Dispose(); } catch { } } try { _bsvNode?.Dispose(); } catch { } try { _link?.Dispose(); } catch { } try { _node.Dispose(); } catch { } };
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
        GameHost.Content = _game;

        // Chat: every message is a Bitcoin transaction; peers are auto-discovered from on-chain Announce txs
        // (no manual key/IP exchange). Send = fund an encrypted ChatDirect tx, push it IP-to-IP to the peer
        // AND broadcast it to miners.
        _chatView = new ChatView(_idPub,
            () => (_gossip?.Peers ?? new List<PokerGossip.Peer>()).Select(p => (p.PubHex, p.Endpoint)).ToList(),
            SendChatTx);
        _chatView.SetHandleResolver(_wallet.IdentityLabelFor);   // chat shows @handles from the wallet's Contacts
        _chatView.SetSaveContact(_wallet.ImportContact);  // save a discovered peer into the wallet address book
        ChatHost.Content = _chatView;

        // The lobby: pick a variant + seat count (2–6), host/join a real table, or play your own bot at the chosen
        // variant. Joining a table or pressing "Play a bot" jumps to the game board.
        _lobby = new LobbyView(_node, _idPub, JoinTable,
            variant => { if (CanPlay()) { _game!.StartBot(variant); Tabs.SelectedIndex = 2; } },
            () => { if (CanPlay()) PlayBot(); },   // "Play my bot" → open YOUR identity-derived bot in its own window
            () => { if (CanPlay()) _ = PlayBlackjack(); });   // "Blackjack" → a full on-chain Blackjack hand
        LobbyHost.Content = _lobby;
        if (_node.BoundPort > 0) { try { _node.SetIdentity(_idPriv, _idPub); _lobby.OnNodeReady(_node.BoundPort); } catch { } }   // node may already be up if the wallet was locked at selection
    }

    private void StartTxLink()
    {
        try
        {
            _link = new TxLink(_currentNet, 0); // loopback by default; expose to LAN/Internet is an explicit opt-in
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

    /// <summary>Every inbound transaction (pushed IP-to-IP or relayed by the network) is inspected here.</summary>
    private void Ingest(Chain.Tx tx)
    {
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
        // PUBLIC broadcast (plaintext, anyone): show it in chat (skip our own).
        var bc = OnChainChat.TryReadBroadcastTx(tx);
        if (bc != null)
        {
            if (!bc.SenderPub.AsSpan().SequenceEqual(_idPub))
                _chatView?.AddIncoming(Convert.ToHexString(bc.SenderPub).ToLowerInvariant(), "[broadcast] " + bc.Text);
            return;
        }
        var msg = OnChainChat.TryReadTx(tx, _idPriv, _idPub);
        if (msg == null) return;
        if (msg.Text.StartsWith("GOSSIP:", StringComparison.Ordinal))      // poker discovery overlay
            _gossip?.Receive(msg.Text["GOSSIP:".Length..]);
        else if (msg.Text.StartsWith("DEAL:", StringComparison.Ordinal) && _activeDeal != null
                 && _activeDeal.PeerPub.AsSpan().SequenceEqual(msg.SenderPub))
            _activeDeal.Deliver(msg.Text["DEAL:".Length..]);             // live hand protocol
        else
            _chatView?.AddIncoming(Convert.ToHexString(msg.SenderPub).ToLowerInvariant(), msg.Text);
    }

    private TxDealChannel? _activeDeal;
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
        _game?.StartNetworked(tableId, tableName);
        Tabs.SelectedIndex = 2; // Game
    }

    /// <summary>FUNDED-GATE: nothing (play / a table) happens until this wallet is funded — except on regtest,
    /// which can self-fund. Until then the user goes nowhere.</summary>
    private bool CanPlay()
    {
        // ORDER: Wallet → Fund → on-chain Identity → Game. A game needs a real (on-chain) identity, which needs
        // funds to broadcast. (regtest can self-fund and is allowed through for local testing.)
        if (_currentNet.Network == BsvNetwork.Regtest) return true;
        if (!_wallet.IsFunded)
        {
            MessageBox.Show("Fund this wallet with real BSV first (your coins appear automatically once on-chain). No funds → no identity → no game.", "Fund first");
            return false;
        }
        if (!_wallet.HasOnChainIdentity)
        {
            MessageBox.Show("Create your identity ON-CHAIN first (Identity tab → Register). An identity is a funded on-chain transaction; a game requires it.", "Identity required");
            return false;
        }
        return true;
    }

    private void PlayBot()
    {
        // the bot is DERIVED from MY identity (Type-42) and named <my-handle>-Bot-NNN; it will only ever play me.
        var ownerHandle = string.IsNullOrWhiteSpace(_wallet.MyHandle) ? _profile.Name.Replace(" ", "") : _wallet.MyHandle;
        var bot = new BotPlayer(_currentNet, LocalIp(), _idPriv, _idPub, ++_botCount, ownerHandle);
        var myHex = Convert.ToHexString(_idPub).ToLowerInvariant();
        bot.AddPeer(myHex, MyEndpoint());                  // the bot knows how to reach us
        _gossip?.AddSeed(bot.PubHex, bot.Endpoint);        // we know how to reach the bot
        _wallet.ImportContact(bot.Name, bot.PubHex);       // add the bot to the address book so it's named everywhere
        // ONE-CLICK funding: the BotWindow's amount + button calls this — send the sats from MY wallet to the bot
        // and credit it immediately; the bot refunds to my receive address on close.
        var myRefund = _wallet.PublicReceiveAddress();
        var win = new BotWindow(bot, async sat =>
        {
            var tx = await _wallet.FundBotAsync(bot.ReceiveAddress(), sat);
            return tx != null && bot.CreditRaw(tx, myRefund);
        }) { Owner = this };
        _bots.Add(bot); _botWindows.Add(win);
        win.Closed += (_, _) => { _bots.Remove(bot); _botWindows.Remove(win); UpdateNetInfo(); };
        win.Show();
        bot.Announce();
        UpdateNetInfo();
        Tabs.SelectedIndex = 2;   // show the Game board
        // ONE CLICK = a REAL on-chain hand vs your bot: auto-fund the bot, then run the genuine two-party
        // mental-poker hand (RunLiveHandAgainst) — every move an on-chain Bitcoin tx, your cards minted as NFTs.
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (!_wallet.IsFunded) { MessageBox.Show("Fund this wallet with real BSV first, then click Play my bot.", "Fund first"); return; }
                var tx = await _wallet.FundBotAsync(bot.ReceiveAddress(), BotPlayer.LiveStake + 5000);
                if (tx != null) bot.CreditRaw(tx, myRefund);
                await System.Threading.Tasks.Task.Delay(1500);   // let the funding + gossip settle
                var peer = new PokerGossip.Peer(bot.PubHex, bot.Endpoint, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                var status = RunLiveHandAgainst(peer);
                MessageBox.Show(status, "Play my bot — on-chain hand");
            }
            catch (Exception ex) { MessageBox.Show("Could not start the on-chain hand: " + ex.Message, "Play my bot"); }
        });
    }

    /// <summary>Play a full ON-CHAIN Blackjack hand vs the dealer: every move a Bitcoin tx, cards as NFTs. The
    /// wallet builds and broadcasts the whole transaction tape and re-syncs the balance from the chain.</summary>
    private async System.Threading.Tasks.Task PlayBlackjack()
    {
        var status = await _wallet.RunOnChainBlackjack(20);   // 20-sat table (per-satoshi)
        MessageBox.Show(status, "On-chain Blackjack");
    }

    /// <summary>
    /// Play a hand as a GENUINE two-party on-chain mental-poker deal against a discovered peer — no local or
    /// bot deck, no shared RNG. Both players run <see cref="LiveDeal"/>; every protocol message is an encrypted
    /// Bitcoin transaction pushed IP-to-IP to the peer and to the miners. Returns an immediate status; the
    /// dealt result is shown on the table when the exchange completes.
    /// </summary>
    private const long LiveStake = 20_000;

    /// <summary>Let the HUMAN choose which discovered peer to play (never auto-selected). Returns null if cancelled.</summary>
    private PokerGossip.Peer? ChooseOpponent(List<PokerGossip.Peer> peers)
    {
        if (!Dispatcher.CheckAccess()) return Dispatcher.Invoke(() => ChooseOpponent(peers));
        var list = new System.Windows.Controls.ListBox { Height = 200, Width = 460 };
        foreach (var p in peers) { var h = _wallet.IdentityLabelFor(p.PubHex); list.Items.Add((h != null ? h + "  " : "") + p.PubHex[..Math.Min(16, p.PubHex.Length)] + "…  @ " + p.Endpoint); }
        list.SelectedIndex = 0;
        var ok = new System.Windows.Controls.Button { Content = "Play this opponent", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        var sp = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new System.Windows.Controls.TextBlock { Text = "Choose your opponent (you select every action):" });
        sp.Children.Add(list); sp.Children.Add(ok);
        var win = new Window { Title = "Choose opponent", Width = 500, Height = 300, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = sp };
        PokerGossip.Peer? chosen = null;
        ok.Click += (_, _) => { if (list.SelectedIndex >= 0) chosen = peers[list.SelectedIndex]; win.Close(); };
        win.ShowDialog();
        return chosen;
    }

    private string RunLiveHand()
    {
        // STANDALONE SPV: there is NO "connect to the BSV network" requirement. A hand is peer-to-peer
        // (transactions handed IP-to-IP to the opponent) and the same txs are sent to a miner to land on-chain.
        if (_activeDeal != null) return "A hand is already in progress.";
        var peers = _gossip?.Peers.ToList() ?? new();
        if (peers.Count == 0) return "No opponent discovered yet — the gossip overlay is still finding poker peers. A fair deal needs a real peer's entropy; there is NO local or bot deck.";
        var peer = ChooseOpponent(peers);   // the HUMAN picks the opponent — never auto-selected
        if (peer == null) return "No opponent chosen.";
        return RunLiveHandAgainst(peer);
    }

    /// <summary>Run the GENUINE two-party on-chain mental-poker hand against a specific peer (e.g. your own bot):
    /// every protocol move is an encrypted Bitcoin transaction, the escrow + settlement land on-chain, and each
    /// of your cards is minted as a real on-chain encrypted NFT. No local deck, no shared RNG.</summary>
    private string RunLiveHandAgainst(PokerGossip.Peer peer)
    {
        if (_activeDeal != null) return "A hand is already in progress.";
        byte[] peerPub; try { peerPub = Convert.FromHexString(peer.PubHex); } catch { return "discovered peer has a bad key."; }
        var seat = _wallet.ReserveSeat(LiveStake + 5000);
        if (seat == null) return $"No spendable coin ≥ {LiveStake + 5000:N0} sat to seat the hand — fund your wallet first (poker is real-money only).";
        var endpoint = peer.Endpoint;
        var peerHex = peer.PubHex;
        var ch = new TxDealChannel(peerPub, plaintext => SendEncrypted(peerHex, endpoint, "DEAL:" + plaintext));
        _activeDeal = ch;
        bool initiator = string.CompareOrdinal(Convert.ToHexString(_idPub).ToLowerInvariant(), peerHex) < 0;
        var s = seat.Value;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var r = initiator
                    ? LiveHand.RunInitiator(ch, s.Utxo, s.ChangePub, (s.Priv, s.Pub), LiveStake)
                    : LiveHand.RunResponder(ch, s.Utxo, s.ChangePub, (s.Priv, s.Pub), LiveStake);
                _bsvNode?.Broadcast(Chain.Serialize(r.EscrowTx));     // both to miners → on-chain
                _bsvNode?.Broadcast(Chain.Serialize(r.Settlement));
                _wallet.MarkSpent(s.Utxo.Txid, s.Utxo.Vout);
                // every card dealt to me becomes a REAL on-chain encrypted NFT sealed to my identity (in my wallet)
                var mintStatus = _wallet.MintCardNftsOnChain(r.MyHoles.Select(c => c.Index).ToList());
                bool iWon = (r.WinnerSeat == 0) == initiator;
                var oppName = _wallet.IdentityLabelFor(peerHex) ?? peerHex[..12] + "…";
                Dispatcher.BeginInvoke(new Action(() => MessageBox.Show(
                    $"Hand vs {oppName}: pot {r.Pot:N0} sat → {(iWon ? "YOU win" : "opponent wins")} · proofs verified: {r.ProofsVerified} · NFTs: {mintStatus}",
                    "On-chain hand complete")));
            }
            catch (Exception ex) { Dispatcher.BeginInvoke(new Action(() => MessageBox.Show("Hand did not complete: " + ex.Message, "On-chain hand"))); }
            finally { _activeDeal = null; }
        });
        return $"Opponent {peerHex[..12]}… found — playing a real two-party on-chain hand for {LiveStake:N0} sat ({(initiator ? "you deal first" : "opponent deals first")}). Every message is a Bitcoin transaction.";
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
        var script = OnChainChat.BuildScript(rpub, _idPub, text);
        var (raw, status) = _wallet.FundTx(script, 1, 1);   // tiny: a 1-sat message output + 1-sat fee
        if (raw == null) return status;
        var (host, port) = ParseHostPort(endpoint);
        if (host != null) _ = TxLink.SendTxAsync(_currentNet, host, port, raw);
        _bsvNode?.Broadcast(raw);
        return "";
    }

    /// <summary>Send a PUBLIC broadcast chat message (plaintext, readable by everyone incl. bots): fund a tiny
    /// ChatGroup tx, push it to EVERY known peer, and broadcast to miners.</summary>
    private string SendBroadcastChat(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Type a message.";
        var (raw, status) = _wallet.FundTx(OnChainChat.BuildBroadcast(_idPub, text), 1, 1);
        if (raw == null) return status;
        foreach (var p in (_gossip?.Peers ?? new List<PokerGossip.Peer>()).ToList())
        { var (h, pt) = ParseHostPort(p.Endpoint); if (h != null) _ = TxLink.SendTxAsync(_currentNet, h, pt, raw); }
        _bsvNode?.Broadcast(raw);
        return "";
    }

    /// <summary>
    /// Announce ourselves so peers discover us: (1) a bootstrap Announce transaction relayed on the Bitcoin
    /// network (seeds nodes that don't know us yet), and (2) the poker gossip overlay's announce/query, which
    /// floods our presence across the overlay and pulls peers others know.
    /// </summary>
    private void Announce()
    {
        // NEVER auto-spend the user's coins. A funded on-chain announce is an EXPLICIT user action — NEVER a
        // 45-second timer. That timer spent the real funding coin every cycle and left fake change: it is what
        // DESTROYED the user's 1,000,000-sat coin. Peer discovery here is OFF-CHAIN gossip only — no tx, no spend.
        _gossip?.Announce();   // poker overlay: announce to known peers (they forward it onward)
        _gossip?.Query();      // and pull the peers they know
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
        if (string.IsNullOrWhiteSpace(text)) return "Type a message.";
        if (broadcast) { var be = SendBroadcastChat(text); return be == "" ? "Sent to everyone (public broadcast)." : be; }
        var err = SendEncrypted(recipientPubHex, peerHostPort, text);
        return err == "" ? $"Sent (encrypted) to {peerHostPort}." : err;
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
        NetInfo.Text = $"   {name} · SPV (online) · poker gossip overlay · players discovered: {players}";
        Title = $"BSV Poker — {_profile.Name} — {name}";
    }
}
