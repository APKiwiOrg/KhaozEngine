using System;

namespace KhaozEngine.Identity.Discord;

/// <summary>Configuration for the Discord provider (client and loopback/http knobs). Discord's authorize and
/// token endpoints are fixed, so unlike <c>OidcProviderOptions</c> there is no authority to configure.</summary>
public sealed class DiscordProviderOptions
{
    public required string ClientId { get; init; }
    public string Scopes { get; init; } = "identify email";
    public int LoopbackPort { get; init; }
    /// <summary>Bounds the token-exchange HTTP calls only. It does nothing for the open-ended wait on the
    /// browser redirect: that is <see cref="SignInTimeout"/>.</summary>
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Deadline for the WHOLE interactive sign-in, the open-ended wait on the loopback redirect
    /// included. Without it a player who opens the browser and never finishes (closes the tab, the browser
    /// crashes, they walk away) leaves the await pending forever, so the <c>using</c> on the loopback listener
    /// never runs and the bound port stays open for the life of the process. On expiry
    /// <c>SignInAsync</c> throws <c>IdentitySignInException</c> and the listener is disposed on the way out.
    /// Default five minutes, which is generous for a human completing a browser flow. Zero or negative (so
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> too) restores the unbounded wait, leaving the
    /// deadline entirely to the caller's own cancellation token.</summary>
    public TimeSpan SignInTimeout { get; init; } = TimeSpan.FromMinutes(5);
}
