using System;
using Microsoft.Extensions.Logging;

namespace KhaozEngine.Identity.Exchange.AspNetCore;

/// <summary>
/// Every line the exchange endpoint writes. The parameters are the whole of what reaches a log: an outcome and a cause
/// drawn from fixed sets, a registered provider id, a status code, a duration, and on a fault the exception. There is
/// deliberately no parameter a credential, a session token, a subject, a display name or a client address could ride.
/// </summary>
internal static partial class ExchangeLog
{
    /// <summary>What <c>Provider</c> reads when the request named no registered provider.</summary>
    internal const string NoProvider = "none";

    [LoggerMessage(EventId = 1, EventName = "AuthExchangeAnswered", Level = LogLevel.Information,
        Message = "Auth exchange {Outcome} ({Cause}), provider {Provider}, HTTP {Status} in {ElapsedMs} ms")]
    internal static partial void Answered(ILogger logger, string outcome, string cause, string provider, int status,
        long elapsedMs);

    [LoggerMessage(EventId = 2, EventName = "AuthExchangeUnavailable", Level = LogLevel.Warning,
        Message = "Auth exchange Unavailable ({Cause}), provider {Provider}, HTTP {Status} in {ElapsedMs} ms")]
    internal static partial void Unavailable(ILogger logger, string cause, string provider, int status, long elapsedMs,
        Exception? fault);

    [LoggerMessage(EventId = 3, EventName = "AuthExchangeFailed", Level = LogLevel.Error,
        Message = "Auth exchange failed unexpectedly, answering HTTP {Status} in {ElapsedMs} ms")]
    internal static partial void Failed(ILogger logger, int status, long elapsedMs, Exception fault);

    // Debug, not Information: a flood is exactly when these arrive fastest, and a line per refusal would turn the rate
    // limit into a log-volume amplifier.
    [LoggerMessage(EventId = 4, EventName = "AuthExchangeRateLimited", Level = LogLevel.Debug,
        Message = "Auth exchange refused by the {Limit}, HTTP {Status}")]
    internal static partial void RateLimited(ILogger logger, string limit, int status);

    [LoggerMessage(EventId = 5, EventName = "AuthExchangeCancelled", Level = LogLevel.Debug,
        Message = "Auth exchange cancelled by the caller after {ElapsedMs} ms")]
    internal static partial void Cancelled(ILogger logger, long elapsedMs);

    [LoggerMessage(EventId = 6, EventName = "AuthExchangeForwardedHeadersOff", Level = LogLevel.Warning,
        Message = "Forwarded headers are not configured, so the auth exchange at {Pattern} limits each client by the " +
            "connection's peer address (HttpContext.Connection.RemoteIpAddress). Behind a proxy every caller shares the " +
            "proxy's bucket. Name the proxies in AddAuthExchangeHosting.")]
    internal static partial void ForwardedHeadersOff(ILogger logger, string pattern);
}
