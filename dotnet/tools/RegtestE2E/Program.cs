using System.Diagnostics;
using System.Text.RegularExpressions;
using BsvPoker.Core;
using BsvPoker.Core.Games;
using BsvPoker.Core.Script;
using BsvPoker.Crypto;
using BsvPoker.Net.Bsv;

// REAL end-to-end on a live BSV regtest node — proves the no-server money spine AND the on-chain poker pot
// mechanic against actual consensus. Nothing faked: real addresses, real funding, real SPV proofs, real
// consensus-valid transactions the node accepts and mines.
//   Phase 1: SPV-fund a wallet, build+sign+broadcast a plain spend, node mines it.
//   Phase 2: fund a 2-of-2 poker escrow (real tx), then cooperatively settle the pot to the winner — both
//            real txs the node accepts and mines. This is exactly how a hand's pot is funded and paid out.

const string P2P = "127.0.0.1";
const int P2PPort = 19444;

static Card C(string s)
{
    int rank = s[0] switch { 'A' => 14, 'K' => 13, 'Q' => 12, 'J' => 11, 'T' => 10, _ => s[0] - '0' };
    var suit = s[1] switch { 's' => Suit.Spades, 'h' => Suit.Hearts, 'd' => Suit.Diamonds, _ => Suit.Clubs };
    return new Card(rank, suit);
}

static string Cli(string args)
{
    var psi = new ProcessStartInfo("wsl.exe",
        $"-d Ubuntu -u root -- docker exec bsvreg bitcoin-cli -regtest -rpcuser=u -rpcpassword=p -rpcport=18443 {args}")
    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    var p = Process.Start(psi)!;
    string o = p.StandardOutput.ReadToEnd(), e = p.StandardError.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0) throw new Exception($"cli '{args}' failed: {e.Trim()}");
    return o.Trim();
}

Console.WriteLine("== BSV REGTEST END-TO-END (real node, real consensus) ==");

var nodeAddr = Cli("getnewaddress");
Cli($"generatetoaddress 101 {nodeAddr}");                   // mature coinbase → node has spendable coins
Console.WriteLine($"node funded; height = {Cli("getblockcount")}");

var seed = WalletKeys.NewSeed();
var net = NetworkParams.For(BsvNetwork.Regtest);

var node = new BsvNode(net);
if (!await node.ConnectAsync(P2P, P2PPort)) { Console.WriteLine("FAIL: could not connect/handshake the node"); return 1; }
var storePath = Path.Combine(Path.GetTempPath(), "bsvpoker-regtest-e2e.dat");
if (File.Exists(storePath)) File.Delete(storePath);
var store = new HeaderStore(storePath);
await node.SyncHeadersToStoreAsync(store, maxBatches: 5, waitMs: 6000);
Console.WriteLine($"client connected (peers={node.PeerCount}); headers validated to {store.Count}");

bool ConfirmMined(string txid, string label)
{
    try
    {
        var json = Cli($"getrawtransaction {txid} true");
        var c = Regex.Match(json, "\"confirmations\"\\s*:\\s*(\\d+)").Groups[1].Value;
        bool ok = int.TryParse(c, out var n) && n >= 1;
        Console.WriteLine(ok ? $"  ✓ node mined {label} ({n} conf)" : $"  ✗ {label} not confirmed (conf='{c}')");
        return ok;
    }
    catch (Exception ex) { Console.WriteLine($"  ✗ node never saw {label}: {ex.Message}"); return false; }
}

// Fund one of our receive keys with `btc`, mine it, then SPV-verify it into a UTXO against our own headers.
async Task<(OnChainWallet.Utxo Utxo, WalletKeys Key)> FundAndVerify(uint recvIndex, string btc)
{
    var key = WalletKeys.Account(seed, 0, recvIndex);
    var payload = new byte[21]; payload[0] = net.AddressVersion; Hashes.Hash160(key.Pub).CopyTo(payload, 1);
    var addr = Base58.CheckEncode(payload);
    var txid = Cli($"sendtoaddress {addr} {btc}");
    var blockHash = Regex.Match(Cli($"generatetoaddress 1 {nodeAddr}"), "[0-9a-f]{64}").Value;
    await node.SyncHeadersToStoreAsync(store, maxBatches: 3, waitMs: 6000);   // pull the new block's header
    var raw = await node.GetBlockAsync(blockHash, waitMs: 15000) ?? throw new Exception("node did not serve block");
    var parsed = BsvBlock.Parse(raw);
    int idx = parsed.Txs.FindIndex(t => Chain.Txid(t) == txid);
    if (idx < 0) throw new Exception("funding tx not in block");
    var lockMe = Chain.P2pkhLock(payload[1..]);
    uint vout = (uint)parsed.Txs[idx].Outs.FindIndex(o => o.Script.AsSpan().SequenceEqual(lockMe));
    var (chain, _) = store.BuildChain();
    var mb = PartialMerkleTree.BuildMerkleBlock(parsed.Header, parsed.Txids, new HashSet<int> { idx });
    var utxo = SpvFunding.VerifyFromMerkleBlock(parsed.Txs[idx], vout, mb, chain, key.Pub, 0, recvIndex)
               ?? throw new Exception("SPV funding did not verify");
    Console.WriteLine($"  SPV-verified {utxo.Value} sat at {utxo.Txid[..12]}…:{utxo.Vout}");
    return (utxo, key);
}

Console.WriteLine("\n-- Phase 1: SPV funding → plain signed spend → node mines it --");
var (u0, _) = await FundAndVerify(0, "1.0");
var w1 = new OnChainWallet(seed); w1.Add(u0);
var backPayload = Base58.CheckDecode(Cli("getnewaddress"));
var spend = w1.BuildAction(Chain.P2pkhLock(backPayload[1..]), 50_000_000, 1000);
if (!w1.VerifySpend(spend)) { Console.WriteLine("FAIL: spend self-verify"); return 1; }
node.Broadcast(Chain.Serialize(spend.Tx));
var spendTxid = Chain.Txid(spend.Tx);
Console.WriteLine($"broadcast spend {spendTxid[..12]}…");
await Task.Delay(1500); Cli($"generatetoaddress 1 {nodeAddr}");
bool ok1 = ConfirmMined(spendTxid, "plain spend");

Console.WriteLine("\n-- Phase 2: on-chain poker pot — fund 2-of-2 escrow → cooperative settlement --");
var (u1, _) = await FundAndVerify(3, "1.0");
var a = WalletKeys.Account(seed, 0, 10);   // player A
var b = WalletKeys.Account(seed, 0, 11);   // player B
long pot = 50_000_000;
var w2 = new OnChainWallet(seed); w2.Add(u1);
var fund = OnChainHand.FundEscrow(w2, a.Pub, b.Pub, pot, 1000);   // real 2-of-2 escrow tx
node.Broadcast(Chain.Serialize(fund.Tx));
var escrowTxid = Chain.Txid(fund.Tx);
Console.WriteLine($"broadcast escrow funding {escrowTxid[..12]}… (pot {pot} sat into 2-of-2)");
await Task.Delay(1500); Cli($"generatetoaddress 1 {nodeAddr}");
bool ok2 = ConfirmMined(escrowTxid, "escrow funding");

// cooperative settlement: both players sign, pot paid to the winner (A)
var settle = OnChainHand.Settle(escrowTxid, 0, pot, a.Pub, 1000, a.Priv, a.Pub, b.Priv, b.Pub);
node.Broadcast(Chain.Serialize(settle));
var settleTxid = Chain.Txid(settle);
Console.WriteLine($"broadcast settlement {settleTxid[..12]}… (pot → winner A)");
await Task.Delay(1500); Cli($"generatetoaddress 1 {nodeAddr}");
bool ok3 = ConfirmMined(settleTxid, "pot settlement");

Console.WriteLine("\n-- Phase 3: always-recoverable — pre-signed nLockTime escrow recovery (rejected early, accepted after lock) --");
var (u2, _) = await FundAndVerify(5, "1.0");
var w3 = new OnChainWallet(seed); w3.Add(u2);
var fund2 = OnChainHand.FundEscrow(w3, a.Pub, b.Pub, pot, 1000);
node.Broadcast(Chain.Serialize(fund2.Tx));
var escrow2Txid = Chain.Txid(fund2.Tx);
await Task.Delay(1500); Cli($"generatetoaddress 1 {nodeAddr}");
bool ok4 = ConfirmMined(escrow2Txid, "recovery-test escrow funding");

int h0 = int.Parse(Cli("getblockcount"));
uint lockHeight = (uint)(h0 + 4);   // recovery becomes valid only after this height
var recovery = OnChainHand.Recover(escrow2Txid, 0, a.Pub, pot / 2, b.Pub, pot - pot / 2, 1000, lockHeight, a.Priv, b.Priv);
var recTxid = Chain.Txid(recovery);

// (a) premature: broadcast before the lock height; mining one block must NOT include it (timelock enforced)
node.Broadcast(Chain.Serialize(recovery));
await Task.Delay(1500); Cli($"generatetoaddress 1 {nodeAddr}");
bool prematureRejected = !ConfirmMined(recTxid, "premature recovery (MUST be rejected)");
Console.WriteLine(prematureRejected ? "  ✓ timelock enforced: recovery not mineable before lock height" : "  ✗ recovery was mined too early!");

// (b) mine past the lock height, rebroadcast; now it must be accepted and mined
int hNow = int.Parse(Cli("getblockcount"));
if (hNow <= lockHeight) Cli($"generatetoaddress {lockHeight - hNow + 1} {nodeAddr}");
node.Broadcast(Chain.Serialize(recovery));
await Task.Delay(1500); Cli($"generatetoaddress 1 {nodeAddr}");
bool ok5 = ConfirmMined(recTxid, "recovery after lock height");

Console.WriteLine("\n-- Phase 4: full on-chain Omaha Hi-Lo hand — escrow → SPLIT settlement (high + low) --");
var (u3, _) = await FundAndVerify(7, "1.0");
var w4 = new OnChainWallet(seed); w4.Add(u3);
var pa = WalletKeys.Account(seed, 0, 20);   // player A (will win the high half)
var pb = WalletKeys.Account(seed, 0, 21);   // player B (will win the low half)
var hiloDeck = new[] { C("Kh"), C("Kd"), C("9s"), C("9c"),     // seat0 holes: trip kings (high)
                       C("Ah"), C("5d"), C("6s"), C("8c"),     // seat1 holes: 7-low (low)
                       C("2h"), C("3d"), C("7s"), C("Kc"), C("Qh") }; // board
var hand = OnChainGameSession.PlayHand(PokerGame.OmahaHiLo, w4, (pa.Priv, pa.Pub), (pb.Priv, pb.Pub), hiloDeck, pot, 1000);
node.Broadcast(Chain.Serialize(hand.Funding.Tx));
var handEscrowTxid = Chain.Txid(hand.Funding.Tx);
await Task.Delay(1500); Cli($"generatetoaddress 1 {nodeAddr}");
bool ok6 = ConfirmMined(handEscrowTxid, "hi-lo escrow funding");
node.Broadcast(Chain.Serialize(hand.Settlement));
var handSettleTxid = Chain.Txid(hand.Settlement);
Console.WriteLine($"broadcast hi-lo split settlement {handSettleTxid[..12]}… (split={hand.Split}, {hand.Settlement.Outs.Count} outputs)");
await Task.Delay(1500); Cli($"generatetoaddress 1 {nodeAddr}");
bool ok7 = hand.Split && hand.Settlement.Outs.Count == 2 && ConfirmMined(handSettleTxid, "hi-lo SPLIT settlement");

Console.WriteLine("\n-- Phase 5: TYPED transaction (every action is its own on-chain frame type) — build → mine → SPEND --");
var (u4, _) = await FundAndVerify(9, "1.0");
var w5 = new OnChainWallet(seed); w5.Add(u4);
var owner = WalletKeys.Account(seed, 0, 30);
// a TableGenesis typed output (4 documented fields), spendable by the owner — proves typed txs are real, not OP_RETURN
var fields = new byte[][] {
    System.Security.Cryptography.RandomNumberGenerator.GetBytes(16),   // tableId
    new byte[] { 0 },                                                  // variant = Hold'em
    new byte[] { 2 },                                                  // seats
    BitConverter.GetBytes(1000L) };                                    // stakes
var typedScript = TxTemplates.BuildOutput(TxKind.TableGenesis, fields, owner.Pub);
var parsedTyped = TxTemplates.Parse(typedScript);
bool parseOk = parsedTyped is { Kind: TxKind.TableGenesis } && parsedTyped.Fields.Length == 4
               && parsedTyped.OwnerPub.AsSpan().SequenceEqual(owner.Pub);
Console.WriteLine($"  typed output parses back: kind={parsedTyped?.Kind}, fields={parsedTyped?.Fields.Length}, ownerOk={parseOk}");
long typedVal = 10000;
var typedFund = w5.BuildAction(typedScript, typedVal, 1000);
node.Broadcast(Chain.Serialize(typedFund.Tx));
var typedTxid = Chain.Txid(typedFund.Tx);
await Task.Delay(1500); Cli($"generatetoaddress 1 {nodeAddr}");
bool ok8 = ConfirmMined(typedTxid, "typed TableGenesis output");
// now SPEND the typed output: scriptSig is just <sig> over the FORKID contract sighash (pubkey is in the script)
var sink = Base58.CheckDecode(Cli("getnewaddress"));
var spendTyped = new Chain.Tx(2,
    new() { new(typedTxid, 0, Array.Empty<byte>(), 0xffffffff) },
    new() { new(typedVal - 1000, Chain.P2pkhLock(sink[1..])) }, 0);
var tsig = TxScriptChecker.Sign(spendTyped, 0, typedScript, typedVal, owner.Priv);
var ss = new byte[1 + tsig.Length]; ss[0] = (byte)tsig.Length; Array.Copy(tsig, 0, ss, 1, tsig.Length);
spendTyped = spendTyped with { Ins = new() { spendTyped.Ins[0] with { ScriptSig = ss } } };
var typedSpendTxid = Chain.Txid(spendTyped);
var rawHex = Convert.ToHexString(Chain.Serialize(spendTyped)).ToLowerInvariant();
try { Console.WriteLine($"  sendrawtransaction → {Cli($"sendrawtransaction {rawHex}")}"); }
catch (Exception ex) { Console.WriteLine($"  typed spend REJECTED: {ex.Message}"); }
await Task.Delay(800); Cli($"generatetoaddress 1 {nodeAddr}");
bool ok9 = parseOk && ConfirmMined(typedSpendTxid, "spend of the typed output");

Console.WriteLine("\n-- Phase 6: a WHOLE HAND as a tape of on-chain transactions (maximize transactions) --");
var (u5, _) = await FundAndVerify(11, "1.0");
var w6 = new OnChainWallet(seed); w6.Add(u5);
var ta = WalletKeys.Account(seed, 0, 40); var tb = WalletKeys.Account(seed, 0, 41);
var handDeck = new[] { C("As"), C("Ah"), C("2c"), C("3d"), C("Ad"), C("Kh"), C("Qs"), C("Jc"), C("9h") };
var tape = OnChainHandTape.BuildHoldem(w6, (ta.Priv, ta.Pub), (tb.Priv, tb.Pub), handDeck, 40000,
    System.Security.Cryptography.RandomNumberGenerator.GetBytes(16), stepValue: 1000, fee: 1000);
Console.WriteLine($"  built a {tape.Steps.Count}-transaction hand tape; submitting each step in order…");
int accepted = 0;
foreach (var step in tape.Steps)
{
    var hex = Convert.ToHexString(Chain.Serialize(step.Tx)).ToLowerInvariant();
    try { Cli($"sendrawtransaction {hex}"); accepted++; node.Broadcast(Chain.Serialize(step.Tx)); }
    catch (Exception ex) { Console.WriteLine($"    ✗ {step.Kind} REJECTED: {ex.Message.Replace("\n", " ")}"); }
}
await Task.Delay(1500); Cli($"generatetoaddress 2 {nodeAddr}"); // 2 blocks to sweep any just-submitted straggler
int minedCount = 0;
for (int i = 0; i < tape.Steps.Count; i++)
{
    var step = tape.Steps[i];
    bool mined = false;
    try { mined = Regex.IsMatch(Cli($"getrawtransaction {Chain.Txid(step.Tx)} true"), "\"confirmations\"\\s*:\\s*[1-9]"); } catch { }
    if (mined) minedCount++; else Console.WriteLine($"    ✗ step {i} {step.Kind} ({Chain.Txid(step.Tx)[..12]}…) not mined");
}
bool ok10 = minedCount == tape.Steps.Count;
Console.WriteLine($"  accepted to mempool: {accepted}/{tape.Steps.Count}");
Console.WriteLine(ok10 ? $"  ✓ all {minedCount} hand-tape transactions mined into the chain"
                       : $"  ✗ only {minedCount}/{tape.Steps.Count} tape transactions mined");

node.Dispose();
if (ok1 && ok2 && ok3 && ok4 && prematureRejected && ok5 && ok6 && ok7 && ok8 && ok9 && ok10)
{
    Console.WriteLine("\nSUCCESS ✓✓✓✓✓✓ END-TO-END PROVEN on real BSV consensus:");
    Console.WriteLine("  • fund → SPV-verify → signed spend (mined)");
    Console.WriteLine("  • poker pot: 2-of-2 escrow (mined) → cooperative settlement to winner (mined)");
    Console.WriteLine("  • always-recoverable: pre-signed nLockTime recovery REJECTED before lock, ACCEPTED after (mined)");
    Console.WriteLine("  • full Omaha Hi-Lo hand: escrow → SPLIT settlement paying high + low halves (mined)");
    Console.WriteLine("  • TYPED transaction template: built, mined, and SPENT on-chain (not OP_RETURN)");
    Console.WriteLine($"  • WHOLE HAND as {tape.Steps.Count} on-chain transactions (table/game/hand/escrow/shuffle/deal/board/bet/showdown/settlement) — all mined");
    return 0;
}
Console.WriteLine("\nFAIL: one or more phases not confirmed");
return 1;
