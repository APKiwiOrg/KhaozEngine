using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace KhaozEngine.Identity.Exchange.AspNetCore;

/// <summary>
/// One mapped exchange endpoint: its two limiters, its body cap and its log. Created when the endpoint is mapped and
/// disposed when the application stops.
/// </summary>
/// <remarks>
/// The order per request is fixed, and each step is cheaper than the next:
/// <list type="number">
/// <item>The per-client fixed window, keyed by <see cref="AuthExchangeClientKey"/>. A refusal is 429 with
/// <c>Retry-After</c>, and nothing else runs.</item>
/// <item>The bounded body read. Over the cap is 413. The body is read BEFORE the global bound, so a caller trickling a
/// body in slowly holds a connection and never a place under the bound, which is reserved for work.</item>
/// <item>The global concurrency bound with its oldest-first queue. A full queue is 429.</item>
/// <item>The parse with the fixed wire options. Not a request object is a bare 400.</item>
/// <item>The exchange, under the caller's cancellation, and the mapped answer.</item>
/// </list>
/// A flood is refused before any JSON is parsed, and none of it depends on the host calling <c>UseRateLimiter</c> or
/// leaving its global limiter alone.
/// </remarks>
internal sealed class AuthExchangeHandler : IDisposable
{
    private readonly AuthExchange exchange;
    private readonly AuthExchangeEndpointOptions options;
    private readonly ILogger logger;
    private readonly PartitionedRateLimiter<string> perClient;
    private readonly ConcurrencyLimiter global;

    public AuthExchangeHandler(AuthExchange exchange, AuthExchangeEndpointOptions options, ILogger logger)
    {
        this.exchange = exchange;
        this.options = options;
        this.logger = logger;
        int permits = options.PermitsPerClientPerMinute;
        perClient = PartitionedRateLimiter.Create<string, string>(client => RateLimitPartition.GetFixedWindowLimiter(
            client, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
        global = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = options.MaxConcurrentExchanges,
            QueueLimit = options.MaxQueuedExchanges,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    }

    public async Task<IResult> HandleAsync(HttpContext context)
    {
        long started = Stopwatch.GetTimestamp();
        CancellationToken ct = context.RequestAborted;

        string client = AuthExchangeClientKey.For(context.Connection.RemoteIpAddress, options.Ipv6PartitionPrefixLength);
        using RateLimitLease clientLease = perClient.AttemptAcquire(client);
        if (!clientLease.IsAcquired)
        {
            ExchangeLog.RateLimited(logger, "per-client window", StatusCodes.Status429TooManyRequests);
            return ExchangeHttpResults.Bare(StatusCodes.Status429TooManyRequests,
                clientLease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan wait) ? wait : null);
        }

        try
        {
            using ExchangeRequestBody body = await ExchangeRequestBody.ReadAsync(context.Request,
                options.MaxRequestBodyBytes, ct).ConfigureAwait(false);
            if (body.Status == BodyReadStatus.TooLarge)
                return AnswerBare(started, StatusCodes.Status413PayloadTooLarge, "BodyTooLarge");
            if (body.Status != BodyReadStatus.Read)
                return AnswerBare(started, StatusCodes.Status400BadRequest, "UnreadableBody");

            using RateLimitLease globalLease = await global.AcquireAsync(1, ct).ConfigureAwait(false);
            if (!globalLease.IsAcquired)
            {
                ExchangeLog.RateLimited(logger, "global bound", StatusCodes.Status429TooManyRequests);
                return ExchangeHttpResults.Bare(StatusCodes.Status429TooManyRequests);
            }

            AuthExchangeRequest? request = body.Parse();
            body.Dispose();
            if (request is null)
                return AnswerBare(started, StatusCodes.Status400BadRequest, "UnparseableBody");

            AuthExchangeResult result = await exchange.ExchangeAsync(request.Provider, request.AccessToken, ct)
                .ConfigureAwait(false);
            Log(started, result, request.Provider);
            return AuthExchangeEndpoints.ToHttpResult(result, options.IncludeBanDetails);
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is OperationCanceledException or IOException)
        {
            // The caller went away. There is nobody to answer, and turning it into a 503 would log an outage that did
            // not happen, so the cancellation propagates to the server, which already knows the request was aborted.
            ExchangeLog.Cancelled(logger, ElapsedMs(started));
            throw;
        }
        catch (Exception ex)
        {
            // The exchange turns every dependency failure into an outcome, so this is a defect. It still answers the one
            // failure envelope rather than whatever the host's exception handler would write.
            ExchangeLog.Failed(logger, StatusCodes.Status503ServiceUnavailable, ElapsedMs(started), ex);
            return AuthExchangeEndpoints.Unavailable();
        }
    }

    public void Dispose()
    {
        perClient.Dispose();
        global.Dispose();
    }

    private IResult AnswerBare(long started, int status, string cause)
    {
        ExchangeLog.Answered(logger, nameof(AuthExchangeOutcome.Malformed), cause, ExchangeLog.NoProvider, status,
            ElapsedMs(started));
        return ExchangeHttpResults.Bare(status);
    }

    private void Log(long started, AuthExchangeResult result, string? provider)
    {
        // The provider id is caller input until the exchange matched it against a registered validator (ordinal), so
        // it reaches the log only once it is known to be one of the game's own ids.
        string loggedProvider = result.Cause == AuthExchangeCause.UnknownProvider || provider is null
            ? ExchangeLog.NoProvider
            : provider;
        int status = AuthExchangeEndpoints.StatusCodeFor(result.Outcome);
        if (result.Outcome == AuthExchangeOutcome.Unavailable)
            ExchangeLog.Unavailable(logger, result.Cause.ToString(), loggedProvider, status, ElapsedMs(started), result.Fault);
        else
            ExchangeLog.Answered(logger, result.Outcome.ToString(), result.Cause.ToString(), loggedProvider, status,
                ElapsedMs(started));
    }

    private static long ElapsedMs(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
