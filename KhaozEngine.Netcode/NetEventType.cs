namespace KhaozEngine.Netcode;

/// <summary>The kind of a <see cref="NetEvent"/> drained from an <see cref="INetTransport"/>.</summary>
public enum NetEventType
{
    /// <summary>A peer connected; <see cref="NetEvent.Connection"/> identifies it.</summary>
    Connected,

    /// <summary>A peer disconnected; <see cref="NetEvent.Connection"/> identifies it.</summary>
    Disconnected,

    /// <summary>Data arrived; <see cref="NetEvent.Data"/> holds the payload, <see cref="NetEvent.Reliability"/> its channel.</summary>
    Data
}
