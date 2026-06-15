using System;
using KhaozEngine.Netcode;
using LiteNetLib;

namespace KhaozEngine.Netcode.LiteNetLib;

/// <summary>
/// Splits a batch so position/transient state (Sequenced) is never head-of-line blocked by reliable
/// events (ReliableOrdered). Before splitting, one reliable event forced the whole batch (positions
/// included) onto the reliable channel, so a single lost packet stalled every later position update
/// until retransmit.
/// </summary>
/// <remarks>
/// The split contract itself (<see cref="IChannelSplittable{TSelf}"/> and
/// <see cref="NetChannelReliability"/>) lives in the transport-free <c>KhaozEngine.Netcode</c>
/// package so batch DTOs can implement it without referencing LiteNetLib. Only the
/// <see cref="DeliveryMethod"/> mapping and send orchestration stay here.
/// </remarks>
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
