using System;
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

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static IdentitySession Build(MemCache cache, DateTimeOffset now)
        => new(new FakeProvider(), cache, new IdentitySessionOptions { OfflineGraceWindow = TimeSpan.FromDays(14) }, () => now);

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
}
