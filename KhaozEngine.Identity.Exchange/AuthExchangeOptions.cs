using System;
using KhaozEngine.Accounts;

namespace KhaozEngine.Identity.Exchange;

/// <summary>
/// The knobs of one <see cref="AuthExchange"/>. <see cref="AuthExchange"/> validates them once, at construction.
/// </summary>
public sealed class AuthExchangeOptions
{
    /// <summary>
    /// The longest <see cref="ProviderTimeout"/> accepted, the longest delay a cancellation timer takes. A deadline
    /// is the point of the option, so an infinite one is refused rather than honoured.
    /// </summary>
    public static readonly TimeSpan MaxProviderTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>
    /// The longest <see cref="TokenLifetime"/> accepted: one year. A bearer token that outlives a year outlives any
    /// rotation plan, and an unbounded value would overflow the expiry the exchange computes from the clock, so a
    /// typo in configuration fails at construction instead of inside every request.
    /// </summary>
    public static readonly TimeSpan MaxTokenLifetime = TimeSpan.FromDays(365);

    /// <summary>
    /// How long a minted token verifies for. Required, positive, and at most <see cref="MaxTokenLifetime"/>. Both
    /// games use seven days.
    /// </summary>
    public required TimeSpan TokenLifetime { get; init; }

    /// <summary>
    /// Whether an unwhitelisted account is refused. <c>false</c> is the policy of an open game, and of a local
    /// development host bound to a throwaway store. A ban still refuses either way.
    /// </summary>
    public bool RequireWhitelist { get; init; } = true;

    /// <summary>The longest credential accepted, in UTF-16 code units, checked before any provider call.</summary>
    public int MaxCredentialChars { get; init; } = 4096;

    /// <summary>
    /// The longest display name handed to the store, in UTF-16 code units. A longer one is clamped, never refused,
    /// and never split inside a surrogate pair. At most <see cref="AccountStoreRules.MaxDisplayNameChars"/>, which
    /// the engine stores refuse to exceed.
    /// </summary>
    public int MaxDisplayNameChars { get; init; } = 64;

    /// <summary>
    /// How long the validator may take. Past it the exchange answers <see cref="AuthExchangeOutcome.Unavailable"/>
    /// without waiting further, even for a validator that ignores its cancellation token, so a stalled provider
    /// cannot park a request for the HTTP client's default 100 seconds.
    /// </summary>
    public TimeSpan ProviderTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The clock the ban expiry, the token expiry and the provider deadline are read against. A test hands in a
    /// fake, and production leaves the system clock.
    /// </summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    internal void Validate()
    {
        if (TokenLifetime <= TimeSpan.Zero || TokenLifetime > MaxTokenLifetime)
            throw new ArgumentOutOfRangeException(nameof(TokenLifetime),
                $"The token lifetime must be positive and at most {MaxTokenLifetime.TotalDays} days.");
        if (MaxCredentialChars <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxCredentialChars), "The credential cap must be positive.");
        if (MaxDisplayNameChars <= 0 || MaxDisplayNameChars > AccountStoreRules.MaxDisplayNameChars)
            throw new ArgumentOutOfRangeException(nameof(MaxDisplayNameChars),
                $"The display name cap must be between 1 and {AccountStoreRules.MaxDisplayNameChars}, the most an " +
                "engine account store holds.");
        if (ProviderTimeout <= TimeSpan.Zero || ProviderTimeout > MaxProviderTimeout)
            throw new ArgumentOutOfRangeException(nameof(ProviderTimeout),
                "The provider deadline must be positive and finite.");
        if (Clock is null)
            throw new ArgumentNullException(nameof(Clock), "The exchange needs a clock.");
    }
}
