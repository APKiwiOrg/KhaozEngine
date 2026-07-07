using System;

namespace KhaozEngine.Identity;

/// <summary>The client-side result of a provider sign-in: the token the server will verify, plus refresh state.</summary>
public readonly record struct ProviderCredential(
    string ProviderId, string CredentialToken, string? RefreshToken, DateTimeOffset ExpiresAtUtc);
