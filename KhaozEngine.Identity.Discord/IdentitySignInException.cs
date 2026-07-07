using System;

namespace KhaozEngine.Identity.Discord;

/// <summary>A recoverable sign-in failure the consumer surfaces as a retryable UI state: a failed browser launch,
/// a mismatched redirect <c>state</c>, or a token-endpoint error. Duplicated from KhaozEngine.Identity.Oidc's
/// exception of the same name so this package does not depend on the Oidc sibling package.</summary>
public sealed class IdentitySignInException : Exception
{
    public IdentitySignInException(string message) : base(message) { }

    public IdentitySignInException(string message, Exception innerException) : base(message, innerException) { }
}
