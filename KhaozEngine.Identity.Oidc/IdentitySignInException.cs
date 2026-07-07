using System;

namespace KhaozEngine.Identity.Oidc;

/// <summary>A recoverable sign-in failure the consumer surfaces as a retryable UI state: a failed browser launch,
/// a mismatched redirect <c>state</c>, or a token-endpoint error.</summary>
public sealed class IdentitySignInException : Exception
{
    public IdentitySignInException(string message) : base(message) { }

    public IdentitySignInException(string message, Exception innerException) : base(message, innerException) { }
}
