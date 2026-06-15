using BsvPoker.Core;
using BsvPoker.Core.Games;
using BsvPoker.Crypto;
using BsvPoker.Net;

namespace BsvPoker.Tests;

/// <summary>
/// MULTIPLAYER group Blackjack over the real mesh: distinct-wallet players on separate nodes join ONE table, the
/// dealerless deck is dealt jointly (dealer hole stays sealed until the dealer plays), each player plays, the
/// dealer plays to 17, and EVERY node independently settles to the SAME result (consensus). The table then runs
/// CONTINUOUSLY — hand after hand, carrying each player's bankroll — until a player LEAVES (cashing out), exactly
/// like a real table. Never one-on-one, no single dealer.
/// </summary>
public static class NetBlackjackTests
{
    private static bool Until(Func<bool> c, int ms) { var dl = Environment.TickCount64 + ms; while (Environment.TickCount64 < dl) { if (c()) return true; Thread.Sleep(20); } return c(); }

    // Spin up N peers in one loopback mesh.
    private static (P2PNode[] nodes, (byte[] Priv, byte[] Pub)[] ids) Mesh(int n)
    {
        var ids = Enumerable.Range(0, n).Select(_ => Secp256k1.GenerateKeyPair()).ToArray();
        var nodes = new P2PNode[n];
        nodes[0] = new P2PNode(0, "127.0.0.1"); nodes[0].SetIdentity(ids[0].Priv, ids[0].Pub); nodes[0].StartAsync().Wait();
        for (int p = 1; p < n; p++)
        {
            nodes[p] = new P2PNode(0, "127.0.0.1"); nodes[p].SetIdentity(ids[p].Priv, ids[p].Pub);
            var seeds = Enumerable.Range(0, p).Select(j => new P2PNode.PeerAddr("127.0.0.1", nodes[j].BoundPort)).ToArray();
            nodes[p].StartAsync(seeds).Wait();
        }
        return (nodes, ids);
    }

    // Drive every node to STAND on its turn (deterministic). Returns when the predicate holds or it times out.
    private static bool DriveStand(NetBlackjack[] g, Func<bool> until, int ms) => Until(() =>
    {
        foreach (var x in g) if (x.State == NetBlackjack.Phase.Playing && x.ToAct == x.MySeat) x.Act(BjAction.Stand);
        return until();
    }, ms);

    public static void All()
    {
        Console.WriteLine("networked group Blackjack (joint dealer, consensus, continuous play, leave/cash-out):");

        T.Run("3 players join one table, deal jointly, play, and ALL nodes settle the first hand to the same result", () =>
        {
            const int N = 3;
            var (nodes, ids) = Mesh(N);
            NetBlackjack[] g = Array.Empty<NetBlackjack>();
            try
            {
                T.True(Until(() => nodes.All(n => n.PeerCount >= N - 1), 20000), "the 3 nodes form one mesh");
                const string table = "t-bj01~p3~b10";
                g = ids.Select((id, i) => new NetBlackjack(nodes[i], table, id.Priv, id.Pub)).ToArray();
                foreach (var x in g) { x.HandPauseMs = 400; x.Start(); }   // short between-hand pause for fast tests (UI default is 10s)

                T.True(Until(() => g.All(x => x.MyHand.Count == 2 && x.DealerCards.Count >= 1 && x.MySeat >= 0), 40000),
                    "each player is dealt 2 cards and sees the dealer up card (joint deal)");
                T.True(g.Select(x => string.Join(",", x.SeatPubs)).Distinct().Count() == 1, "all nodes agree on the seat order");

                // capture each node's view of the FIRST hand the moment it settles (HandOver), before the auto re-deal
                var capNet = new long[N][]; var capOut = new BjOutcome[N][]; var capDealer = new string[N];
                bool done = DriveStand(g, () =>
                {
                    for (int i = 0; i < N; i++)
                        if (capDealer[i] == null && g[i].State == NetBlackjack.Phase.HandOver && g[i].HandNumber == 1)
                        { capNet[i] = g[i].Net.ToArray(); capOut[i] = g[i].Outcomes.ToArray(); capDealer[i] = string.Join(",", g[i].DealerCards.Select(c => c.Index)); }
                    return capDealer.All(d => d != null);
                }, 60000);
                T.True(done, "every player stands, the dealer plays to 17, and the first hand completes on all nodes");

                T.True(capDealer.All(d => d == capDealer[0]), "all nodes agree on the dealer's hand");
                for (int s = 0; s < N; s++)
                {
                    long net0 = capNet[0][s];
                    T.True(capNet.All(c => c[s] == net0), $"all nodes agree on seat {s}'s net result ({net0})");
                    T.True(capOut.All(c => c[s] == capOut[0][s]), $"all nodes agree on seat {s}'s outcome");
                }
                T.True(!g.Any(x => x.CheatDetected), "no false cheat detection");
            }
            finally { foreach (var x in g) { try { x.Stop(); } catch { } } foreach (var n in nodes) { try { n.Dispose(); } catch { } } }
        });

        T.Run("the table runs CONTINUOUSLY: hand after hand is dealt automatically (no one ever asked it to stop)", () =>
        {
            const int N = 2;
            var (nodes, ids) = Mesh(N);
            NetBlackjack[] g = Array.Empty<NetBlackjack>();
            try
            {
                T.True(Until(() => nodes.All(n => n.PeerCount >= N - 1), 20000), "the 2 nodes form one mesh");
                const string table = "t-bjcont~p2~b10";
                g = ids.Select((id, i) => new NetBlackjack(nodes[i], table, id.Priv, id.Pub)).ToArray();
                foreach (var x in g) { x.HandPauseMs = 400; x.Start(); }   // short between-hand pause for fast tests (UI default is 10s)
                T.True(Until(() => g.All(x => x.MyHand.Count == 2), 40000), "the first hand is dealt");

                long buyIn = g[0].MyBankroll;   // the starting bankroll (buy-in)
                T.True(buyIn > 0, "each player starts with a buy-in bankroll");

                // keep standing; the table must reach at least a 4th hand entirely on its own (continuous re-deal)
                bool reached = DriveStand(g, () => g.All(x => x.HandNumber >= 4), 120000);
                T.True(reached, "the table dealt hand #2, #3, #4 … automatically, with no stop after the first");
                T.True(g.Select(x => string.Join(",", x.SeatPubs)).Distinct().Count() == 1, "the seating is stable across hands");
                T.True(!g.Any(x => x.CheatDetected), "no false cheat detection across many hands");
                // the bankroll moved (hands were actually wagered and settled), not frozen
                T.True(g.Any(x => x.MyBankroll != buyIn), "bankrolls change as hands are won/lost across the session");
            }
            finally { foreach (var x in g) { try { x.Stop(); } catch { } } foreach (var n in nodes) { try { n.Dispose(); } catch { } } }
        });

        T.Run("LEAVE the table: a player cashes out and is dealt out, the remaining players keep playing", () =>
        {
            const int N = 3;
            var (nodes, ids) = Mesh(N);
            NetBlackjack[] g = Array.Empty<NetBlackjack>();
            try
            {
                T.True(Until(() => nodes.All(n => n.PeerCount >= N - 1), 20000), "the 3 nodes form one mesh");
                const string table = "t-bjleave~p3~b10";
                g = ids.Select((id, i) => new NetBlackjack(nodes[i], table, id.Priv, id.Pub)).ToArray();
                foreach (var x in g) { x.HandPauseMs = 400; x.Start(); }   // short between-hand pause for fast tests (UI default is 10s)
                T.True(DriveStand(g, () => g.All(x => x.HandNumber >= 2 && x.MyHand.Count == 2), 60000), "the table is dealing (reached hand #2)");

                // pick the player NOT in the seat that is currently to act, so leaving is clean; any player works
                var leaver = g[0];
                long cashOut = leaver.MyBankroll;
                leaver.Leave();
                T.True(cashOut > 0, "the leaver has a bankroll to cash out");

                // the leaver ends their session (cashed out); the OTHER two players continue dealing further hands
                bool leaverDone = DriveStand(g, () => leaver.SessionOver, 60000);
                T.True(leaverDone, "the leaver is dealt out and the session ends for them (cashed out)");

                var stayers = g.Where(x => x != leaver).ToArray();
                int handAtLeave = stayers.Max(x => x.HandNumber);
                bool continued = DriveStand(stayers, () => stayers.All(x => x.HandNumber > handAtLeave && x.MySeat >= 0), 90000);
                T.True(continued, "the remaining players keep playing more hands after one player left");
                T.True(stayers.All(x => x.SeatPubs.Length == 2), "the table is now a 2-player game (the leaver was removed)");
                T.True(stayers.All(x => !x.CheatDetected), "no false cheat detection after the membership change");
            }
            finally { foreach (var x in g) { try { x.Stop(); } catch { } } foreach (var n in nodes) { try { n.Dispose(); } catch { } } }
        });

        T.Run("ON-CHAIN POT: players escrow real coins into an n-of-n, play, then co-sign the on-chain payout", () =>
        {
            const int N = 2;
            var (nodes, ids) = Mesh(N);
            NetBlackjack[] g = Array.Empty<NetBlackjack>();
            try
            {
                T.True(Until(() => nodes.All(n => n.PeerCount >= N - 1), 20000), "the 2 nodes form one mesh");
                const string table = "t-bjpot~p2~b10";

                // each player has a real wallet coin; FundPot escrows their stake into the n-of-n pot script.
                var wallets = ids.Select(_ => { var w = new OnChainWallet(WalletKeys.NewSeed()); w.Add(new OnChainWallet.Utxo("ee".PadRight(64, '7'), 0, 1_000_000, 0, 0)); return w; }).ToArray();
                var settlements = new byte[N][];
                g = ids.Select((id, i) => new NetBlackjack(nodes[i], table, id.Priv, id.Pub)).ToArray();
                for (int i = 0; i < N; i++)
                {
                    int idx = i; var w = wallets[i];
                    g[i].HandPauseMs = 400;
                    g[i].RecoveryLockHeight = 950_000;   // the app sets this to (tip + ~30 days); any future height for the test
                    g[i].FundPot = (script, value) => { try { var sp = w.SpendAction(script, value, 1); return new NetBlackjack.PotCoin(Chain.Txid(sp.Tx), 0, value, Convert.ToHexString(Chain.Serialize(sp.Tx)).ToLowerInvariant()); } catch { return null; } };
                    g[i].OnSettlementTx = raw => settlements[idx] = raw;
                }
                foreach (var x in g) x.Start();

                // FUNDING: every player escrows their stake before any card is dealt; play begins once the pot is funded
                T.True(Until(() => g.All(x => x.PotValue > 0 && x.MyHand.Count == 2), 40000), "the pot is funded on-chain and the first hand is dealt");
                long pot = g[0].PotValue;
                T.True(g.All(x => x.PotValue == pot), "every node agrees on the pot value");
                T.Eq(pot, g[0].MyBankroll * 2 * N, "the pot equals every player's full stake (buy-in + house share)");

                // GRIEFING SAFETY: before any card, every player holds the pre-signed nLockTime REFUND — a valid
                // n-of-n spend of every pot coin that returns each stake, broadcastable if settlement ever stalls.
                T.True(g.All(x => x.RecoveryRaw != null), "every player holds the pre-signed refund (no stake can be stranded)");
                var rec = Chain.Deserialize(g[0].RecoveryRaw!);
                for (int j = 0; j < rec.Ins.Count; j++)
                    T.True(Chain.VerifyMultisigNofN(rec, j, g[0].PotPubs, pot / rec.Ins.Count), $"refund input {j} is a valid n-of-n spend (all co-signed at funding)");
                T.True(rec.LockTime > 0 && rec.Ins.All(i => i.Sequence != 0xffffffff), "the refund is nLockTime-gated (non-final) — only valid after the timeout");
                T.Eq(rec.Outs.Sum(o => o.Value), pot - g[0].PotFee, "the refund returns the whole pot (minus fee) to the stakers");

                // play a couple of hands by standing, then one player leaves → the session settles on-chain
                T.True(DriveStand(g, () => g.All(x => x.HandNumber >= 2), 60000), "a couple of hands play");
                g[0].Leave();
                bool settled = DriveStand(g, () => g.All(x => x.PotSettled), 90000);
                T.True(settled, "leaving a real-token table co-signs the on-chain settlement and closes it");

                // the broadcast settlement is a VALID n-of-n spend of every pot coin, conserving the pot
                var raw = settlements.FirstOrDefault(s => s != null);
                T.True(raw != null, "a settlement tx was broadcast on-chain");
                var tx = Chain.Deserialize(raw!);
                var pubs = g[0].PotPubs;
                for (int j = 0; j < tx.Ins.Count; j++)
                    T.True(Chain.VerifyMultisigNofN(tx, j, pubs, pot / tx.Ins.Count), $"pot input {j} is a valid n-of-n spend (all players co-signed)");
                T.Eq(tx.Outs.Sum(o => o.Value), pot - g[0].PotFee, "the settlement pays out the whole pot (minus fee) to the players' standings");
            }
            finally { foreach (var x in g) { try { x.Stop(); } catch { } } foreach (var n in nodes) { try { n.Dispose(); } catch { } } }
        });

        T.Run("ON-CHAIN POT continuation: a player leaves a 3-handed table → the pot SPLITS, the other two play on", () =>
        {
            const int N = 3;
            var (nodes, ids) = Mesh(N);
            NetBlackjack[] g = Array.Empty<NetBlackjack>();
            try
            {
                T.True(Until(() => nodes.All(n => n.PeerCount >= N - 1), 20000), "the 3 nodes form one mesh");
                const string table = "t-bjsplit~p3~b10";
                var wallets = ids.Select(_ => { var w = new OnChainWallet(WalletKeys.NewSeed()); w.Add(new OnChainWallet.Utxo("ff".PadRight(64, '8'), 0, 5_000_000, 0, 0)); return w; }).ToArray();
                var splits = new List<byte[]>();
                g = ids.Select((id, i) => new NetBlackjack(nodes[i], table, id.Priv, id.Pub)).ToArray();
                for (int i = 0; i < N; i++)
                {
                    var w = wallets[i];
                    g[i].HandPauseMs = 400; g[i].RecoveryLockHeight = 950_000;
                    g[i].FundPot = (script, value) => { try { var sp = w.SpendAction(script, value, 1); return new NetBlackjack.PotCoin(Chain.Txid(sp.Tx), 0, value, Convert.ToHexString(Chain.Serialize(sp.Tx)).ToLowerInvariant()); } catch { return null; } };
                    g[i].OnSettlementTx = raw => { lock (splits) splits.Add(raw); };
                }
                foreach (var x in g) x.Start();
                T.True(Until(() => g.All(x => x.PotValue > 0 && x.MyHand.Count == 2), 40000), "the 3-player pot is funded and dealt");
                long pot0 = g[0].PotValue;
                T.True(DriveStand(g, () => g.All(x => x.HandNumber >= 2), 60000), "a hand plays");

                // seat 0's player leaves; with two left the pot SPLITS on-chain and the other two keep playing
                var leaver = g[0]; var stayers = new[] { g[1], g[2] };
                leaver.Leave();
                T.True(DriveStand(g, () => leaver.SessionOver, 90000), "the leaver is cashed out on-chain and done");

                bool cont = DriveStand(stayers, () => stayers.All(x => x.PotValue > 0 && x.PotValue < pot0 && x.SeatPubs.Length == 2 && !x.SessionOver), 90000);
                T.True(cont, "the remaining two continue on the smaller re-escrowed pot (a real 2-of-2)  [" + string.Join(" | ", stayers.Select(x => x.DebugState)) + "]");
                T.True(stayers.Select(x => x.PotValue).Distinct().Count() == 1, "the two agree on the new pot value");
                int hAt = stayers.Max(x => x.HandNumber);
                T.True(DriveStand(stayers, () => stayers.All(x => x.HandNumber > hAt), 90000), "more hands are dealt after the split");
                T.True(splits.Count > 0, "an on-chain split/settlement tx was broadcast (leaver paid + remainder re-escrowed)");
                T.True(!g.Any(x => x.CheatDetected), "no false cheat detection across the split");
            }
            finally { foreach (var x in g) { try { x.Stop(); } catch { } } foreach (var n in nodes) { try { n.Dispose(); } catch { } } }
        });

        T.Run("RED-TEAM: a stake paid to the WRONG script is rejected — the on-chain pot never readies (game still plays)", () =>
        {
            const int N = 2;
            var (nodes, ids) = Mesh(N);
            NetBlackjack[] g = Array.Empty<NetBlackjack>();
            try
            {
                T.True(Until(() => nodes.All(n => n.PeerCount >= N - 1), 20000), "the 2 nodes form one mesh");
                const string table = "t-bjevil~p2~b10";
                var wallets = ids.Select(_ => { var w = new OnChainWallet(WalletKeys.NewSeed()); w.Add(new OnChainWallet.Utxo("ee".PadRight(64, '9'), 0, 1_000_000, 0, 0)); return w; }).ToArray();
                g = ids.Select((id, i) => new NetBlackjack(nodes[i], table, id.Priv, id.Pub)).ToArray();
                // g[0] = HONEST (escrows into the real pot). g[1] = ATTACKER: pays the SAME value to their OWN address
                // (not the n-of-n pot) and announces it as a stake — trying to play without really escrowing.
                g[0].HandPauseMs = 400; g[0].RecoveryLockHeight = 950_000;
                g[0].FundPot = (script, value) => { var sp = wallets[0].SpendAction(script, value, 1); return new NetBlackjack.PotCoin(Chain.Txid(sp.Tx), 0, value, Convert.ToHexString(Chain.Serialize(sp.Tx)).ToLowerInvariant()); };
                g[1].HandPauseMs = 400; g[1].RecoveryLockHeight = 950_000;
                g[1].FundPot = (script, value) => { var wrong = Chain.P2pkhLockForPub(ids[1].Pub); var sp = wallets[1].SpendAction(wrong, value, 1); return new NetBlackjack.PotCoin(Chain.Txid(sp.Tx), 0, value, Convert.ToHexString(Chain.Serialize(sp.Tx)).ToLowerInvariant()); };
                foreach (var x in g) x.Start();

                // the game deals INSTANTLY (on-chain pot is background) but the fake stake is rejected, so the pot
                // never becomes ready — no on-chain payout can ride on a stake that was never really escrowed.
                T.True(Until(() => g[0].MyHand.Count == 2, 40000), "the game deals instantly — funding is background, it never blocks the cards");
                T.True(Until(() => g[0].Rejected > 0, 20000), "the honest node rejects the fake funding announcement");
                Thread.Sleep(1500);
                T.True(!g[0].PotReady, "the on-chain pot never becomes ready on a fake stake (the payout stays off the chain)");
            }
            finally { foreach (var x in g) { try { x.Stop(); } catch { } } foreach (var n in nodes) { try { n.Dispose(); } catch { } } }
        });

        T.Run("RED-TEAM: a DOUBLE-SPENT stake (a miner rejects it) is caught in the background — pot never readies", () =>
        {
            const int N = 2;
            var (nodes, ids) = Mesh(N);
            NetBlackjack[] g = Array.Empty<NetBlackjack>();
            try
            {
                T.True(Until(() => nodes.All(n => n.PeerCount >= N - 1), 20000), "the 2 nodes form one mesh");
                const string table = "t-bjds~p2~b10";
                var wallets = ids.Select(_ => { var w = new OnChainWallet(WalletKeys.NewSeed()); w.Add(new OnChainWallet.Utxo("dd".PadRight(64, '5'), 0, 1_000_000, 0, 0)); return w; }).ToArray();
                g = ids.Select((id, i) => new NetBlackjack(nodes[i], table, id.Priv, id.Pub)).ToArray();
                string myRaw0 = "";
                g[0].FundPot = (script, value) => { var sp = wallets[0].SpendAction(script, value, 1); myRaw0 = Convert.ToHexString(Chain.Serialize(sp.Tx)).ToLowerInvariant(); return new NetBlackjack.PotCoin(Chain.Txid(sp.Tx), 0, value, myRaw0); };
                // the honest node asks the "miner": it accepts ITS OWN stake, but the PEER's funding tx is reported as
                // NOT first-seen (a conflicting double-spend reached the miner first) — exactly the BSV check.
                g[0].VerifyFundedOnChain = raw => raw == myRaw0;
                g[1].FundPot = (script, value) => { var sp = wallets[1].SpendAction(script, value, 1); return new NetBlackjack.PotCoin(Chain.Txid(sp.Tx), 0, value, Convert.ToHexString(Chain.Serialize(sp.Tx)).ToLowerInvariant()); };
                g[1].VerifyFundedOnChain = _ => true;
                foreach (var x in g) x.Start();

                // the game deals INSTANTLY; the miner check runs in the background and flags the double-spend, which
                // blocks the on-chain payout (PotReady stays false) without ever freezing the cards.
                T.True(Until(() => g[0].MyHand.Count == 2, 40000), "the game deals instantly — the miner check is background");
                T.True(Until(() => g[0].CheatDetected, 30000), "the honest node catches the double-spent stake by asking the miner");
                Thread.Sleep(1500);
                T.True(!g[0].PotReady, "a double-spent stake keeps the on-chain pot from ever becoming ready (payout stays off-chain)");
            }
            finally { foreach (var x in g) { try { x.Stop(); } catch { } } foreach (var n in nodes) { try { n.Dispose(); } catch { } } }
        });

        T.Run("BUST logic: a player who busts is done immediately — they can never keep acting past 21", () =>
        {
            const int N = 2;
            var (nodes, ids) = Mesh(N);
            NetBlackjack[] g = Array.Empty<NetBlackjack>();
            try
            {
                T.True(Until(() => nodes.All(n => n.PeerCount >= N - 1), 20000), "the 2 nodes form one mesh");
                const string table = "t-bjbust~p2~b10";
                g = ids.Select((id, i) => new NetBlackjack(nodes[i], table, id.Priv, id.Pub)).ToArray();
                foreach (var x in g) { x.HandPauseMs = 400; x.Start(); }
                T.True(Until(() => g.All(x => x.MyHand.Count == 2), 40000), "the first hand is dealt");

                // AGGRESSIVE strategy: always HIT while under 21 (hit to 17+). This drives players into busts; the
                // engine must mark them done the instant they pass 21 and never let them act again. If the bust bug
                // were present the busted player would stay "to act" and this driver would hit forever → timeout.
                // Run several hands so a bust is virtually certain to occur.
                bool reached = Until(() =>
                {
                    foreach (var x in g)
                    {
                        if (x.State != NetBlackjack.Phase.Playing || x.ToAct != x.MySeat || x.AwaitingMyCard) continue;
                        // NEVER act after 21: if our own logic ever offered a turn at >21 that is the bug.
                        T.True(Blackjack.Value(x.MyHand).Total <= 21, $"a turn is never offered after busting (had {Blackjack.Value(x.MyHand).Total})");
                        if (Blackjack.Value(x.MyHand).Total < 17) x.Act(BjAction.Hit); else x.Act(BjAction.Stand);
                    }
                    return g.All(y => y.HandNumber >= 3);
                }, 120000);
                T.True(reached, "hands keep completing under an aggressive hit strategy (busts resolve and the turn advances)");
                T.True(!g.Any(x => x.CheatDetected), "no false cheat detection");
            }
            finally { foreach (var x in g) { try { x.Stop(); } catch { } } foreach (var n in nodes) { try { n.Dispose(); } catch { } } }
        });
    }
}
