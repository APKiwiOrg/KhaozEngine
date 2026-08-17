using System;

namespace KhaozEngine.Social;

/// <summary>
/// A provider-neutral social/presence backend (Discord today, Steam/other tomorrow). Every method is
/// best-effort: a transport failure degrades to disconnected and never throws into the caller. Games
/// normally talk to <see cref="SocialPresenceController"/> rather than this directly.
/// </summary>
public interface ISocialProvider : IDisposable
{
    /// <summary>
    /// True once connected to the platform client and ready to publish presence.
    /// <para>
    /// This is also how a provider REPORTS A DROP, and the only way that gets a session back.
    /// <see cref="SocialPresenceController"/> polls it once per frame while connected and reads a false as
    /// "the transport died": it re-enters its connect backoff, keeps the provider, and calls
    /// <see cref="TryInitialize"/> again. So a provider whose connection dies mid-session must go false here
    /// and leave itself in a state a later <see cref="TryInitialize"/> can connect from, exactly as it must
    /// after a failed connect. Throwing instead is the OTHER answer and means something different: a throw
    /// is terminal for the session and disposes the provider, because a provider that threw is in a state
    /// the seam cannot promise anything about. A backend that can tell a plain disconnect from a genuine
    /// failure should route the disconnect here rather than throw.
    /// </para>
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Connect for the given platform application/client id. Returns false on any failure.
    /// <para>
    /// Must be RE-ATTEMPTABLE on the same instance: <see cref="SocialPresenceController"/> retries a
    /// failed connect on a backoff rather than throwing the provider away, because the usual reason for
    /// one is that the platform client has not finished starting. A provider that failed here therefore
    /// has to leave itself in a state a later call can connect from, and must drop anything a
    /// half-finished attempt left behind rather than carry it into the next one.
    /// </para>
    /// </summary>
    bool TryInitialize(string applicationId);

    /// <summary>Pump platform callbacks. Call once per frame on the main thread.</summary>
    void Update();

    /// <summary>Publish the local player's rich presence.</summary>
    void SetPresence(in RichPresence presence);

    /// <summary>Clear any published presence.</summary>
    void ClearPresence();

    /// <summary>The local platform identity, once connected. Returns false when unknown.</summary>
    bool TryGetLocalUser(out SocialUser user);

    /// <summary>Raised when a friend activates "Join Game"; carries the game-encoded join secret.</summary>
    event Action<string> JoinRequested;

    /// <summary>Raised when another user asks to join; the game accepts or rejects the request.</summary>
    event Action<JoinRequest> JoinRequestReceived;
}
