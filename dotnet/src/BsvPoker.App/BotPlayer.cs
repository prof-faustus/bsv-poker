using System.Windows.Threading;
using BsvPoker.Core;
using BsvPoker.Crypto;
using BsvPoker.Net;

namespace BsvPoker.App;

/// <summary>
/// A SEPARATE automated player — its own identity, its own P2P node, its own seat — that joins the same
/// table as the human and plays by itself. It is NOT the human window and NOT a second copy of the app:
/// it is a distinct, in-process automated participant connected over the local peer link. The human may
/// take control (act for it) but it remains a bot. A bot never launches a bot.
/// </summary>
public sealed class BotPlayer : IDisposable
{
    private readonly P2PNode _node;
    private readonly NetGame _net;
    private readonly DispatcherTimer _timer;
    public (byte[] Priv, byte[] Pub) Id { get; }
    public bool HumanControl { get; set; }
    public NetGame Net => _net;

    public BotPlayer(int mainNodePort, string tableId)
    {
        Id = Secp256k1.GenerateKeyPair();
        _node = new P2PNode(0, "127.0.0.1");
        _node.SetIdentity(Id.Priv, Id.Pub);
        _node.StartAsync(new[] { new P2PNode.PeerAddr("127.0.0.1", mainNodePort) }).Wait();
        _net = new NetGame(_node, tableId, Id.Priv, Id.Pub);
        _net.Start();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _timer.Tick += (_, _) => AutoAct();
        _timer.Start();
    }

    // act automatically on the bot's turn — unless the human has taken the wheel
    private void AutoAct()
    {
        if (HumanControl) return;
        var h = _net.Hand;
        if (h == null || h.Complete || _net.MySeat < 0 || h.ToAct != _net.MySeat) return;
        try { var a = BotPolicy.Decide(h); _net.Act(a.Kind, a.Amount); } catch { }
    }

    public long Stack => _net.Hand?.Seats.ElementAtOrDefault(_net.MySeat < 0 ? 0 : _net.MySeat)?.Stack ?? 0;
    public string Status => _net.Status;

    public void Dispose() { try { _timer.Stop(); } catch { } try { _net.Stop(); } catch { } try { _node.Dispose(); } catch { } }
}
