using System.Windows;
using BsvPoker.App.Views;
using BsvPoker.Net;

namespace BsvPoker.App;

public partial class MainWindow : Window
{
    private readonly Profile _profile = new();
    private readonly P2PNode _node = new(0, "127.0.0.1"); // loopback by default; LAN is an explicit opt-in
    private GameView? _game;

    public MainWindow()
    {
        InitializeComponent();
        // PER-INSTANCE: each running copy gets its OWN profile (wallet + identity). A 2nd copy is a
        // DIFFERENT player, not a clone.
        Title = $"BSV Poker — {_profile.Name}";

        var vault = new CardVault(_profile.Dir, _profile.IdentityPriv, _profile.IdentityPub);
        var wallet = new WalletView(_profile.Dir, vault);
        WalletHost.Content = wallet;
        var chat = new ChatService(_node, _profile.IdentityPriv, _profile.IdentityPub, _profile.Dir);
        ChatHost.Content = new ChatView(chat);
        _game = new GameView(_node, _profile.IdentityPriv, _profile.IdentityPub, vault, wallet.RefreshCards);
        GameHost.Content = _game;

        var lobby = new LobbyView(_node, _profile.IdentityPub, JoinTable, PlayBot);
        LobbyHost.Content = lobby;

        Loaded += async (_, _) =>
        {
            _node.SetIdentity(_profile.IdentityPriv, _profile.IdentityPub); // sign presence/table announcements
            await _node.StartAsync();
            // announce the FULL pubkey as presence so peers can DM us (and find us in the lobby).
            await _node.HeartbeatAsync(Convert.ToHexString(_profile.IdentityPub).ToLowerInvariant(), $"127.0.0.1:{_node.BoundPort}");
            lobby.OnNodeReady(_node.BoundPort);
        };
        Closed += (_, _) => { try { _node.Dispose(); } catch { } };
    }

    private void JoinTable(string tableId, string tableName)
    {
        _game?.StartNetworked(tableId, tableName);
        Tabs.SelectedIndex = 2; // switch to the Game tab
    }

    private void PlayBot(BsvPoker.Core.Variant variant)
    {
        _game?.StartBot(variant);
        Tabs.SelectedIndex = 2;
    }
}
