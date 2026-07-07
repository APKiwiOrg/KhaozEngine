using System;

namespace KhaozEngine.Identity.Discord;

/// <summary>Configuration for the Discord provider (client and loopback/http knobs). Discord's authorize and
/// token endpoints are fixed, so unlike <c>OidcProviderOptions</c> there is no authority to configure.</summary>
public sealed class DiscordProviderOptions
{
    public required string ClientId { get; init; }
    public string Scopes { get; init; } = "identify email";
    public int LoopbackPort { get; init; }
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
