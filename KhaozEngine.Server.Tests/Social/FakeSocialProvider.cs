using System;
using System.Collections.Generic;
using KhaozEngine.Social;

namespace KhaozEngine.Tests;

/// <summary>
/// Records calls made to an <see cref="ISocialProvider"/> so the orchestration in
/// <see cref="SocialPresenceController"/> can be asserted without a live platform.
/// </summary>
internal sealed class FakeSocialProvider : ISocialProvider
{
    public List<string> InitializedWith { get; } = new();
    public List<RichPresence> PresenceCalls { get; } = new();
    public int ClearCalls { get; private set; }
    public int UpdateCalls { get; private set; }
    public int DisposeCalls { get; private set; }

    public bool InitializeResult { get; set; } = true;
    public bool ConnectedResult { get; set; } = true;
    public SocialUser? LocalUser { get; set; }

    /// <summary>When set, the next call to the named method throws to exercise session-disable.</summary>
    public bool ThrowOnSetPresence { get; set; }
    public bool ThrowOnUpdate { get; set; }

    public bool IsConnected => ConnectedResult;

    public bool TryInitialize(string applicationId)
    {
        InitializedWith.Add(applicationId);
        return InitializeResult;
    }

    public void Update()
    {
        UpdateCalls++;
        if (ThrowOnUpdate)
        {
            throw new InvalidOperationException("boom");
        }
    }

    public void SetPresence(in RichPresence presence)
    {
        if (ThrowOnSetPresence)
        {
            throw new InvalidOperationException("boom");
        }

        PresenceCalls.Add(presence);
    }

    public void ClearPresence() => ClearCalls++;

    public bool TryGetLocalUser(out SocialUser user)
    {
        if (LocalUser is { } u)
        {
            user = u;
            return true;
        }

        user = default;
        return false;
    }

    public event Action<string>? JoinRequested;
    public event Action<JoinRequest>? JoinRequestReceived;

    public void RaiseJoinRequested(string secret) => JoinRequested?.Invoke(secret);
    public void RaiseJoinRequestReceived(JoinRequest request) => JoinRequestReceived?.Invoke(request);

    public void Dispose() => DisposeCalls++;
}
