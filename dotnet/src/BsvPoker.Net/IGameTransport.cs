namespace BsvPoker.Net;

/// <summary>
/// The transport a <see cref="NetGame"/> session runs over: subscribe to a table's messages and publish to it.
/// This is the ONLY coupling between the dealerless poker engine and how bytes move, so the SAME engine runs over
/// the live <see cref="P2PNode"/> peer network AND over an in-memory bus (for deterministic, socket-free testing).
/// Implementations deliver a publish to all subscribers of the table (including the publisher's own node, exactly
/// as the P2P node does) and asynchronously (never inline on the caller's thread).
/// </summary>
public interface IGameTransport
{
    /// <summary>Subscribe to a table's messages; returns an unsubscribe action.</summary>
    Action Subscribe(string tableId, Action<string> onEvent);

    /// <summary>Publish a message to a table; returns the number of recipients.</summary>
    Task<int> PublishAsync(string tableId, byte[] payload);
}
