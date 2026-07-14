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
/// HTTP-free) and calls <see cref="AttachSessionTokenAsync(string, string, string, System.DateTimeOffset, System.Threading.CancellationToken)"/>
/// with the verified subject to reach <see cref="IdentityStatus.SignedIn"/>.
///
/// A lapsed session can be renewed silently: while a credential is held, <see cref="RefreshCredentialAsync"/>
/// exchanges it with the provider for a fresh, rotated credential and persists it durably, so silent reconnect
/// survives across days even when the provider rotates the refresh token on every use.</summary>
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
    /// <see cref="AttachSessionTokenAsync(string, string, string, System.DateTimeOffset, System.Threading.CancellationToken)"/>
    /// establishes it), so the resulting state is
    /// <see cref="IdentityStatus.OfflineGrace"/> with a null subject: a credential is held, but no session token is
    /// signed in yet. The consumer exchanges the credential with the server and calls
    /// <see cref="AttachSessionTokenAsync(string, string, string, System.DateTimeOffset, System.Threading.CancellationToken)"/>
    /// to complete sign-in.</summary>
    public async Task<IdentityState> SignInAsync(CancellationToken ct)
    {
        ProviderCredential credential = await provider.SignInAsync(ct).ConfigureAwait(false);
        DateTimeOffset now = clock();
        CachedSession session = new(credential, null, null, now, null);
        await cache.SaveAsync(session, ct).ConfigureAwait(false);
        return Set(new IdentityState(IdentityStatus.OfflineGrace, null, null, credential, null));
    }

    /// <summary>Completes sign-in after the consumer has exchanged the provider credential with the server, using
    /// the credential currently held in <see cref="Current"/>. Persists a <see cref="CachedSession"/> with the
    /// verified <paramref name="subject"/>, the new session token, and <c>LastAuthenticatedUtc</c> refreshed to
    /// now, then sets <see cref="Current"/> to <see cref="IdentityStatus.SignedIn"/> with that subject and
    /// <paramref name="displayName"/>.
    ///
    /// This is the turn-key overload. It persists whatever credential <see cref="Current"/> holds, which is the
    /// rotated credential once <see cref="RefreshCredentialAsync"/> has run (that method updates
    /// <see cref="Current"/> before the exchange). A consumer that orchestrates its own provider refresh, and so
    /// does not go through <see cref="RefreshCredentialAsync"/>, should call the
    /// <see cref="AttachSessionTokenAsync(string, string, ProviderCredential, string, System.DateTimeOffset, System.Threading.CancellationToken)"/>
    /// overload with the freshly refreshed credential, otherwise the stale credential is re-persisted.</summary>
    /// <exception cref="InvalidOperationException">No credential is held to attach a session to. Call
    /// <see cref="SignInAsync"/> first.</exception>
    public async Task<IdentityState> AttachSessionTokenAsync(
        string subject, string? displayName, string sessionToken, DateTimeOffset expiryUtc, CancellationToken ct)
    {
        if (Current.Credential is not ProviderCredential credential)
            throw new InvalidOperationException("no credential to attach a session to; call SignInAsync first");

        return await AttachSessionTokenAsync(subject, displayName, credential, sessionToken, expiryUtc, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Completes sign-in with an explicitly supplied provider <paramref name="credential"/>. Persists a
    /// <see cref="CachedSession"/> carrying THAT credential (not whatever <see cref="Current"/> held), the verified
    /// <paramref name="subject"/>, the new session token, and <c>LastAuthenticatedUtc</c> refreshed to now, then
    /// sets <see cref="Current"/> to <see cref="IdentityStatus.SignedIn"/> with the same credential.
    ///
    /// Use this when the consumer runs its own provider refresh before the server exchange: passing the freshly
    /// refreshed credential persists the rotated token, so the NEXT silent refresh presents a live token. A
    /// successful attach is the only step that moves <c>LastAuthenticatedUtc</c> forward and so re-anchors the
    /// offline-grace window.</summary>
    public async Task<IdentityState> AttachSessionTokenAsync(
        string subject, string? displayName, ProviderCredential credential, string sessionToken,
        DateTimeOffset expiryUtc, CancellationToken ct)
    {
        DateTimeOffset now = clock();
        CachedSession session = new(credential, sessionToken, expiryUtc, now, subject);
        await cache.SaveAsync(session, ct).ConfigureAwait(false);
        return Set(new IdentityState(IdentityStatus.SignedIn, subject, displayName, credential, sessionToken));
    }

    /// <summary>Renews the held provider credential against the provider's rotating-token endpoint, persisting the
    /// rotated credential durably BEFORE any server exchange, so silent reconnect survives across days even when
    /// the provider rotates the refresh token on every use (Discord and most OAuth providers do, invalidating the
    /// old token the instant the refresh succeeds). A crash between this refresh and the following
    /// <see cref="AttachSessionTokenAsync(string, string, string, System.DateTimeOffset, System.Threading.CancellationToken)"/>
    /// must not lose the rotated token, otherwise the next silent refresh presents a dead token and fails.
    ///
    /// The cache write replaces only the credential slot. It preserves the subject, the session token, the
    /// session-token expiry, and <c>LastAuthenticatedUtc</c> exactly. A provider-level refresh is not a server
    /// exchange, so it must NOT extend the offline-grace window. Only a successful
    /// <see cref="AttachSessionTokenAsync(string, string, string, System.DateTimeOffset, System.Threading.CancellationToken)"/>
    /// moves <c>LastAuthenticatedUtc</c> forward. <see cref="Current"/> is updated to carry the rotated credential
    /// at the SAME status. A refresh renews the credential, it does not sign the player in.
    ///
    /// The one carve-out is the degenerate empty-cache fallback: if the cache was lost out-of-band while
    /// <see cref="Current"/> still holds a credential, there is no stored session to preserve, so the rebuilt
    /// <see cref="CachedSession"/> anchors <c>LastAuthenticatedUtc</c> at now instead.
    ///
    /// If the cache save throws after the provider refresh already succeeded, the rotated credential is not
    /// retained and the exception propagates unchanged. The next refresh attempt then presents the old, now
    /// invalidated, token and comes back <see cref="CredentialRefreshOutcome.Rejected"/>, sending the consumer to
    /// interactive sign-in. This fails safe rather than letting the cache and <see cref="Current"/> silently
    /// diverge.
    ///
    /// Outcomes (see <see cref="CredentialRefreshResult"/>):
    /// <list type="bullet">
    /// <item>the provider returns a fresh credential -> <see cref="CredentialRefreshOutcome.Refreshed"/></item>
    /// <item>the provider returns null (a revoked or expired chain) -> <see cref="CredentialRefreshOutcome.Rejected"/>,
    /// the cache and <see cref="Current"/> left untouched, the consumer falls back to interactive sign-in</item>
    /// <item>the provider throws (a transient 5xx or a transport fault) -> the exception propagates unchanged, so
    /// the consumer retries later rather than forcing a sign-in</item>
    /// </list></summary>
    /// <exception cref="InvalidOperationException">No credential is held to refresh. Call
    /// <see cref="SignInAsync"/> first.</exception>
    public async Task<CredentialRefreshResult> RefreshCredentialAsync(CancellationToken ct)
    {
        if (Current.Credential is not ProviderCredential credential)
            throw new InvalidOperationException("no credential to refresh; call SignInAsync first");

        ProviderCredential? result = await provider.RefreshAsync(credential, ct).ConfigureAwait(false);
        if (result is not ProviderCredential refreshed)
            return new CredentialRefreshResult(CredentialRefreshOutcome.Rejected, Current);

        // Persist the rotated credential immediately, before any server exchange. Replace only the credential
        // slot so the grace anchor (LastAuthenticatedUtc), the session token, and the subject are preserved. When
        // the cache is empty there is no stored anchor to preserve, so build a session from Current's fields and
        // anchor at now.
        CachedSession? cached = await cache.LoadAsync(ct).ConfigureAwait(false);
        CachedSession updated = cached is CachedSession existing
            ? existing with { Credential = refreshed }
            : new CachedSession(refreshed, Current.SessionToken, null, clock(), Current.Subject);
        await cache.SaveAsync(updated, ct).ConfigureAwait(false);

        IdentityState state = Set(Current with { Credential = refreshed });
        return new CredentialRefreshResult(CredentialRefreshOutcome.Refreshed, state);
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
