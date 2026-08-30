using System;

namespace KhaozEngine.Identity.Discord;

/// <summary>A recoverable sign-in failure the consumer surfaces as a retryable UI state: a failed browser launch,
/// a mismatched redirect <c>state</c>, or a token-endpoint error. Derives from
/// <see cref="KhaozEngine.Identity.SignInException"/>, the shared base in the core package, so cross-provider
/// code catches one type rather than one per backend. The type stays here, under this package's own namespace,
/// so this package does not depend on the Oidc sibling package.</summary>
public sealed class IdentitySignInException : SignInException
{
    public IdentitySignInException(string message) : base(message) { }

    public IdentitySignInException(string message, Exception innerException) : base(message, innerException) { }
}
