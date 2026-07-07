using System;

namespace KhaozEngine.Identity.Oidc;

/// <summary>Configuration for a single generic OIDC provider (authority, client, and loopback/http knobs).</summary>
public sealed class OidcProviderOptions
{
    public required string Authority { get; init; }
    public required string ClientId { get; init; }
    public string Scopes { get; init; } = "openid profile email";
    public int LoopbackPort { get; init; }
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
