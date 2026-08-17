namespace KhaozEngine.Social;

/// <summary>
/// The connection lifecycle of a <see cref="SocialPresenceController"/>. Read it to drive a status line
/// (a "connecting" or "not found" line, resolved through the game's localization catalog the way any other
/// player-facing text is), and subscribe to
/// <see cref="SocialPresenceController.StateChanged"/> to be told when it moves.
/// </summary>
public enum SocialPresenceState
{
    /// <summary>No connect attempt has been made yet. The first one runs on <c>Initialize()</c> or first use.</summary>
    Uninitialized,

    /// <summary>
    /// A connect attempt failed and another is scheduled on the backoff. Presence set while in this state
    /// is held and published once the connect lands.
    /// </summary>
    Connecting,

    /// <summary>Connected to the platform. Presence publishes.</summary>
    Connected,

    /// <summary>
    /// Every scheduled connect attempt failed, so the controller stopped asking: the platform client is
    /// genuinely not there. Nothing touches the provider again until <see cref="SocialPresenceController.Retry"/>.
    /// </summary>
    GivenUp,

    /// <summary>
    /// Terminal for the session. Either the provider failed mid-use (a transport death, which is not a
    /// cold-start race and is not retried), or there is no backend at all and social was never going to
    /// connect. The provider has been disposed.
    /// </summary>
    Disabled,

    /// <summary>The controller has been disposed. Every call is a no-op.</summary>
    Disposed,
}
