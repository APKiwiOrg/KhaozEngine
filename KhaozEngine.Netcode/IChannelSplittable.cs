namespace KhaozEngine.Netcode;

/// <summary>Reliability class for an entity-update sub-batch.</summary>
public enum NetChannelReliability
{
    /// <summary>Position/transient state: latest value wins, stale packets may be dropped.</summary>
    UnreliableSequenced,

    /// <summary>Events that must arrive in order: spawns, destroys, collects, hits, game state.</summary>
    ReliableOrdered
}

/// <summary>
/// A batch that can be split into a reliable and an unreliable sub-batch for channel-separated sending.
/// </summary>
/// <remarks>
/// Transport-free: this contract names no transport type, so a batch DTO in a transport-agnostic
/// project (e.g. one shared with a web server) can implement it without referencing any UDP library.
/// The LiteNetLib delivery-method mapping lives in <c>KhaozEngine.Netcode.LiteNetLib.ChannelSplitter</c>.
/// </remarks>
/// <typeparam name="TSelf">The implementing batch type (CRTP), so extraction stays strongly typed.</typeparam>
public interface IChannelSplittable<TSelf>
{
    /// <summary>True if the batch has any position/transient content to send unreliably.</summary>
    bool HasUnreliableContent { get; }

    /// <summary>True if the batch has any event content that must be sent reliably.</summary>
    bool HasReliableContent { get; }

    /// <summary>The position/transient-only sub-batch (reliable fields nulled/cleared).</summary>
    TSelf ExtractUnreliable();

    /// <summary>The events-only sub-batch (position fields nulled/cleared).</summary>
    TSelf ExtractReliable();
}
