using System;
using KhaozEngine.Social;
using KhaozEngine.Social.Discord.Internal;

namespace KhaozEngine.Social.Discord;

/// <summary>
/// Discord Rich Presence <see cref="ISocialProvider"/>: a pure-managed IPC client (no native libs, no
/// third-party packages). Rich presence, local identity, and join/invite. Every operation is
/// best-effort - a Discord failure degrades to disconnected and never throws into the game.
/// </summary>
public sealed class DiscordSocialProvider : ISocialProvider
{
    private readonly string optionsAppId;
    private readonly DiscordIpcClient client;
    private string applicationId = string.Empty;

    /// <summary>Production ctor: real named-pipe / unix-socket transport.</summary>
    public DiscordSocialProvider(DiscordSocialOptions? options = null)
        : this(new NamedPipeDiscordTransport(), options?.ApplicationId ?? string.Empty)
    {
    }

    /// <summary>Test/custom-transport ctor.</summary>
    internal DiscordSocialProvider(IDiscordIpcTransport transport, string optionsAppId = "")
    {
        this.optionsAppId = optionsAppId;
        client = new DiscordIpcClient(transport);
        client.JoinSecretReceived += OnJoinSecret;
        client.JoinRequestUserReceived += OnJoinRequestUser;
    }

    public bool IsConnected => client.IsConnected;

    public event Action<string>? JoinRequested;
    public event Action<JoinRequest>? JoinRequestReceived;

    public bool TryInitialize(string applicationId)
    {
        this.applicationId = string.IsNullOrEmpty(applicationId) ? optionsAppId : applicationId;
        if (string.IsNullOrEmpty(this.applicationId))
        {
            return false;
        }

        return client.TryConnect(this.applicationId);
    }

    public void Update() => client.Pump();

    public void SetPresence(in RichPresence presence) => client.SetActivity(presence);

    public void ClearPresence() => client.ClearActivity();

    public bool TryGetLocalUser(out SocialUser user)
    {
        if (client.LocalUser is { } u)
        {
            user = u;
            return true;
        }

        user = default;
        return false;
    }

    private void OnJoinSecret(string secret) => JoinRequested?.Invoke(secret);

    private void OnJoinRequestUser(SocialUser requester)
    {
        // Answer over IPC: accept sends SEND_ACTIVITY_JOIN_INVITE, reject sends CLOSE_ACTIVITY_REQUEST,
        // both naming this requester. The id is captured rather than the SocialUser so the delegate holds
        // only what the reply needs, and the game may hold the request across a disconnect (or past
        // Dispose), where responding is a silent no-op rather than a throw.
        string requesterId = requester.Id;
        JoinRequestReceived?.Invoke(
            new JoinRequest(requester, accept => client.RespondToJoinRequest(requesterId, accept)));
    }

    public void Dispose()
    {
        client.JoinSecretReceived -= OnJoinSecret;
        client.JoinRequestUserReceived -= OnJoinRequestUser;
        client.Dispose();
    }
}
