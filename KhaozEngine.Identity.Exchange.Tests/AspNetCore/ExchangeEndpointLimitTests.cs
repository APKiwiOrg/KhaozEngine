using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
/// grouping, the global bound and its queue, a slow upload that holds no place under it, a health probe that none of
/// them starve, and caller cancellation.
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
        // The fixed window reports the whole window on a refusal, not the time left in it, so this is exactly 60.
        Assert.Equal(TimeSpan.FromMinutes(1), refused.RetryAfter);
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
    public async Task ASlowUpload_HoldsNoPlaceUnderTheGlobalBound()
    {
        // One place and no queue. Were the body read under the bound, the upload that never finishes would hold the
        // only place and the complete request behind it would be refused.
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ExchangeHttpHost host = await ExchangeHttpHost.StartAsync(await ExchangeAccounts.BuildAsync(),
            new AuthExchangeEndpointOptions { PermitsPerClientPerMinute = 100, MaxConcurrentExchanges = 1, MaxQueuedExchanges = 0 },
            middleware: (context, next) =>
            {
                if (context.Request.Headers.ContainsKey(SlowUploadHeader))
                    context.Request.Body = new FirstReadSignal(context.Request.Body, reading);
                return next(context);
            });

        var neverEnds = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var abandon = new CancellationTokenSource();
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/auth/exchange") { Content = new NeverEndingBody(neverEnds.Task) };
        upload.Headers.TransferEncodingChunked = true;
        upload.Headers.Add(SlowUploadHeader, "1");
        Task<HttpResponseMessage> slow = host.Client.SendAsync(upload, abandon.Token);
        try
        {
            // The handler has started reading the slow body, so any place it takes before the read, it holds now.
            await reading.Task.WaitAsync(Wait);

            ExchangeReply complete = await host.ExchangeAsync("discord", ExchangeAccounts.OkCredential).WaitAsync(Wait);

            Assert.Equal(HttpStatusCode.OK, complete.Status);
            Assert.False(slow.IsCompleted);
        }
        finally
        {
            await abandon.CancelAsync();
            neverEnds.TrySetResult();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow);
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

    private const string SlowUploadHeader = "X-Test-Slow-Upload";

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

    /// <summary>A chunked body that sends the start of a request and then nothing, until the test lets it end.</summary>
    private sealed class NeverEndingBody(Task end) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken cancellationToken)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"provider\":\"discord\","), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await end.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>The request body, unchanged, except that the handler's first read of it is reported.</summary>
    private sealed class FirstReadSignal(Stream inner, TaskCompletionSource reading) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            reading.TrySetResult();
            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("The handler reads the body asynchronously.");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
