using System;

namespace KhaozEngine.Social;

/// <summary>
/// An inbound "ask to join" from another user. The game calls <see cref="Accept"/> or
/// <see cref="Reject"/> exactly once; both are best-effort and never throw.
/// </summary>
public sealed class JoinRequest
{
    private readonly Action<bool>? respond;
    private bool answered;

    public JoinRequest(SocialUser user, Action<bool>? respond)
    {
        User = user;
        this.respond = respond;
    }

    /// <summary>The user asking to join.</summary>
    public SocialUser User { get; }

    /// <summary>Approve the request (idempotent; only the first call has effect).</summary>
    public void Accept() => Answer(true);

    /// <summary>Decline the request (idempotent; only the first call has effect).</summary>
    public void Reject() => Answer(false);

    private void Answer(bool accept)
    {
        if (answered)
        {
            return;
        }

        answered = true;
        respond?.Invoke(accept);
    }
}
