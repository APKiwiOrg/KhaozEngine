using System;

namespace KhaozEngine.Identity;

/// <summary>The shared base for every provider backend's sign-in failure: a recoverable failure the consumer
/// surfaces as a retryable UI state, such as a failed browser launch, a mismatched redirect <c>state</c>, or a
/// token-endpoint error. Each provider package keeps its own derived exception
/// (<c>KhaozEngine.Identity.Oidc.IdentitySignInException</c>,
/// <c>KhaozEngine.Identity.Discord.IdentitySignInException</c>), so code that cares which backend failed still
/// catches the provider type. Code that does not care, which is most of it once a game offers a choice of
/// sign-in providers, catches this one type instead of one per backend. The name deliberately differs from the
/// providers' own so that a file importing this namespace alongside a provider namespace, as every consumer's
/// sign-in code does, keeps resolving the unqualified provider name.
/// <para><see cref="Reason"/> names the rule an engine provider refused, and the identifiers name its subject.
/// The identifiers an engine refusal sets carry no token, authorization code, <c>state</c> value, client secret,
/// raw authority URL or response body. The message is developer prose, never a classification token.</para></summary>
public class SignInException : Exception
{
    /// <summary>Creates a failure with no reason, for a backend outside the engine. <paramref name="message"/> is
    /// also kept as <see cref="Detail"/>.</summary>
    public SignInException(string message) : this(null, message, null) { }

    /// <summary>Creates a failure with no reason and an inner cause, for a backend outside the engine.
    /// <paramref name="message"/> is also kept as <see cref="Detail"/>.</summary>
    public SignInException(string message, Exception innerException) : this(null, message, innerException) { }

    /// <summary>Creates a failure refused by <paramref name="reason"/>.</summary>
    /// <param name="reason">The rule that refused the sign-in.</param>
    /// <param name="message">The developer-facing explanation, not a classification token.</param>
    public SignInException(SignInRefusal reason, string message) : this((SignInRefusal?)reason, message, null) { }

    /// <summary>Creates a failure refused by <paramref name="reason"/>, with the cause that triggered it.</summary>
    /// <param name="reason">The rule that refused the sign-in.</param>
    /// <param name="message">The developer-facing explanation, not a classification token.</param>
    /// <param name="innerException">The exception that triggered the refusal.</param>
    public SignInException(SignInRefusal reason, string message, Exception innerException)
        : this((SignInRefusal?)reason, message, innerException) { }

    /// <summary>Initializes a provider's legacy or structured failure without calling a public legacy
    /// constructor. A null reason retains caller text in <see cref="Detail"/>.</summary>
    /// <param name="reason">The refused rule, or null for a consumer-built legacy failure.</param>
    /// <param name="message">The developer-facing explanation.</param>
    /// <param name="innerException">The cause, when available.</param>
    protected SignInException(SignInRefusal? reason, string message, Exception? innerException)
        : base(message, innerException)
    {
        Reason = reason;
        Detail = reason is null ? message : null;
    }

    /// <summary>The rule that refused the sign-in. Null only for an instance built through a message-only
    /// constructor by code outside the engine, such as another <see cref="IIdentityProvider"/>.</summary>
    public SignInRefusal? Reason { get; }

    /// <summary>Untrusted caller text from the message-only constructors. Null on engine refusals.</summary>
    public string? Detail { get; }

    /// <summary>The <see cref="IIdentityProvider.ProviderId"/> of the engine provider that refused, on every
    /// engine refusal.</summary>
    public string? ProviderId { get; init; }

    /// <summary>The sign-in timeout that expired, for <see cref="SignInRefusal.TimedOut"/>.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>The HTTP status the endpoint answered with, for <see cref="SignInRefusal.DiscoveryHttpFailure"/>
    /// and <see cref="SignInRefusal.TokenHttpFailure"/>.</summary>
    public int? StatusCode { get; init; }

    /// <summary>The endpoint that was being read, for <see cref="SignInRefusal.DiscoveryHttpFailure"/>,
    /// <see cref="SignInRefusal.TokenHttpFailure"/> and <see cref="SignInRefusal.EndpointJsonMalformed"/>.</summary>
    public SignInEndpointRole? EndpointRole { get; init; }

    /// <summary>The JSON member the rule names, for the discovery endpoint, token member and
    /// <c>expires_in</c> refusals. It is the member's name, never its value.</summary>
    public string? MemberName { get; init; }

    /// <summary>The authority's scheme, for <see cref="SignInRefusal.AuthorityNotHttps"/>. The rest of the
    /// authority is not carried.</summary>
    public string? ActualScheme { get; init; }
}
