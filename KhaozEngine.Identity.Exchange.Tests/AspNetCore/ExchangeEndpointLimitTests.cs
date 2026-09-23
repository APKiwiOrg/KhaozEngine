using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Accounts;
using KhaozEngine.Identity;
using KhaozEngine.Identity.Exchange;
using KhaozEngine.Identity.Exchange.AspNetCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace KhaozEngine.Tests.Identity.Exchange.AspNetCore;

/// <summary>
/// The endpoint's own bounds over a real socket: the per-client window with its <c>Retry-After</c>, the IPv6 /64
/// grouping, the global bound and its queue, a health probe that none of them starve, and caller cancellation.
/// </summary>
[Collection(ExchangeKestrelCollection.Name)]
public class ExchangeEndpointLimitTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task ThePerClientWindow_RefusesTheSixthExchange_With429AndRetryAfter_AndLeavesHealthAlone()
    {
        ScriptedValidator validator = ExchangeAccounts.Validator();
        await using ExchangeHttpHost host = await ExchangeHttpHost.StartAsync(await ExchangeAccounts.BuildAsync(validator));

        for (int i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await host.ExchangeAsync("discord", "bad")).Status);
        ExchangeReply refused = await host.ExchangeAsync("discord", ExchangeAccounts.OkCredential);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.Status);
        Assert.Empty(refused.Body);
        Assert.True(refused.NoStore);
        Assert.NotNull(refused.RetryAfter);
        Assert.InRange(refused.RetryAfter!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        // Refused before the body was read, so the provider never saw the sixth credential.
        Assert.Equal(5, validator.Calls);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task IPv6Callers_ShareOneWindowPerSlash64_ThroughTheTrustedProxy()
    {
        await using ExchangeHttpHost host = await ExchangeHttpHost.StartAsync(await ExchangeAccounts.BuildAsync(),
            new AuthExchangeEndpointOptions { PermitsPerClientPerMinute = 1 },
            new AuthExchangeHostingOptions { TrustedProxies = TrustedProxyNetworks.Loopback });

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.ExchangeAsync("discord", "bad", "2001:db8:1:2::1")).Status);
        // Another address in the same /64 is the same client.
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await host.ExchangeAsync("discord", "bad", "2001:db8:1:2:ffff:ffff:ffff:fffe")).Status);
        // The next /64 is another client.
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.ExchangeAsync("discord", "bad", "2001:db8:1:3::1")).Status);
    }

    [Fact]
    public async Task IPv4Callers_AreEachTheirOwnClient_AndAMappedAddressIsTheSameCaller()
    {
        await using ExchangeHttpHost host = await ExchangeHttpHost.StartAsync(await ExchangeAccounts.BuildAsync(),
            new AuthExchangeEndpointOptions { PermitsPerClientPerMinute = 1 },
            new AuthExchangeHostingOptions { TrustedProxies = TrustedProxyNetworks.Loopback });

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.ExchangeAsync("discord", "bad", "203.0.113.7")).Status);
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await host.ExchangeAsync("discord", "bad", "::ffff:203.0.113.7")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.ExchangeAsync("discord", "bad", "203.0.113.8")).Status);
    }

    [Fact]
    public async Task TheGlobalBound_RefusesPastItsQueue_With429_WhileHealthStillAnswers()
    {
        var blocking = new BlockingValidator();
        await using ExchangeHttpHost host = await ExchangeHttpHost.StartAsync(
            ExchangeFixture.Build(blocking, new InMemoryAccountStore(whitelistOnCreate: true)),
            new AuthExchangeEndpointOptions { PermitsPerClientPerMinute = 100, MaxConcurrentExchanges = 1, MaxQueuedExchanges = 1 });

        Task<ExchangeReply> running = host.ExchangeAsync("discord", "first");
        await blocking.Entered.WaitAsync(Wait);
        // One of these takes the single queue place and the other is refused at once. Which is which depends on
        // arrival order, and the fact holds either way.
        Task<ExchangeReply> second = host.ExchangeAsync("discord", "second");
        Task<ExchangeReply> third = host.ExchangeAsync("discord", "third");
        Task<ExchangeReply> refusedFirst = await Task.WhenAny(second, third).WaitAsync(Wait);
        ExchangeReply refused = await refusedFirst;

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.Status);
        Assert.Empty(refused.Body);
        Assert.True(refused.NoStore);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/healthz")).StatusCode);
        Assert.Equal(1, blocking.Calls);

        blocking.Release();
        Task<ExchangeReply> queued = refusedFirst == second ? third : second;
        Assert.Equal(HttpStatusCode.OK, (await running.WaitAsync(Wait)).Status);
        Assert.Equal(HttpStatusCode.OK, (await queued.WaitAsync(Wait)).Status);
        Assert.Equal(2, blocking.Calls);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesIntoTheExchange_AndFreesItsPlace()
    {
        var blocking = new BlockingValidator();
        var logs = new CapturedLogs();
        await using ExchangeHttpHost host = await ExchangeHttpHost.StartAsync(
            ExchangeFixture.Build(blocking, new InMemoryAccountStore(whitelistOnCreate: true)),
            new AuthExchangeEndpointOptions { PermitsPerClientPerMinute = 100, MaxConcurrentExchanges = 1, MaxQueuedExchanges = 0 },
            logs: logs);

        using var cancel = new CancellationTokenSource();
        Task<ExchangeReply> abandoned = host.ExchangeAsync("discord", "first", ct: cancel.Token);
        await blocking.Entered.WaitAsync(Wait);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        // The request's abort reached the validator through the exchange's linked token.
        await blocking.Cancelled.WaitAsync(Wait);
        await WaitForAsync(() => logs.Lines.Any(l => l.EventName == "AuthExchangeCancelled"));
        // It stayed a cancellation: no outage was logged or answered for a caller who left.
        Assert.DoesNotContain(logs.Lines, l => l.EventName is "AuthExchangeUnavailable" or "AuthExchangeFailed");

        // The single place under the bound was given back, so the next caller is served rather than refused.
        blocking.Release();
        Assert.Equal(HttpStatusCode.OK, (await host.ExchangeAsync("discord", "second")).Status);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not hold in time.");
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Verifies every credential, but only once released. Until then it holds its caller, and it reports a cancellation
    /// it observes.
    /// </summary>
    private sealed class BlockingValidator : IIdentityValidator
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;

        public string ProviderId => "discord";

        public Task Entered => entered.Task;

        public Task Cancelled => cancelled.Task;

        public int Calls => Volatile.Read(ref calls);

        public void Release() => released.TrySetResult();

        public Task<VerifiedIdentity?> ValidateAsync(string credentialToken, CancellationToken ct = default) =>
            throw new NotSupportedException("The exchange must call ValidateDetailedAsync.");

        public async Task<IdentityValidation> ValidateDetailedAsync(string credentialToken, CancellationToken ct = default)
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            try
            {
                await released.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
            return IdentityValidation.Verified(new VerifiedIdentity(credentialToken, "discord", "Wren",
                new System.Collections.Generic.Dictionary<string, string>()));
        }
    }
}
