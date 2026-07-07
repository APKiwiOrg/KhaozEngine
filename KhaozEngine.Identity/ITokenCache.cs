using System;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Identity;

/// <summary>A persisted sign-in session: the provider credential plus any minted session token, so a returning
/// player can skip an interactive sign-in.</summary>
public readonly record struct CachedSession(
    ProviderCredential Credential, string? SessionToken, DateTimeOffset? SessionTokenExpiresUtc,
    DateTimeOffset LastAuthenticatedUtc);

/// <summary>The persistence seam behind a <see cref="CachedSession"/>: load on startup, save after sign-in, clear
/// on sign-out. Implementations decide the storage medium (file, OS keychain, etc.).</summary>
public interface ITokenCache
{
    Task<CachedSession?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(CachedSession session, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}
