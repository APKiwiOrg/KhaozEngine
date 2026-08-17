using System;

namespace KhaozEngine.Social;

/// <summary>
/// A provider-neutral social/presence backend (Discord today, Steam/other tomorrow). Every method is
/// best-effort: a transport failure degrades to disconnected and never throws into the caller. Games
/// normally talk to <see cref="SocialPresenceController"/> rather than this directly.
/// </summary>
public interface ISocialProvider : IDisposable
{
    /// <summary>True once connected to the platform client and ready to publish presence.</summary>
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
