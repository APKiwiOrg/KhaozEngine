namespace KhaozEngine.Social.Discord;

/// <summary>Configuration for <see cref="DiscordSocialProvider"/>.</summary>
public sealed class DiscordSocialOptions
{
    /// <summary>The game's Discord Application (client) id. Required; presence is a no-op without it.</summary>
    public string ApplicationId { get; init; } = string.Empty;
}
