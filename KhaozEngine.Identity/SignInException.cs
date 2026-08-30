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
/// sign-in code does, keeps resolving the unqualified provider name.</summary>
public class SignInException : Exception
{
    public SignInException(string message) : base(message) { }

    public SignInException(string message, Exception innerException) : base(message, innerException) { }
}
