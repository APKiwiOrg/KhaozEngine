using System;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Identity;

/// <summary>Drives launch/sign-in identity state, including offline-grace on a cached session.
///
/// State machine on <see cref="RestoreAsync"/> (from whatever is in the <see cref="ITokenCache"/>):
/// <list type="bullet">
/// <item>no cached session -> <see cref="IdentityStatus.RequiresSignIn"/></item>
/// <item>a session token that has not expired -> <see cref="IdentityStatus.SignedIn"/></item>
/// <item>an expired session token, but still within <see cref="IdentitySessionOptions.OfflineGraceWindow"/> of the
/// cached <c>LastAuthenticatedUtc</c> -> <see cref="IdentityStatus.OfflineGrace"/> (play continues offline)</item>
/// <item>an expired session token beyond the grace window -> <see cref="IdentityStatus.RequiresSignIn"/></item>
/// </list>
///
/// <see cref="IdentityState.Subject"/> is always the server-verified subject from the <c>/auth/exchange</c>
/// result, persisted on <see cref="CachedSession.Subject"/>. It is not known until that exchange completes, so
/// <see cref="SignInAsync"/> leaves it null; the consumer performs the exchange out-of-band (this package stays
/// HTTP-free) and calls <see cref="AttachSessionTokenAsync"/> with the verified subject to reach
/// <see cref="IdentityStatus.SignedIn"/>.</summary>
public sealed class IdentitySession
{
    private readonly IIdentityProvider provider;
    private readonly ITokenCache cache;
    private readonly IdentitySessionOptions options;
    private readonly Func<DateTimeOffset> clock;

    public IdentitySession(IIdentityProvider provider, ITokenCache cache, IdentitySessionOptions options,
        Func<DateTimeOffset>? clock = null)
    {
        this.provider = provider;
        this.cache = cache;
        this.options = options;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public IdentityState Current { get; private set; } = new(IdentityStatus.RequiresSignIn, null, null, null, null);

    /// <summary>Loads the cached session (if any) and computes the launch state per the state machine above.</summary>
    public async Task<IdentityState> RestoreAsync(CancellationToken ct)
    {
        CachedSession? cached = await cache.LoadAsync(ct).ConfigureAwait(false);
        if (cached is not CachedSession s) return Set(new IdentityState(IdentityStatus.RequiresSignIn, null, null, null, null));

        DateTimeOffset now = clock();
        bool tokenValid = s.SessionToken is not null && s.SessionTokenExpiresUtc is DateTimeOffset expiresUtc && now < expiresUtc;
        if (tokenValid)
            return Set(new IdentityState(IdentityStatus.SignedIn, s.Subject, null, s.Credential, s.SessionToken));

        if (now - s.LastAuthenticatedUtc <= options.OfflineGraceWindow)
            return Set(new IdentityState(IdentityStatus.OfflineGrace, s.Subject, null, s.Credential, s.SessionToken));

        return Set(new IdentityState(IdentityStatus.RequiresSignIn, null, null, null, null));
    }

    /// <summary>Runs the provider's interactive sign-in and persists the resulting credential. The verified
    /// <see cref="IdentityState.Subject"/> is not yet known at this point (only the server exchange in
    /// <see cref="AttachSessionTokenAsync"/> establishes it), so the resulting state is
    /// <see cref="IdentityStatus.OfflineGrace"/> with a null subject: a credential is held, but no session token is
    /// signed in yet. The consumer exchanges the credential with the server and calls
    /// <see cref="AttachSessionTokenAsync"/> to complete sign-in.</summary>
    public async Task<IdentityState> SignInAsync(CancellationToken ct)
    {
        ProviderCredential credential = await provider.SignInAsync(ct).ConfigureAwait(false);
        DateTimeOffset now = clock();
        CachedSession session = new(credential, null, null, now, null);
        await cache.SaveAsync(session, ct).ConfigureAwait(false);
        return Set(new IdentityState(IdentityStatus.OfflineGrace, null, null, credential, null));
    }

    /// <summary>Completes sign-in after the consumer has exchanged the provider credential with the server.
    /// Persists a <see cref="CachedSession"/> with the verified <paramref name="subject"/>, the new session token,
    /// and <c>LastAuthenticatedUtc</c> refreshed to now, then sets <see cref="Current"/> to
    /// <see cref="IdentityStatus.SignedIn"/> with that subject and <paramref name="displayName"/>.</summary>
    public async Task<IdentityState> AttachSessionTokenAsync(
        string subject, string? displayName, string sessionToken, DateTimeOffset expiryUtc, CancellationToken ct)
    {
        if (Current.Credential is not ProviderCredential credential)
            throw new InvalidOperationException("no credential to attach a session to; call SignInAsync first");

        DateTimeOffset now = clock();
        CachedSession session = new(credential, sessionToken, expiryUtc, now, subject);
        await cache.SaveAsync(session, ct).ConfigureAwait(false);
        return Set(new IdentityState(IdentityStatus.SignedIn, subject, displayName, credential, sessionToken));
    }

    public async Task SignOutAsync(CancellationToken ct)
    {
        await cache.ClearAsync(ct).ConfigureAwait(false);
        Set(new IdentityState(IdentityStatus.RequiresSignIn, null, null, null, null));
    }

    private IdentityState Set(IdentityState state)
    {
        Current = state;
        return state;
    }
}
