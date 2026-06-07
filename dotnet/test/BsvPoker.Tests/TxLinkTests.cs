using BsvPoker.Core;
using BsvPoker.Core.Games;
using BsvPoker.Crypto;
using BsvPoker.Net.Bsv;

namespace BsvPoker.Tests;

/// <summary>
/// The ONLY player-to-player transport carries nothing but Bitcoin transactions. This proves a player can
/// push a Bitcoin tx IP-to-IP straight to another player (the Bitcoin-wire tx message), and the peer parses
/// it — exactly the mandated model (the same tx is also broadcast to miners for on-chain storage).
/// </summary>
public static class TxLinkTests
{
    private static Card C(string s)
    {
        int rank = s[0] switch { 'A' => 14, 'K' => 13, 'Q' => 12, 'J' => 11, 'T' => 10, _ => s[0] - '0' };
        var suit = s[1] switch { 's' => Suit.Spades, 'h' => Suit.Hearts, 'd' => Suit.Diamonds, _ => Suit.Clubs };
        return new Card(rank, suit);
    }

    public static void All()
    {
        Console.WriteLine("IP-to-IP transport (every packet is a Bitcoin transaction):");

        T.Run("a transaction is delivered IP-to-IP as a Bitcoin tx message and parsed by the peer", () =>
        {
            var net = NetworkParams.For(BsvNetwork.Regtest);
            using var link = new TxLink(net, 0);
            Chain.Tx? received = null;
            using var ev = new ManualResetEventSlim();
            link.OnTransaction += tx => { received = tx; ev.Set(); };
            link.Start();

            var k = Secp256k1.GenerateKeyPair();
            var tx = new Chain.Tx(2,
                new() { new("ab".PadRight(64, '1'), 0, Array.Empty<byte>(), 0xffffffff) },
                new() { new(1000, Chain.P2pkhLockForPub(k.Pub)) }, 0);
            var raw = Chain.Serialize(tx);

            var ok = TxLink.SendTxAsync(net, "127.0.0.1", link.Port, raw).GetAwaiter().GetResult();
            T.True(ok, "send completed over the Bitcoin wire");
            T.True(ev.Wait(TimeSpan.FromSeconds(5)), "the peer received the pushed transaction");
            T.Eq(Chain.Txid(received!), Chain.Txid(tx), "the received tx is byte-identical (txid matches)");
        });

        T.Run("a chat message delivered IP-to-IP decrypts for the recipient", () =>
        {
            var net = NetworkParams.For(BsvNetwork.Regtest);
            using var link = new TxLink(net, 0);
            var bob = Secp256k1.GenerateKeyPair();
            var alice = Secp256k1.GenerateKeyPair();
            OnChainChat.Incoming? got = null;
            using var ev = new ManualResetEventSlim();
            link.OnTransaction += tx => { got = OnChainChat.TryReadTx(tx, bob.Priv, bob.Pub); if (got != null) ev.Set(); };
            link.Start();

            // Alice funds a chat tx and pushes it straight to Bob's IP (and would also send it to miners)
            var w = new OnChainWallet(WalletKeys.NewSeed());
            w.Add(new OnChainWallet.Utxo("cd".PadRight(64, '2'), 0, 1_000_000, 0, 0));
            var spend = w.SpendAction(OnChainChat.BuildScript(bob.Pub, alice.Pub, "nice hand"), 1000, 500);
            TxLink.SendTxAsync(net, "127.0.0.1", link.Port, Chain.Serialize(spend.Tx)).GetAwaiter().GetResult();

            T.True(ev.Wait(TimeSpan.FromSeconds(5)), "Bob received the chat tx IP-to-IP");
            T.Eq(got!.Text, "nice hand", "and decrypted it");
        });

        T.Run("a WHOLE on-chain hand is delivered to the opponent IP-to-IP, every step a Bitcoin tx", () =>
        {
            // Alice drives the hand; she pushes EACH hand transaction straight to Bob's node (and would also
            // send each to miners). Bob receives every step as a Bitcoin transaction and validates it.
            var net = NetworkParams.For(BsvNetwork.Regtest);
            using var bob = new TxLink(net, 0);
            var received = new List<Chain.Tx>();
            using var done = new ManualResetEventSlim();
            var expected = 0;
            bob.OnTransaction += tx => { lock (received) { received.Add(tx); if (received.Count >= expected && expected > 0) done.Set(); } };
            bob.Start();

            var seed = WalletKeys.NewSeed();
            var w = new OnChainWallet(seed);
            w.Add(new OnChainWallet.Utxo("ef".PadRight(64, '3'), 0, 100_000_000, 0, 0));
            var a = WalletKeys.Account(seed, 2, 0); var bk = WalletKeys.Account(seed, 2, 1);
            var deck = new[] { C("As"), C("Ah"), C("2c"), C("3d"), C("Ad"), C("Kh"), C("Qs"), C("Jc"), C("9h") };
            var tape = OnChainHandTape.BuildHoldem(w, (a.Priv, a.Pub), (bk.Priv, bk.Pub), deck, 40000, new byte[16]);
            expected = tape.Steps.Count;

            foreach (var step in tape.Steps)
                TxLink.SendTxAsync(net, "127.0.0.1", bob.Port, Chain.Serialize(step.Tx)).GetAwaiter().GetResult();

            T.True(done.Wait(TimeSpan.FromSeconds(15)), $"Bob received all {expected} hand transactions IP-to-IP");
            // Bob can read every typed step and verify the final settlement is a valid 2-of-2 spend
            var settle = received.FirstOrDefault(t => Chain.Txid(t) == Chain.Txid(tape.Settlement));
            T.True(settle != null, "the settlement arrived as a transaction");
            T.True(Chain.VerifyMultisig2of2(settle!, 0, a.Pub, bk.Pub, 40000), "and is a valid co-signed settlement");
        });

        T.Run("two DISTINCT players co-sign a 2-of-2 settlement by exchanging transactions IP-to-IP", () =>
        {
            // real two-party poker money: Alice and Bob (separate keys) settle a shared pot. Both compute the
            // same settlement deterministically; Alice sends her signature to Bob as an ENCRYPTED on-chain
            // message (a Bitcoin tx) over TxLink; Bob combines both signatures into the valid 2-of-2 spend.
            var net = NetworkParams.For(BsvNetwork.Regtest);
            var alice = Secp256k1.GenerateKeyPair();
            var bob = Secp256k1.GenerateKeyPair();
            var escrowTxid = "aa".PadRight(64, '1'); long pot = 50000, fee = 1000;
            var settlement = Chain.BuildCooperativeSettlement(escrowTxid, 0, pot, alice.Pub, fee); // both derive this
            var sigA = Chain.SignMultisig(settlement, 0, alice.Pub, bob.Pub, pot, alice.Priv);

            using var bobLink = new TxLink(net, 0);
            byte[]? sigFromAlice = null;
            using var ev = new ManualResetEventSlim();
            bobLink.OnTransaction += tx => { var m = OnChainChat.TryReadTx(tx, bob.Priv, bob.Pub); if (m != null) { sigFromAlice = Convert.FromHexString(m.Text); ev.Set(); } };
            bobLink.Start();

            // Alice funds + pushes the signature-bearing transaction straight to Bob
            var w = new OnChainWallet(WalletKeys.NewSeed());
            w.Add(new OnChainWallet.Utxo("bb".PadRight(64, '2'), 0, 1_000_000, 0, 0));
            var carrier = w.SpendAction(OnChainChat.BuildScript(bob.Pub, alice.Pub, Convert.ToHexString(sigA)), 1000, 500);
            TxLink.SendTxAsync(net, "127.0.0.1", bobLink.Port, Chain.Serialize(carrier.Tx)).GetAwaiter().GetResult();
            T.True(ev.Wait(TimeSpan.FromSeconds(5)), "Bob received Alice's signature as a transaction");

            var sigB = Chain.SignMultisig(settlement, 0, alice.Pub, bob.Pub, pot, bob.Priv);
            var final = Chain.ApplyMultisigScriptSig(settlement, 0, sigFromAlice!, sigB);
            T.True(Chain.VerifyMultisig2of2(final, 0, alice.Pub, bob.Pub, pot), "the two-party co-signed settlement is consensus-valid");
        });
    }
}
