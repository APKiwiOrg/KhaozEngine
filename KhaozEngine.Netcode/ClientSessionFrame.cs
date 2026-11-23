using System;

namespace KhaozEngine.Netcode;

/// <summary>One queued session event or a dedicated transport frame held for synchronous dispatch.</summary>
internal readonly struct ClientSessionFrame(ClientSessionEvent sessionEvent, byte[]? frame = null)
{
    internal ClientSessionEventKind Kind => sessionEvent.Kind;
    internal string RejectReason => sessionEvent.RejectReason;
    internal NetChannelReliability Reliability => sessionEvent.Reliability;
    internal ReadOnlyMemory<byte> Payload => frame is null ? sessionEvent.Data : frame.AsMemory(1);

    internal static ClientSessionFrame Borrow(byte[] frame, NetChannelReliability reliability) =>
        new(ClientSessionEvent.FromData(Array.Empty<byte>(), reliability), frame);

    internal ClientSessionEvent ToOwnedEvent() =>
        frame is null ? sessionEvent : ClientSessionEvent.FromData(SessionFrame.ReadBody(frame), Reliability);
}
