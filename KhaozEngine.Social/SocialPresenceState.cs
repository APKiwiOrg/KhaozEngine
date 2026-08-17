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
    /// Terminal for the session. Either a provider call THREW mid-use (a provider that throws is in an
    /// unknown state, so the seam cannot promise it can be connected again), or there is no backend at all
    /// and social was never going to connect. The provider has been disposed. A connection that merely
    /// DROPPED is not this: that is <see cref="Reconnecting"/>.
    /// </summary>
    Disabled,

    /// <summary>The controller has been disposed. Every call is a no-op.</summary>
    Disposed,

    /// <summary>
    /// A live connection dropped (the provider went <see cref="ISocialProvider.IsConnected"/> false, the way
    /// a player quitting Discord mid-session leaves it) and the controller is working its way back on the
    /// same backoff a cold start uses. Presence set while in this state is held, and the presence that was
    /// live at the drop is republished when the reconnect lands. It differs from <see cref="Connecting"/>
    /// only in what a game can SAY about it ("lost Discord, reconnecting" rather than "connecting"):
    /// everything inside the controller treats the two identically.
    /// <para>
    /// It sits at the end rather than beside <see cref="Connecting"/> on purpose. Appending is what keeps
    /// every existing member's numeric value where it was, which an inserted member would have moved.
    /// </para>
    /// </summary>
    Reconnecting,
}
