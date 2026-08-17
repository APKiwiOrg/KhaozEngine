using System;

namespace KhaozEngine.Social;

/// <summary>
/// No-op provider used when no social platform is available (headless servers, CI, tests, or a game
/// that did not add a backend). Silent, never connects, never throws. This is the default a
/// <see cref="SocialPresenceController"/> uses when no provider is supplied, and the controller
/// recognises it as "no backend, by choice": it goes straight to
/// <see cref="SocialPresenceState.Disabled"/> without ever arming its connect-retry backoff, so an
/// opted-out game pays nothing per frame.
/// </summary>
public sealed class NullSocialProvider : ISocialProvider
{
    public bool IsConnected => false;
    public bool TryInitialize(string applicationId) => false;
    public void Update() { }
    public void SetPresence(in RichPresence presence) { }
    public void ClearPresence() { }

    public bool TryGetLocalUser(out SocialUser user)
    {
        user = default;
        return false;
    }

    public event Action<string> JoinRequested { add { } remove { } }
    public event Action<JoinRequest> JoinRequestReceived { add { } remove { } }

    public void Dispose() { }
}
