using System;

namespace KhaozEngine.Identity.Oidc;

/// <summary>A recoverable sign-in failure the consumer surfaces as a retryable UI state: a failed browser launch,
/// a mismatched redirect <c>state</c>, or a token-endpoint error. Derives from
/// <see cref="KhaozEngine.Identity.SignInException"/>, the shared base in the core package, so cross-provider
/// code catches one type rather than one per backend. <see cref="SignInException.Reason"/> and the base's
/// identifiers name the rule and its subject.</summary>
public sealed class IdentitySignInException : SignInException
{
    public IdentitySignInException(string message) : base(message) { }

    public IdentitySignInException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates a failure refused by <paramref name="reason"/>.</summary>
    public IdentitySignInException(SignInRefusal reason, string message) : base(reason, message) { }

    /// <summary>Creates a failure refused by <paramref name="reason"/>, with the cause that triggered it.</summary>
    public IdentitySignInException(SignInRefusal reason, string message, Exception innerException)
        : base(reason, message, innerException) { }
}
