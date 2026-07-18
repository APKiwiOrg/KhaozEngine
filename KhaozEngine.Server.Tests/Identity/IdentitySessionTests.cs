using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Identity;
using Xunit;

namespace KhaozEngine.Tests.Identity;

public class IdentitySessionTests
{
    private sealed class MemCache : ITokenCache
    {
        public CachedSession? Value;
        public Task<CachedSession?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Value);
        public Task SaveAsync(CachedSession s, CancellationToken ct = default) { Value = s; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken ct = default) { Value = null; return Task.CompletedTask; }
    }

    private sealed class FakeProvider : IIdentityProvider
    {
        public string ProviderId => "fake";
        public Task<ProviderCredential> SignInAsync(CancellationToken ct = default)
            => Task.FromResult(new ProviderCredential("fake", "cred", "refresh", DateTimeOffset.UnixEpoch));
        public Task<ProviderCredential?> RefreshAsync(ProviderCredential e, CancellationToken ct = default)
            => Task.FromResult<ProviderCredential?>(null);
    }

    /// <summary>Models a rotating-refresh-token provider (Discord's behaviour): every successful refresh mints
    /// a new refresh token and invalidates the previous one, so presenting a stale (already-rotated-away) token
    /// is rejected with null. Sign-in seeds the chain at "r0".</summary>
    private sealed class RotatingProvider : IIdentityProvider
    {
        private int counter;
        private string live = "r0";

        public string ProviderId => "rot";

        public Task<ProviderCredential> SignInAsync(CancellationToken ct = default)
            => Task.FromResult(new ProviderCredential("rot", "access-0", live, DateTimeOffset.UnixEpoch));

        public Task<ProviderCredential?> RefreshAsync(ProviderCredential presented, CancellationToken ct = default)
        {
            if (presented.RefreshToken != live)
                return Task.FromResult<ProviderCredential?>(null);
            counter++;
            live = $"r{counter}";
            return Task.FromResult<ProviderCredential?>(
                new ProviderCredential("rot", $"access-{counter}", live, DateTimeOffset.UnixEpoch));
        }
    }

    private sealed class ThrowingProvider : IIdentityProvider
    {
        public string ProviderId => "throw";
        public Task<ProviderCredential> SignInAsync(CancellationToken ct = default)
            => Task.FromResult(new ProviderCredential("throw", "cred", "refresh", DateTimeOffset.UnixEpoch));
        public Task<ProviderCredential?> RefreshAsync(ProviderCredential e, CancellationToken ct = default)
            => throw new HttpRequestException("simulated transient transport fault");
    }

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly IdentitySessionOptions Opts = new() { OfflineGraceWindow = TimeSpan.FromDays(14) };

    private static IdentitySession Build(MemCache cache, DateTimeOffset now)
        => new(new FakeProvider(), cache, Opts, () => now);

    [Fact]
    public async Task No_cache_requires_signin()
    {
        MemCache c = new();
        IdentityState s = await Build(c, T0).RestoreAsync(CancellationToken.None);
        Assert.Equal(IdentityStatus.RequiresSignIn, s.Status);
    }

    [Fact]
    public async Task Valid_session_token_is_signed_in()
    {
        MemCache c = new();
        c.Value = new CachedSession(
            new ProviderCredential("fake", "cred", "r", T0.AddDays(30)),
            "session",
            T0.AddHours(1),
            T0,
            "user-42");
        IdentityState s = await Build(c, T0.AddMinutes(30)).RestoreAsync(CancellationToken.None);
        Assert.Equal(IdentityStatus.SignedIn, s.Status);
        Assert.Equal("session", s.SessionToken);
        Assert.Equal("user-42", s.Subject);
    }

    [Fact]
    public async Task Expired_session_offline_within_window_is_offline_grace()
    {
        MemCache c = new();
        c.Value = new CachedSession(
            new ProviderCredential("fake", "cred", "r", T0),
            "session",
            T0.AddHours(1),
            T0,
            "user-42");
        IdentityState s = await Build(c, T0.AddDays(5)).RestoreAsync(CancellationToken.None);
        Assert.Equal(IdentityStatus.OfflineGrace, s.Status);
        Assert.Equal("user-42", s.Subject);
    }

    [Fact]
    public async Task Expired_session_past_grace_requires_signin()
    {
        MemCache c = new();
        c.Value = new CachedSession(
            new ProviderCredential("fake", "cred", "r", T0),
            "session",
            T0.AddHours(1),
            T0,
            "user-42");
        IdentityState s = await Build(c, T0.AddDays(20)).RestoreAsync(CancellationToken.None);
        Assert.Equal(IdentityStatus.RequiresSignIn, s.Status);
    }

    [Fact]
    public async Task No_cache_sign_in_leaves_subject_null_until_attach()
    {
        MemCache c = new();
        IdentitySession session = Build(c, T0);
        IdentityState signedIn = await session.SignInAsync(CancellationToken.None);
        Assert.Null(signedIn.Subject);
        Assert.NotNull(c.Value);
        Assert.Null(c.Value!.Value.Subject);
    }

    [Fact]
    public async Task Attach_session_token_sets_subject_display_name_and_signed_in()
    {
        MemCache c = new();
        IdentitySession session = Build(c, T0);
        await session.SignInAsync(CancellationToken.None);

        IdentityState state = await session.AttachSessionTokenAsync(
            "user-99", "Display Name", "session-tok", T0.AddHours(1), CancellationToken.None);

        Assert.Equal(IdentityStatus.SignedIn, state.Status);
        Assert.Equal("user-99", state.Subject);
        Assert.Equal("Display Name", state.DisplayName);
        Assert.Equal("session-tok", state.SessionToken);

        Assert.NotNull(c.Value);
        Assert.Equal("user-99", c.Value!.Value.Subject);
        Assert.Equal("session-tok", c.Value.Value.SessionToken);
    }

    [Fact]
    public async Task Restored_offline_grace_and_signed_in_never_expose_placeholder_subject()
    {
        // Guards against the brief's placeholder (Subject(s) => Credential.ProviderId): the provider id
        // is "fake" for every case here, but the persisted verified Subject is a distinct value, so if the
        // placeholder ever creeps back in, this assertion catches it.
        MemCache c = new();
        c.Value = new CachedSession(
            new ProviderCredential("fake", "cred", "r", T0.AddDays(30)),
            "session",
            T0.AddHours(1),
            T0,
            "verified-subject-not-provider-id");
        IdentityState s = await Build(c, T0.AddMinutes(30)).RestoreAsync(CancellationToken.None);
        Assert.Equal("verified-subject-not-provider-id", s.Subject);
        Assert.NotEqual("fake", s.Subject);
    }

    // ---- Durable silent refresh (RefreshCredentialAsync + the rotated-token persist contract) ----

    /// <summary>Acceptance test: N consecutive silent refreshes against a rotating-token provider all succeed,
    /// and after every cycle the cache holds exactly the latest rotated refresh token. The old code fails on
    /// cycle 2 because the rotated token never lands in the cache or <c>Current</c>, so the second refresh
    /// presents a stale token the provider has already invalidated.</summary>
    [Fact]
    public async Task Rotation_durability_five_cycles_all_succeed_and_cache_holds_latest_token()
    {
        MemCache c = new();
        IdentitySession session = new(new RotatingProvider(), c, Opts, () => T0);
        await session.SignInAsync(CancellationToken.None);
        await session.AttachSessionTokenAsync("sub", null, "sess-0", T0.AddHours(1), CancellationToken.None);

        for (int i = 1; i <= 5; i++)
        {
            CredentialRefreshResult r = await session.RefreshCredentialAsync(CancellationToken.None);
            Assert.Equal(CredentialRefreshOutcome.Refreshed, r.Outcome);
            Assert.True(r.IsRefreshed);
            // The rotated token is in the cache immediately, before the server exchange below.
            Assert.Equal($"r{i}", c.Value!.Value.Credential.RefreshToken);

            // Consumer exchanges with the game server and attaches via the OLD overload (Nullwake's shape).
            await session.AttachSessionTokenAsync("sub", null, $"sess-{i}", T0.AddHours(1), CancellationToken.None);
            Assert.Equal($"r{i}", c.Value!.Value.Credential.RefreshToken);
        }
    }

    /// <summary>The precise field-bug shape: refresh, attach, refresh again. The second refresh must succeed.
    /// The old code re-persists the stale credential on attach, so the second refresh presents the
    /// already-rotated-away token and the provider rejects it (401).</summary>
    [Fact]
    public async Task One_shot_regression_second_refresh_succeeds()
    {
        MemCache c = new();
        IdentitySession session = new(new RotatingProvider(), c, Opts, () => T0);
        await session.SignInAsync(CancellationToken.None);
        await session.AttachSessionTokenAsync("sub", null, "sess-0", T0.AddHours(1), CancellationToken.None);

        CredentialRefreshResult first = await session.RefreshCredentialAsync(CancellationToken.None);
        Assert.Equal(CredentialRefreshOutcome.Refreshed, first.Outcome);
        await session.AttachSessionTokenAsync("sub", null, "sess-1", T0.AddHours(1), CancellationToken.None);

        CredentialRefreshResult second = await session.RefreshCredentialAsync(CancellationToken.None);
        Assert.Equal(CredentialRefreshOutcome.Refreshed, second.Outcome);
        Assert.Equal("r2", c.Value!.Value.Credential.RefreshToken);
    }

    /// <summary>A provider-level refresh persists the rotated credential immediately (before any server
    /// exchange), preserves the offline-grace anchor and the session slot exactly, and updates
    /// <c>Current</c> at the SAME status. A refresh renews the credential, it does not sign the player in.</summary>
    [Fact]
    public async Task Refresh_persists_rotated_credential_before_exchange_without_touching_grace_or_status()
    {
        MemCache c = new();
        IdentitySession session = new(new RotatingProvider(), c, Opts, () => T0.AddHours(1));
        c.Value = new CachedSession(
            new ProviderCredential("rot", "access-0", "r0", T0), "sess", T0, T0, "sub");
        IdentityState restored = await session.RestoreAsync(CancellationToken.None);
        Assert.Equal(IdentityStatus.OfflineGrace, restored.Status);

        CredentialRefreshResult r = await session.RefreshCredentialAsync(CancellationToken.None);

        Assert.Equal(CredentialRefreshOutcome.Refreshed, r.Outcome);
        Assert.Equal("r1", c.Value!.Value.Credential.RefreshToken);
        Assert.Equal("access-1", c.Value!.Value.Credential.CredentialToken);
        Assert.Equal(T0, c.Value!.Value.LastAuthenticatedUtc);       // grace anchor NOT extended by a refresh
        Assert.Equal("sess", c.Value!.Value.SessionToken);           // session slot preserved
        Assert.Equal(T0, c.Value!.Value.SessionTokenExpiresUtc);
        Assert.Equal("sub", c.Value!.Value.Subject);
        Assert.Equal(IdentityStatus.OfflineGrace, session.Current.Status);   // status unchanged
        Assert.Equal("r1", session.Current.Credential!.Value.RefreshToken);  // Current carries the rotated cred
    }

    /// <summary>The provider says the chain is dead (null). The outcome is Rejected and neither the cache nor
    /// <c>Current</c> is touched, so the consumer falls to interactive sign-in.</summary>
    [Fact]
    public async Task Refresh_rejected_when_provider_returns_null_leaves_cache_and_current_untouched()
    {
        MemCache c = new();
        IdentitySession session = new(new FakeProvider(), c, Opts, () => T0);
        await session.SignInAsync(CancellationToken.None);
        CachedSession before = c.Value!.Value;
        IdentityState currentBefore = session.Current;

        CredentialRefreshResult r = await session.RefreshCredentialAsync(CancellationToken.None);

        Assert.Equal(CredentialRefreshOutcome.Rejected, r.Outcome);
        Assert.False(r.IsRefreshed);
        Assert.Equal(currentBefore, r.State);
        Assert.Equal(before, c.Value!.Value);
        Assert.Equal(currentBefore, session.Current);
    }

    /// <summary>A transient provider failure (a 5xx or a transport fault) propagates unchanged. The consumer
    /// treats it as offline-retry-later, not sign-in-required, so the cache and <c>Current</c> stay put.</summary>
    [Fact]
    public async Task Refresh_propagates_provider_exception_and_leaves_cache_and_current_untouched()
    {
        MemCache c = new();
        IdentitySession session = new(new ThrowingProvider(), c, Opts, () => T0);
        await session.SignInAsync(CancellationToken.None);
        CachedSession before = c.Value!.Value;
        IdentityState currentBefore = session.Current;

        await Assert.ThrowsAsync<HttpRequestException>(() => session.RefreshCredentialAsync(CancellationToken.None));

        Assert.Equal(before, c.Value!.Value);
        Assert.Equal(currentBefore, session.Current);
    }

    /// <summary>No credential held -> the refresh guard throws, mirroring the attach guard.</summary>
    [Fact]
    public async Task Refresh_without_credential_throws()
    {
        MemCache c = new();
        IdentitySession session = new(new FakeProvider(), c, Opts, () => T0);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.RefreshCredentialAsync(CancellationToken.None));
    }

    /// <summary>The explicit-credential attach overload persists the PASSED credential and puts it in the
    /// resulting state, not <c>Current.Credential</c>. This serves consumers that orchestrate their own
    /// provider refresh without going through <c>RefreshCredentialAsync</c>.</summary>
    [Fact]
    public async Task Attach_with_explicit_credential_persists_that_credential_not_current()
    {
        MemCache c = new();
        IdentitySession session = new(new FakeProvider(), c, Opts, () => T0);
        await session.SignInAsync(CancellationToken.None); // Current.Credential is ("fake","cred","refresh",...)

        ProviderCredential rotated = new("fake", "new-access", "new-refresh", T0.AddDays(1));
        IdentityState state = await session.AttachSessionTokenAsync(
            "sub", "Name", rotated, "sess", T0.AddHours(1), CancellationToken.None);

        Assert.Equal("new-refresh", c.Value!.Value.Credential.RefreshToken);
        Assert.Equal("new-access", c.Value!.Value.Credential.CredentialToken);
        Assert.Equal("new-access", state.Credential!.Value.CredentialToken);
        Assert.Equal(IdentityStatus.SignedIn, state.Status);
        Assert.Equal("sub", state.Subject);
        Assert.Equal("Name", state.DisplayName);
    }
}
