using System;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace KhaozEngine.Identity.Exchange.AspNetCore;

/// <summary>
/// The two response shapes the exchange sends, each carrying <c>Cache-Control: no-store</c> itself, so a game that maps
/// its own route over <see cref="AuthExchangeEndpoints.ToHttpResult"/> gets the header without remembering it.
/// </summary>
internal static class ExchangeHttpResults
{
    /// <summary>
    /// The fixed wire options: web defaults, so camelCase names and every member written, null included. Fixed here so
    /// a host's global JSON settings cannot change the wire.
    /// </summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static IResult Envelope(AuthExchangeResponse response, int statusCode) =>
        new NoStoreResult(Results.Json(response, Json, contentType: null, statusCode: statusCode));

    internal static IResult Bare(int statusCode, TimeSpan? retryAfter = null) => new BareResult(statusCode, retryAfter);

    private static void MarkNoStore(HttpContext context) => context.Response.Headers.CacheControl = "no-store";

    private sealed class NoStoreResult(IResult inner) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            MarkNoStore(httpContext);
            return inner.ExecuteAsync(httpContext);
        }
    }

    // A status with no body at all. Status code pages are switched off for the response, so a host that runs that
    // middleware cannot hang a problem document on a 400, a 413 or a 429 the design says carries nothing.
    private sealed class BareResult(int statusCode, TimeSpan? retryAfter) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            if (httpContext.Features.Get<IStatusCodePagesFeature>() is { } pages) pages.Enabled = false;
            MarkNoStore(httpContext);
            if (retryAfter is { } wait)
            {
                long seconds = Math.Max(1, (long)Math.Ceiling(wait.TotalSeconds));
                httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            }
            httpContext.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        }
    }
}
