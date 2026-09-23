using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Accounts;
using KhaozEngine.Netcode;

namespace KhaozEngine.Identity.Exchange;

/// <summary>
/// Trades a provider credential for a <see cref="SignedToken"/>: the whole decision behind <c>POST /auth/exchange</c>,
/// with no HTTP type in it. One instance serves every request and is safe to call concurrently.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ExchangeAsync"/> runs one fixed sequence:
/// </para>
/// <list type="number">
/// <item>The shape check. An unknown provider id, or a credential that is blank, longer than
/// <see cref="AuthExchangeOptions.MaxCredentialChars"/>, or carries anything but visible ASCII (a control character,
/// a space or a non-ASCII character), answers <see cref="AuthExchangeOutcome.Malformed"/> before any provider
/// call.</item>
/// <item>The provider. <see cref="IIdentityValidator.ValidateDetailedAsync"/> runs under
/// <see cref="AuthExchangeOptions.ProviderTimeout"/>. A reported outage (a provider 5xx or 429), a validator that
/// throws, or the deadline answers <see cref="AuthExchangeOutcome.Unavailable"/>, and a refusal answers
/// <see cref="AuthExchangeOutcome.InvalidCredential"/>, both before any account is touched. An outage never reads as a
/// refused credential, because a client that discards a good token over an outage re-runs sign-in against a provider
/// that is already down.</item>
/// <item>The account. The policy resolves the display name, clamped to
/// <see cref="AuthExchangeOptions.MaxDisplayNameChars"/>, and the store finds or creates the account. A subject the
/// store returns that <see cref="AccountStoreRules.IsAdmissibleSubject"/> refuses is a server fault, never a
/// token.</item>
/// <item>The gate. <see cref="AuthAdmission.Decide"/> refuses a banned account first and an unwhitelisted one
/// second.</item>
/// <item>The token. Only an admitted account reaches the policy's claims, and the exchange mints v3 when they carry a
/// persistence key and v2 otherwise, expiring at the clock plus <see cref="AuthExchangeOptions.TokenLifetime"/>.</item>
/// </list>
/// <para>
/// Any store or policy exception answers <see cref="AuthExchangeOutcome.Unavailable"/> with the exception in
/// <see cref="AuthExchangeResult.Fault"/>. The caller's own cancellation propagates as
/// <see cref="OperationCanceledException"/>. Nothing is logged here: the host logs the outcome and the cause, and
/// never the credential, the token, the key, the subject or the display name.
/// </para>
/// </remarks>
public sealed class AuthExchange
{
    private static readonly IAuthExchangePolicy DefaultPolicy = new DefaultAuthExchangePolicy();

    private readonly Dictionary<string, IIdentityValidator> validators = new(StringComparer.Ordinal);
    private readonly IAccountStore accounts;
    private readonly byte[] signingSecret;
    private readonly AuthExchangeOptions options;
    private readonly IAuthExchangePolicy policy;

    /// <summary>Composes an exchange.</summary>
    /// <param name="validators">One validator per provider, keyed by <see cref="IIdentityValidator.ProviderId"/>
    /// (ordinal). At least one, and no provider id twice.</param>
    /// <param name="accounts">The account store. The source of truth for the whitelist flag and the ban.</param>
    /// <param name="signingSecret">The HMAC key the game server verifies tokens under, at least
    /// <see cref="SigningSecret.MinimumBytes"/> long. Load it with <see cref="SigningSecret.Load"/>, or use
    /// <see cref="SigningSecret.CreateEphemeral"/> for local development. The exchange keeps its own copy.</param>
    /// <param name="options">The lifetime, the caps, the provider deadline and the clock.</param>
    /// <param name="policy">The game's display name and claims policy, or <c>null</c> for the defaults: the provider's
    /// name else the provider subject, and a v2 token.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The key is shorter than <see cref="SigningSecret.MinimumBytes"/>, there is
    /// no validator, a validator is null or has a blank provider id, or two share one.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range, including a
    /// <see cref="AuthExchangeOptions.MaxDisplayNameChars"/> above <see cref="AccountStoreRules.MaxDisplayNameChars"/>.</exception>
    public AuthExchange(IEnumerable<IIdentityValidator> validators, IAccountStore accounts, byte[] signingSecret,
        AuthExchangeOptions options, IAuthExchangePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(validators);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(signingSecret);
        ArgumentNullException.ThrowIfNull(options);
        if (signingSecret.Length < SigningSecret.MinimumBytes)
            throw new ArgumentException(
                $"The signing key is {signingSecret.Length} bytes, under the {SigningSecret.MinimumBytes}-byte minimum. " +
                "A shorter HMAC key is brute-forceable, so the exchange refuses to mint under it.", nameof(signingSecret));
        options.Validate();

        foreach (IIdentityValidator validator in validators)
        {
            if (validator is null)
                throw new ArgumentException("A validator in the set is null.", nameof(validators));
            string providerId = validator.ProviderId;
            if (string.IsNullOrWhiteSpace(providerId))
                throw new ArgumentException("A validator has a blank provider id.", nameof(validators));
            if (!this.validators.TryAdd(providerId, validator))
                throw new ArgumentException($"Two validators claim the provider id '{providerId}'.", nameof(validators));
        }
        if (this.validators.Count == 0)
            throw new ArgumentException("An exchange needs at least one validator.", nameof(validators));

        this.accounts = accounts;
        this.signingSecret = (byte[])signingSecret.Clone();
        this.options = options;
        this.policy = policy ?? DefaultPolicy;
    }

    /// <summary>
    /// Runs one exchange. Never throws for a refusal or a dependency failure: each is an outcome.
    /// </summary>
    /// <param name="provider">The provider id the client named. Matched ordinally.</param>
    /// <param name="accessToken">The provider credential the client posted.</param>
    /// <param name="ct">The caller's cancellation, which propagates.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<AuthExchangeResult> ExchangeAsync(string? provider, string? accessToken,
        CancellationToken ct = default)
    {
        if (provider is null || !validators.TryGetValue(provider, out IIdentityValidator? validator))
            return new AuthExchangeResult(AuthExchangeOutcome.Malformed, Cause: AuthExchangeCause.UnknownProvider);
        if (string.IsNullOrEmpty(accessToken) || accessToken.Length > options.MaxCredentialChars
            || !IsVisibleAscii(accessToken))
            return new AuthExchangeResult(AuthExchangeOutcome.Malformed, Cause: AuthExchangeCause.CredentialShape);
        ct.ThrowIfCancellationRequested();

        (IdentityValidation validation, AuthExchangeResult? providerFailure) =
            await ValidateUnderDeadlineAsync(provider, validator, accessToken, ct).ConfigureAwait(false);
        if (providerFailure is not null) return providerFailure;
        if (validation.Outcome == IdentityValidationOutcome.Refused)
            return new AuthExchangeResult(AuthExchangeOutcome.InvalidCredential, Cause: AuthExchangeCause.CredentialRefused);
        if (validation.Outcome != IdentityValidationOutcome.Verified || validation.Identity is not { } identity)
            return Unavailable(AuthExchangeCause.ProviderUnavailable, fault: null);

        string? displayName;
        try
        {
            displayName = Clamp(policy.ResolveDisplayName(identity), options.MaxDisplayNameChars);
        }
        catch (Exception ex)
        {
            return Unavailable(AuthExchangeCause.PolicyFault, ex);
        }

        DateTimeOffset now = options.Clock.GetUtcNow();
        var signIn = new AccountSignIn(provider, identity.Subject, displayName,
            identity.Claims ?? ReadOnlyDictionary<string, string>.Empty, now);
        AccountRecord? account;
        try
        {
            account = await accounts.FindOrCreateAsync(signIn, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Unavailable(AuthExchangeCause.StoreFault, ex);
        }

        if (account is null)
            return Unavailable(AuthExchangeCause.StoreFault,
                new InvalidOperationException("The account store returned no account for a verified sign-in."));
        // Checked here rather than trusted, because a game's own store mints its own subjects and a token or a join
        // gate that splits on '.' or reserves guest: must never be handed one it refuses. The message never quotes it.
        if (!AccountStoreRules.IsAdmissibleSubject(account.Subject))
            return Unavailable(AuthExchangeCause.InadmissibleSubject, new InvalidOperationException(
                "The account store returned a subject a SignedToken or the join gate refuses: empty, carrying '.', or " +
                $"under the reserved '{AccountStoreRules.ReservedSubjectPrefix}' prefix."));

        AuthExchangeOutcome admission = AuthAdmission.Decide(account, now, options.RequireWhitelist);
        if (admission == AuthExchangeOutcome.Banned)
            return new AuthExchangeResult(AuthExchangeOutcome.Banned, Subject: account.Subject,
                DisplayName: account.DisplayName, Ban: account.Ban);
        if (admission == AuthExchangeOutcome.NotWhitelisted)
            return new AuthExchangeResult(AuthExchangeOutcome.NotWhitelisted, Subject: account.Subject,
                DisplayName: account.DisplayName);

        SessionClaims claims;
        try
        {
            claims = await policy.IssueClaimsAsync(account, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Unavailable(AuthExchangeCause.PolicyFault, ex);
        }
        if (claims.DisplayName is null)
            return Unavailable(AuthExchangeCause.PolicyFault,
                new InvalidOperationException("The exchange policy issued claims with no display name."));

        DateTimeOffset expires = now + options.TokenLifetime;
        string token = string.IsNullOrEmpty(claims.PersistenceKey)
            ? SignedToken.Mint(account.Subject, claims.DisplayName, expires, signingSecret)
            : SignedToken.Mint(account.Subject, claims.DisplayName, claims.PersistenceKey, expires, signingSecret);
        return new AuthExchangeResult(AuthExchangeOutcome.Ok, token, expires, account.Subject, account.DisplayName);
    }

    // The validator under the deadline. The linked token lets a cooperative validator abort its call, and WaitAsync
    // stops waiting at the deadline even for one that ignores the token, so no validator holds a request past it. The
    // call starts on the thread pool, because a validator that blocks before returning its task (synchronous I/O ahead
    // of its first await) would otherwise hold this caller before there is any task to put a deadline on.
    private async Task<(IdentityValidation Validation, AuthExchangeResult? Failure)> ValidateUnderDeadlineAsync(
        string provider, IIdentityValidator validator, string accessToken, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(options.ProviderTimeout, options.Clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        CancellationToken token = linked.Token;
        Task<IdentityValidation>? call = null;
        try
        {
            call = Task.Run(() => validator.ValidateDetailedAsync(accessToken, token), token);
            IdentityValidation validation = await call.WaitAsync(token).ConfigureAwait(false);
            return validation.Outcome == IdentityValidationOutcome.ProviderUnavailable
                ? (default, Unavailable(AuthExchangeCause.ProviderUnavailable, fault: null))
                : (validation, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            ObserveAbandoned(call);
            return (default, Unavailable(AuthExchangeCause.ProviderTimeout, new TimeoutException(
                $"The '{provider}' validator did not answer within {options.ProviderTimeout}.", ex)));
        }
        catch (Exception ex)
        {
            return (default, Unavailable(AuthExchangeCause.ProviderUnavailable, ex));
        }
    }

    private static AuthExchangeResult Unavailable(AuthExchangeCause cause, Exception? fault) =>
        new(AuthExchangeOutcome.Unavailable, Fault: fault, Cause: cause);

    // Every bearer credential a provider issues (an OAuth token, a JWT) is visible ASCII, '!' to '~'. Anything else is
    // garbage to refuse here: a control character reaching a provider's Authorization header throws inside the HTTP
    // client, which would answer an unauthenticated caller with an outage and a logged stack trace.
    private static bool IsVisibleAscii(string credential)
    {
        foreach (char c in credential)
        {
            if (c is < '!' or > '~') return false;
        }
        return true;
    }

    // A validator abandoned at the deadline may still fault later. Observing it keeps that from surfacing as an
    // unobserved task exception long after the request was answered.
    private static void ObserveAbandoned(Task? call)
    {
        if (call is null || call.IsCompleted) return;
        _ = call.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // Clamps to at most max UTF-16 code units without splitting a surrogate pair. Clamped rather than refused: a
    // provider name longer than the game keeps is not the player's fault.
    private static string? Clamp(string? name, int max)
    {
        if (name is null || name.Length <= max) return name;
        int cut = char.IsHighSurrogate(name[max - 1]) ? max - 1 : max;
        return name[..cut];
    }

    private sealed class DefaultAuthExchangePolicy : IAuthExchangePolicy;
}
