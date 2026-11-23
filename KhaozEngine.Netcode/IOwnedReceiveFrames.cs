namespace KhaozEngine.Netcode;

/// <summary>
/// A transport whose data events each carry a dedicated receive array. Once enqueued, the transport
/// never modifies or recycles that array, so the session can keep it through the receive drain.
/// </summary>
internal interface IOwnedReceiveFrames;
