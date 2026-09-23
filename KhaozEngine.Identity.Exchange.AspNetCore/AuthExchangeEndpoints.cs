using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KhaozEngine.Identity.Exchange.AspNetCore;

/// <summary>
/// Mounts <c>POST /auth/exchange</c> over a game's <see cref="AuthExchange"/>, and the public mapping from an exchange
/// result to its HTTP answer, for a game that maps a route of its own over the core.
/// </summary>
/// <remarks>
/// <para>The mapping, which is the design's and both games' today:</para>
/// <list type="table">
/// <listheader><term>Outcome</term><description>Answer</description></listheader>
/// <item><term><see cref="AuthExchangeOutcome.Ok"/></term><description>200, <c>ok</c> with the token, its expiry, the
/// subject and the display name.</description></item>
/// <item><term><see cref="AuthExchangeOutcome.NotWhitelisted"/></term><description>403, <c>not_whitelisted</c> with the
/// subject and the display name.</description></item>
/// <item><term><see cref="AuthExchangeOutcome.Banned"/></term><description>403, <c>banned</c> with the subject and the
/// display name, and the ban's reason and expiry only when ban details are on.</description></item>
/// <item><term><see cref="AuthExchangeOutcome.InvalidCredential"/></term><description>401,
/// <c>invalid_credential</c>.</description></item>
/// <item><term><see cref="AuthExchangeOutcome.Unavailable"/></term><description>503, <c>unavailable</c>, whichever
/// dependency failed.</description></item>
/// <item><term><see cref="AuthExchangeOutcome.Malformed"/></term><description>400 with no body.</description></item>
/// </list>
/// <para>
/// The mapped endpoint adds a body over the cap (413, no body), an unparseable body (400, no body) and a rate-limit
/// refusal (429, no body, <c>Retry-After</c> when the limiter knows it). A client turns the bare 429 into
/// <see cref="AuthExchangeStatuses.RetryLater"/>. Every answer carries <c>Cache-Control: no-store</c>, and none carries a
/// CORS header or a cookie.
/// </para>
/// </remarks>
public static class AuthExchangeEndpoints
{
    /// <summary>
    /// Maps the exchange endpoint. It takes the <see cref="HttpContext"/> rather than a bound body, so its per-client
    /// window, its global bound and its body cap all run before a byte of JSON is parsed, and it reads and writes the
    /// wire with fixed web-default JSON options a host's settings cannot change.
    /// </summary>
    /// <remarks>
    /// The limiters are created here, one pair per mapped endpoint, and disposed when the application stops. If
    /// forwarded headers are not configured, a warning at mapping time says the per-client window partitions on the
    /// connection's peer address, which behind a proxy is the proxy for every caller.
    /// </remarks>
    /// <param name="routes">The application or a route group.</param>
    /// <param name="exchange">The game's composed exchange. One instance serves every request.</param>
    /// <param name="options">The route, the bounds and the ban-details switch, or <c>null</c> for the defaults.</param>
    /// <returns>The endpoint's builder, for the host's own conventions.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="routes"/> or <paramref name="exchange"/> is null.</exception>
    /// <exception cref="ArgumentException">An option is out of range.</exception>
    public static RouteHandlerBuilder MapAuthExchange(this IEndpointRouteBuilder routes, AuthExchange exchange,
        AuthExchangeEndpointOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(exchange);
        options ??= new AuthExchangeEndpointOptions();
        options.Validate();

        IServiceProvider services = routes.ServiceProvider;
        ILogger logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(AuthExchangeEndpoints).FullName!)
            ?? NullLogger.Instance;
        ForwardedHeaders forwarded = services.GetService<IOptions<ForwardedHeadersOptions>>()?.Value.ForwardedHeaders
            ?? ForwardedHeaders.None;
        if ((forwarded & ForwardedHeaders.XForwardedFor) == 0)
            ExchangeLog.ForwardedHeadersOff(logger, options.Pattern);

        var handler = new AuthExchangeHandler(exchange, options, logger);
        services.GetService<IHostApplicationLifetime>()?.ApplicationStopped.Register(handler.Dispose);

        return routes.MapPost(options.Pattern, (Func<HttpContext, Task<IResult>>)handler.HandleAsync)
            .WithMetadata(new BodySizeLimit(options.MaxRequestBodyBytes));
    }

    /// <summary>
    /// The HTTP answer for <paramref name="result"/>, per the table above, with <c>Cache-Control: no-store</c>. For a game
    /// that maps its own route over <see cref="AuthExchange"/> or over <see cref="AuthAdmission"/>, so the two answer
    /// alike.
    /// </summary>
    /// <param name="result">What the exchange decided.</param>
    /// <param name="includeBanDetails">Whether a banned caller is told the ban's reason and expiry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The outcome is not a defined one.</exception>
    public static IResult ToHttpResult(AuthExchangeResult result, bool includeBanDetails)
    {
        ArgumentNullException.ThrowIfNull(result);
        int status = StatusCodeFor(result.Outcome);
        // Malformed has no envelope and ToResponse refuses it, so it never reaches the serializer.
        return result.Outcome == AuthExchangeOutcome.Malformed
            ? ExchangeHttpResults.Bare(status)
            : ExchangeHttpResults.Envelope(result.ToResponse(includeBanDetails), status);
    }

    /// <summary>
    /// The one answer for everything that means "nothing about this account was decided, try again": 503 with the
    /// <c>unavailable</c> envelope and <c>Cache-Control: no-store</c>.
    /// </summary>
    public static IResult Unavailable() =>
        ExchangeHttpResults.Envelope(new AuthExchangeResponse(AuthExchangeStatuses.Unavailable),
            StatusCodes.Status503ServiceUnavailable);

    /// <summary>The HTTP status an outcome answers with, per the table above.</summary>
    /// <param name="outcome">The exchange outcome.</param>
    /// <exception cref="ArgumentOutOfRangeException">The outcome is not a defined one.</exception>
    public static int StatusCodeFor(AuthExchangeOutcome outcome) => outcome switch
    {
        AuthExchangeOutcome.Ok => StatusCodes.Status200OK,
        AuthExchangeOutcome.NotWhitelisted => StatusCodes.Status403Forbidden,
        AuthExchangeOutcome.Banned => StatusCodes.Status403Forbidden,
        AuthExchangeOutcome.InvalidCredential => StatusCodes.Status401Unauthorized,
        AuthExchangeOutcome.Unavailable => StatusCodes.Status503ServiceUnavailable,
        AuthExchangeOutcome.Malformed => StatusCodes.Status400BadRequest,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not a defined exchange outcome."),
    };

    // The endpoint's body cap as routing metadata: the routing middleware lowers the server's per-request limit to it
    // when this endpoint is selected, before the handler runs.
    private sealed class BodySizeLimit(long bytes) : IRequestSizeLimitMetadata
    {
        public long? MaxRequestBodySize => bytes;
    }
}
