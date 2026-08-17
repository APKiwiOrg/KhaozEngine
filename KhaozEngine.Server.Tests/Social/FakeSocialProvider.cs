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

    /// <summary>
    /// Throws out of <see cref="IsConnected"/>. The seam says a provider reports a drop by returning false
    /// here, but the controller reads it every frame, so a backend that throws instead must not reach the
    /// game loop either.
    /// </summary>
    public bool ThrowOnIsConnected { get; set; }

    /// <summary>
    /// How often <see cref="IsConnected"/> was read, which is the controller's per-frame drop probe: a live
    /// session is supposed to pay this one bool and nothing else.
    /// </summary>
    public int IsConnectedReads { get; private set; }

    /// <summary>Throws out of <see cref="TryInitialize"/>, as a backend with a missing native layer can.</summary>
    public bool ThrowOnInitialize { get; set; }

    /// <summary>
    /// The first N connect attempts return false, whatever <see cref="InitializeResult"/> says: the
    /// platform client that starts up a few seconds after the game does.
    /// </summary>
    public int FailInitializeCount { get; set; }

    /// <summary>
    /// Set <see cref="ConnectedResult"/> to false to play the player quitting Discord mid-session: the
    /// transport is gone, and the provider says so here rather than throwing.
    /// </summary>
    public bool IsConnected
    {
        get
        {
            IsConnectedReads++;
            if (ThrowOnIsConnected)
            {
                throw new InvalidOperationException("boom");
            }

            return ConnectedResult;
        }
    }

    public bool TryInitialize(string applicationId)
    {
        InitializedWith.Add(applicationId);
        if (ThrowOnInitialize)
        {
            throw new InvalidOperationException("boom");
        }

        return InitializedWith.Count > FailInitializeCount && InitializeResult;
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
