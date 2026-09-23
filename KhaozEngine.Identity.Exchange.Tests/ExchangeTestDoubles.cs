using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Accounts;
using KhaozEngine.Identity;
using KhaozEngine.Identity.Exchange;
using KhaozEngine.Netcode;

namespace KhaozEngine.Tests.Identity.Exchange;

/// <summary>Shared fixtures: the fixed instant, a key loaded the way production loads one, and the options.</summary>
internal static class ExchangeFixture
{
    public static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>A 32-byte key loaded through <see cref="SigningSecret.Load"/>, as a host loads its variable.</summary>
    public static byte[] Key()
    {
        string raw = "  " + Convert.ToBase64String(Enumerable.Range(1, SigningSecret.MinimumBytes).Select(i => (byte)i).ToArray()) + "\n";
        var environment = new Dictionary<string, string?> { ["TEST_TOKEN_SECRET"] = raw };
        return SigningSecret.Load(name => environment.GetValueOrDefault(name), "TEST_TOKEN_SECRET")!;
    }

    public static AuthExchangeOptions Options(TimeProvider clock, bool requireWhitelist = true) => new()
    {
        TokenLifetime = Lifetime,
        RequireWhitelist = requireWhitelist,
        Clock = clock,
    };

    public static AuthExchange Build(IIdentityValidator validator, IAccountStore store, TimeProvider? clock = null,
        IAuthExchangePolicy? policy = null, bool requireWhitelist = true, byte[]? key = null) =>
        new(new[] { validator }, store, key ?? Key(), Options(clock ?? new FixedClock(Now), requireWhitelist), policy);

    public static string Json(AuthExchangeResult result, bool includeBanDetails = false) =>
        JsonSerializer.Serialize(result.ToResponse(includeBanDetails), Web);
}

/// <summary>A clock that reads what it is told. Timers stay real, so a provider deadline still fires.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// A validator that answers whatever it is scripted to, through the detailed call only, so a test proves the exchange
/// never takes the flattening legacy path.
/// </summary>
internal sealed class ScriptedValidator(string providerId, Func<string, CancellationToken, Task<IdentityValidation>> answer)
    : IIdentityValidator
{
    private int calls;

    public string ProviderId => providerId;

    public int Calls => Volatile.Read(ref calls);

    /// <summary>Verifies each listed credential as its provider subject and name, and refuses every other.</summary>
    public static ScriptedValidator Users(params (string Credential, string Subject, string? Name)[] users) =>
        new("discord", (credential, _) =>
        {
            foreach ((string c, string subject, string? name) in users)
            {
                if (c == credential)
                    return Task.FromResult(IdentityValidation.Verified(
                        new VerifiedIdentity(subject, "discord", name, new Dictionary<string, string>())));
            }
            return Task.FromResult(IdentityValidation.Refused("unknown token"));
        });

    public static ScriptedValidator Always(IdentityValidation validation) =>
        new("discord", (_, _) => Task.FromResult(validation));

    public static ScriptedValidator Throwing(Exception fault) =>
        new("discord", (_, _) => Task.FromException<IdentityValidation>(fault));

    /// <summary>Waits for its cancellation token, as a cooperative validator stuck on a silent provider does.</summary>
    public static ScriptedValidator Stalled() =>
        new("discord", async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            return IdentityValidation.Refused();
        });

    public Task<VerifiedIdentity?> ValidateAsync(string credentialToken, CancellationToken ct = default) =>
        throw new NotSupportedException("The exchange must call ValidateDetailedAsync.");

    public Task<IdentityValidation> ValidateDetailedAsync(string credentialToken, CancellationToken ct = default)
    {
        Interlocked.Increment(ref calls);
        return answer(credentialToken, ct);
    }
}

/// <summary>
/// A validator written against the older contract: only <see cref="ValidateAsync"/>, so the default detailed member
/// maps its null to a refusal and lets its exceptions through. Ruinborne's exchange called this path directly.
/// </summary>
internal sealed class LegacyValidator(Func<VerifiedIdentity?> answer) : IIdentityValidator
{
    public string ProviderId => "discord";

    public Task<VerifiedIdentity?> ValidateAsync(string credentialToken, CancellationToken ct = default) =>
        Task.FromResult(answer());
}

/// <summary>
/// An account store over the reference store that counts every call and can be told to fail, to answer nothing, or
/// to hand back an account under a subject of its own choosing, as a game's own store over its own schema might.
/// </summary>
internal sealed class CountingAccountStore(IAccountStore inner) : IAccountStore
{
    private int calls;

    public CountingAccountStore(bool whitelistOnCreate) : this(new InMemoryAccountStore(whitelistOnCreate))
    {
    }

    public IAccountStore Inner => inner;

    public int Calls => Volatile.Read(ref calls);

    public AccountSignIn? LastSignIn { get; private set; }

    public Exception? Fault { get; set; }

    public bool AnswerNull { get; set; }

    public Func<AccountRecord, AccountRecord>? Rewrite { get; set; }

    public async Task<AccountRecord> FindOrCreateAsync(AccountSignIn signIn, CancellationToken ct = default)
    {
        Interlocked.Increment(ref calls);
        LastSignIn = signIn;
        if (Fault is not null) throw Fault;
        if (AnswerNull) return null!;
        AccountRecord account = await inner.FindOrCreateAsync(signIn, ct).ConfigureAwait(false);
        return Rewrite is null ? account : Rewrite(account);
    }

    public Task<AccountRecord?> FindAsync(string subject, CancellationToken ct = default) => Count(inner.FindAsync(subject, ct));

    public Task<IReadOnlyList<AccountRecord>> ListAsync(string? afterSubject = null, int limit = 500,
        CancellationToken ct = default) => Count(inner.ListAsync(afterSubject, limit, ct));

    public Task<IReadOnlyList<AccountRecord>> ListBannedAsync(CancellationToken ct = default) =>
        Count(inner.ListBannedAsync(ct));

    public Task<AccountRecord?> SetWhitelistedAsync(string subject, bool whitelisted, CancellationToken ct = default) =>
        Count(inner.SetWhitelistedAsync(subject, whitelisted, ct));

    public Task<AccountRecord?> BanAsync(string subject, string reason, DateTimeOffset? until,
        CancellationToken ct = default) => Count(inner.BanAsync(subject, reason, until, ct));

    public Task<AccountRecord?> UnbanAsync(string subject, CancellationToken ct = default) =>
        Count(inner.UnbanAsync(subject, ct));

    private Task<T> Count<T>(Task<T> task)
    {
        Interlocked.Increment(ref calls);
        return task;
    }
}

/// <summary>
/// Ruinborne's policy shape: the provider's name passes through unchanged (null stays null), and admission issues a
/// persistence key. Counts claim issues, so a test proves nothing is issued for a refused account.
/// </summary>
internal sealed class PassThroughPolicy(string? persistenceKey = null) : IAuthExchangePolicy
{
    private int issued;

    public int Issued => Volatile.Read(ref issued);

    public string? ResolveDisplayName(VerifiedIdentity identity) => identity.DisplayName;

    public ValueTask<SessionClaims> IssueClaimsAsync(AccountRecord account, CancellationToken ct)
    {
        Interlocked.Increment(ref issued);
        return ValueTask.FromResult(new SessionClaims(account.DisplayName ?? account.Subject, persistenceKey));
    }
}

/// <summary>A policy that fails where it is told to, or issues claims with no name.</summary>
internal sealed class FaultyPolicy(bool failResolve = false, bool failIssue = false, bool issueNoName = false)
    : IAuthExchangePolicy
{
    public string? ResolveDisplayName(VerifiedIdentity identity) =>
        failResolve ? throw new InvalidOperationException("resolve fault") : identity.DisplayName;

    public ValueTask<SessionClaims> IssueClaimsAsync(AccountRecord account, CancellationToken ct)
    {
        if (failIssue) throw new InvalidOperationException("issue fault");
        return ValueTask.FromResult(issueNoName ? default : new SessionClaims("name", null));
    }
}
