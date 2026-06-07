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
    private sealed class UtxoRec { public string Txid { get; set; } = ""; public uint Vout { get; set; } public long Value { get; set; } public uint KeyChain { get; set; } public uint KeyIndex { get; set; } public bool Spent { get; set; } }
    private sealed class File_ { public string Seed { get; set; } = ""; public int RecvIndex { get; set; } public List<UtxoRec> Utxos { get; set; } = new(); public List<Tx> History { get; set; } = new(); }

    private readonly string _path;
    private File_ _w = new();
    private byte[] _seed = Array.Empty<byte>();   // the 32-byte master seed held in memory
    private bool _locked;                          // true when the wallet is encrypted and not yet unlocked this session

    private readonly Func<BsvNode?> _node;          // current network's live node (for broadcast); may be null/peerless
    private readonly Func<HeaderStore?> _store;     // current network's validated header store (for SPV verification)
    private readonly Func<NetworkParams> _net;      // current network parameters (address version, etc.)

    private readonly TextBlock _bal = new() { Foreground = Brushes.White, FontSize = 28, FontWeight = FontWeights.Bold };
    private readonly TextBlock _recv = new() { Foreground = Brushes.LightGreen, FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap };
    private readonly ListView _history = new() { Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x0F, 0x0F)), Foreground = Brushes.White, BorderThickness = new Thickness(0), Height = 200 };
    private readonly TextBox _amount = new() { Width = 110, Text = "10000" };
    private readonly TextBox _fee = new() { Width = 70, Text = "500" };
    private readonly TextBox _dest = new() { Width = 300 };
    private readonly TextBlock _status = new() { Foreground = Brushes.Gray, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly CardVault _vault;
    private readonly WrapPanel _cards = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _cardsLabel = new() { Foreground = Brushes.Gray, Margin = new Thickness(0, 14, 0, 2), Text = "My cards (NFTs)" };

    public WalletView(string dataDir, CardVault vault, Func<BsvNode?> node, Func<HeaderStore?> store, Func<NetworkParams> net)
    {
        _vault = vault; _node = node; _store = store; _net = net;
        Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0D)); // dark behind light text (no white-on-white)
        Foreground = Brushes.White;
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "wallet.json");
        Load();

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = "Wallet — real BSV (SPV, on-chain)", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
        root.Children.Add(new TextBlock { Text = "Balance (confirmed, satoshis)", Foreground = Brushes.Gray, Margin = new Thickness(0, 10, 0, 0) });
        root.Children.Add(_bal);

        // No card UI at all unless real on-chain card NFTs exist — there is nothing card-like to show with no sats.
        // (Real 1-sat card NFTs from on-chain Deal transactions will populate this once that path is wired.)

        root.Children.Add(new TextBlock { Text = "Receive address (give this to your funder)", Foreground = Brushes.Gray, Margin = new Thickness(0, 10, 0, 2) });
        root.Children.Add(_recv);
        var newAddr = Btn("New address"); newAddr.Click += (_, _) => { if (Guard()) { _w.RecvIndex++; Save(); Render(); } };
        var importBtn = Btn("Import funding (SPV envelope)…"); importBtn.Click += (_, _) => { if (Guard()) ImportFunding(); };
        var makeEnv = Btn("Create funding envelope…"); makeEnv.Click += async (_, _) => { if (Guard()) await CreateEnvelope(); };
        root.Children.Add(new WrapPanel { Children = { newAddr, importBtn, makeEnv } });

        var send = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        send.Children.Add(new TextBlock { Text = "Send  amount ", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        send.Children.Add(_amount);
        send.Children.Add(new TextBlock { Text = "  fee ", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        send.Children.Add(_fee);
        send.Children.Add(new TextBlock { Text = "  to ", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        send.Children.Add(_dest);
        var sendBtn = Btn("Send"); sendBtn.Click += (_, _) => DoSend();
        send.Children.Add(sendBtn);
        root.Children.Add(send);

        root.Children.Add(new TextBlock { Text = "History", Foreground = Brushes.Gray, Margin = new Thickness(0, 14, 0, 4) });
        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = "Time", Width = 170, DisplayMemberBinding = new System.Windows.Data.Binding("Time") });
        gv.Columns.Add(new GridViewColumn { Header = "Type", Width = 90, DisplayMemberBinding = new System.Windows.Data.Binding("Type") });
        gv.Columns.Add(new GridViewColumn { Header = "Amount (sat)", Width = 110, DisplayMemberBinding = new System.Windows.Data.Binding("Amount") });
        gv.Columns.Add(new GridViewColumn { Header = "Balance", Width = 100, DisplayMemberBinding = new System.Windows.Data.Binding("Balance") });
        gv.Columns.Add(new GridViewColumn { Header = "Memo", Width = 300, DisplayMemberBinding = new System.Windows.Data.Binding("Memo") });
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
        var sign = Btn("Sign message…");
        sign.Click += (_, _) => { if (Guard()) SignMessageDialog(); };
        var verify = Btn("Verify message…");
        verify.Click += (_, _) => VerifyMessageDialog();
        var coins = Btn("Coins (UTXOs)…");
        coins.Click += (_, _) => { if (Guard()) ShowCoins(); };
        adv.Children.Add(sign); adv.Children.Add(verify); adv.Children.Add(coins);
        root.Children.Add(adv);
        root.Children.Add(_status);

        Content = new ScrollViewer { Content = root };
        Render();
    }

    // ---- real on-chain funding & spending ----

    private long Balance => _w.Utxos.Where(u => !u.Spent).Sum(u => u.Value);

    /// <summary>True when the wallet can fund an on-chain hand of the given pot right now.</summary>
    public bool CanPlayOnChain(long pot) => !_locked && _node()?.PeerCount > 0 && Balance >= pot + OnChainHandReserve;

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

    /// <summary>
    /// Settle a COMPLETED hand on-chain through the real product: emit the full transaction tape (table/game/
    /// hand genesis, the 2-of-2 pot escrow, every shuffle/deal/board/bet/showdown step, and the cooperative
    /// settlement) funded from this wallet and broadcast over our own BSV node. The two seats' keys are derived
    /// from this wallet's seed (the local bot opponent is controlled by the same human, so funds stay
    /// recoverable). Returns a human-readable status. This is the same tape proven on a real node by RegtestE2E.
    /// </summary>
    public string PlayOnChainHand(IReadOnlyList<Card> deck, long pot)
    {
        if (_locked) return "🔒 Unlock the wallet first.";
        var node = _node();
        if (node == null || node.PeerCount == 0) return "Not connected to any BSV peers yet — cannot broadcast on-chain.";
        var w = new OnChainWallet(_seed);
        foreach (var u in _w.Utxos.Where(u => !u.Spent)) w.Add(new OnChainWallet.Utxo(u.Txid, u.Vout, u.Value, u.KeyChain, u.KeyIndex));
        if (w.Balance < pot + OnChainHandReserve) return $"Insufficient on-chain balance: have {w.Balance:N0} sat, need ~{pot + OnChainHandReserve:N0}.";
        try
        {
            var a = WalletKeys.Account(_seed, 2, 0);
            var b = WalletKeys.Account(_seed, 2, 1);
            long before = w.Balance;
            var tape = OnChainHandTape.BuildHoldem(w, (a.Priv, a.Pub), (b.Priv, b.Pub), deck, pot,
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            foreach (var step in tape.Steps) node.Broadcast(Chain.Serialize(step.Tx));
            // reconcile our spendable set with the wallet's post-hand state (the parked typed outputs leave the wallet)
            _w.Utxos = w.Coins.Select(u => new UtxoRec { Txid = u.Txid, Vout = u.Vout, Value = u.Value, KeyChain = u.KeyChain, KeyIndex = u.KeyIndex }).ToList();
            AppendTx("on-chain hand", w.Balance - before, $"{tape.Steps.Count} txs · pot {pot} · winner seat {tape.WinnerSeat}");
            Save(); Render();
            return $"Broadcast {tape.Steps.Count} on-chain transactions for the hand (pot {pot:N0} sat → seat {tape.WinnerSeat}). Fees/values deducted.";
        }
        catch (Exception ex) { return "On-chain settle failed: " + ex.Message; }
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
                _w.Utxos.Add(new UtxoRec { Txid = utxo.Txid, Vout = utxo.Vout, Value = utxo.Value, KeyChain = utxo.KeyChain, KeyIndex = utxo.KeyIndex });
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

    private static Button Btn(string t) => new() { Content = t, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 6, 10, 6) };

    // ---- persistence / seed lifecycle ----

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
            // brand-new wallet: a fresh seed, and EMPTY — no play money, no opening balance, no UTXOs.
            _seed = WalletKeys.NewSeed();
            _w = new File_ { Seed = WalletKeys.SeedToBackup(_seed), RecvIndex = 0 };
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

    private void Restore()
    {
        var box = new TextBox { AcceptsReturn = true, Height = 80, Width = 460, TextWrapping = TextWrapping.Wrap };
        var ok = new Button { Content = "Restore", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        var win = new Window { Title = "Restore wallet — enter your seed backup", Width = 500, Height = 200, Owner = Window.GetWindow(this), Content = new StackPanel { Margin = new Thickness(12), Children = { box, ok } } };
        ok.Click += (_, _) =>
        {
            try { _seed = WalletKeys.BackupToSeed(box.Text.Trim()); _w = new File_ { Seed = WalletKeys.SeedToBackup(_seed), RecvIndex = 0 }; _locked = false; AppendTx("restore", 0, "wallet restored from seed"); Save(); Render(); win.Close(); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Invalid seed"); }
        };
        win.ShowDialog();
    }

    private void AppendTx(string type, long amount, string memo)
        => _w.History.Add(new Tx { Time = DateTime.UtcNow.ToString("u"), Type = type, Amount = amount, Balance = Balance, Memo = memo });

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
        _bal.Text = Balance.ToString("N0") + " sat";
        _recv.Text = ReceiveAddress() + $"   (#{_w.RecvIndex})";
        _history.ItemsSource = _w.History.AsEnumerable().Reverse().ToList();
        RefreshCards();
    }

    /// <summary>
    /// Card NFTs are 1-sat ON-CHAIN outputs created by real Deal transactions. None are shown unless they
    /// have on-chain provenance — there are no free/phantom cards, so with no sats there are no cards.
    /// </summary>
    public void RefreshCards()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(RefreshCards)); return; }
        _cards.Children.Clear();
        _cardsLabel.Text = "Card NFTs (1-sat on-chain outputs): 0 — cards appear only when dealt by real transactions";
    }
}
