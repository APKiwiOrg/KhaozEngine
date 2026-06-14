using System;
using LiteNetLib;

namespace KhaozEngine.Netcode.LiteNetLib;

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

/// <summary>
/// Splits a batch so position/transient state (Sequenced) is never head-of-line blocked by reliable
/// events (ReliableOrdered). Before splitting, one reliable event forced the whole batch (positions
/// included) onto the reliable channel, so a single lost packet stalled every later position update
/// until retransmit.
/// </summary>
public static class ChannelSplitter
{
    /// <summary>Maps a reliability class to LiteNetLib's delivery method.</summary>
    public static DeliveryMethod ToDeliveryMethod(NetChannelReliability reliability) => reliability switch
    {
        NetChannelReliability.UnreliableSequenced => DeliveryMethod.Sequenced,
        NetChannelReliability.ReliableOrdered => DeliveryMethod.ReliableOrdered,
        _ => throw new ArgumentOutOfRangeException(nameof(reliability), reliability, "Unknown reliability.")
    };

    /// <summary>
    /// Invokes <paramref name="send"/> once per non-empty sub-batch with the matching delivery method:
    /// the unreliable part on Sequenced, the reliable part on ReliableOrdered. Empty parts are skipped.
    /// </summary>
    public static void Send<T>(T batch, Action<T, DeliveryMethod> send) where T : IChannelSplittable<T>
    {
        ArgumentNullException.ThrowIfNull(send);

        if (batch.HasUnreliableContent)
        {
            send(batch.ExtractUnreliable(), ToDeliveryMethod(NetChannelReliability.UnreliableSequenced));
        }

        if (batch.HasReliableContent)
        {
            send(batch.ExtractReliable(), ToDeliveryMethod(NetChannelReliability.ReliableOrdered));
        }
    }
}
