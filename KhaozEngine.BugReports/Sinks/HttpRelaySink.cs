using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Http;

namespace KhaozEngine.BugReports;

/// <summary>
/// Posts a bundle to the bug report relay's intake route as <c>application/zip</c>, with the player's bearer token
/// and the report id as the idempotency key. Every outcome maps to a result:
/// a 2xx is Sent with the relay's reference, or <see cref="ReportIds.ReferenceCode"/> when the body is unreadable.
/// No token, a 401, a 429, a 408 or 5xx after retries and a transport fault after retries are Queued, so the
/// bundle stays for a later flush. Any other 4xx is Rejected with the relay's error text.
/// </summary>
public sealed class HttpRelaySink : IBugReportSink
{
    const string ZipMediaType = "application/zip";

    readonly HttpClient http;
    readonly Uri intake;
    readonly Func<string?> bearerToken;

    /// <summary>Creates a sink for one relay.</summary>
    /// <param name="http">The client to send with. Not disposed by the sink.</param>
    /// <param name="relayBase">The relay's absolute base address. A missing trailing slash is added.</param>
    /// <param name="bearerToken">Reads the current session token on each submit. Null or blank queues without a request.</param>
    public HttpRelaySink(HttpClient http, Uri relayBase, Func<string?> bearerToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(relayBase);
        ArgumentNullException.ThrowIfNull(bearerToken);
        if (!relayBase.IsAbsoluteUri)
            throw new ArgumentException("The relay base must be an absolute address.", nameof(relayBase));

        var builder = new UriBuilder(relayBase);
        if (!builder.Path.EndsWith('/'))
            builder.Path += "/";
        this.http = http;
        intake = new Uri(builder.Uri, RelayRoutes.Intake);
        this.bearerToken = bearerToken;
    }

    /// <summary>Three attempts of up to 60 seconds each, retrying 408 and 5xx only.</summary>
    public static HttpRetryPolicy DefaultPolicy { get; } = HttpRetryPolicy.Default with
    {
        MaxAttempts = 3,
        PerAttemptTimeout = TimeSpan.FromSeconds(60),
        IsRetryableStatus = status => status == HttpStatusCode.RequestTimeout || (int)status is >= 500 and <= 599,
    };

    /// <summary>The retry policy for each submit. Defaults to <see cref="DefaultPolicy"/>.</summary>
    public HttpRetryPolicy RetryPolicy { get; init; } = DefaultPolicy;

    /// <inheritdoc />
    public async Task<BugReportSubmitResult> SubmitAsync(BugReportSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (!ReportIds.IsValid(submission.ReportId))
            throw new ArgumentException("A canonical version 7 report id is required.", nameof(submission));
        cancellationToken.ThrowIfCancellationRequested();

        string? token = bearerToken();
        if (string.IsNullOrWhiteSpace(token))
            return Queued("no_token");

        ArraySegment<byte> body = MemoryMarshal.TryGetArray(submission.Bundle, out ArraySegment<byte> segment)
            ? segment
            : new ArraySegment<byte>(submission.Bundle.ToArray());

        HttpRequestMessage NewRequest()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, intake)
            {
                Content = new ByteArrayContent(body.Array!, body.Offset, body.Count),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(ZipMediaType);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add(RelayHeaders.IdempotencyKey, submission.ReportId);
            return request;
        }

        HttpResponseMessage response;
        try
        {
            response = await HttpRetry.SendAsync(http, NewRequest, RetryPolicy, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransportFault(ex, cancellationToken))
        {
            return Queued("network");
        }

        using (response)
            return await MapAsync(response, submission.ReportId, cancellationToken).ConfigureAwait(false);
    }

    static async Task<BugReportSubmitResult> MapAsync(HttpResponseMessage response, string reportId, CancellationToken cancellationToken)
    {
        int status = (int)response.StatusCode;
        string statusError = "http_" + status.ToString(CultureInfo.InvariantCulture);

        if (response.IsSuccessStatusCode)
        {
            byte[]? body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            string? reference = body is null ? null : TryParse(body, static json => BugReportJson.DeserializeIntakeAccepted(json).Reference);
            return new BugReportSubmitResult(BugReportSubmitStatus.Sent,
                string.IsNullOrWhiteSpace(reference) ? ReportIds.ReferenceCode(reportId) : reference, null, null);
        }

        if (status == 429)
            return new BugReportSubmitResult(BugReportSubmitStatus.Queued, null, statusError, RetryAfter(response.Headers.RetryAfter));

        // 401 means the token is missing or expired, so the next sign-in can still deliver it.
        if (status is >= 400 and <= 499 && status is not (401 or 408))
        {
            byte[]? body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            string? error = body is null ? null : TryParse(body, static json => BugReportJson.DeserializeRelayError(json).Error);
            return new BugReportSubmitResult(BugReportSubmitStatus.Rejected, null,
                string.IsNullOrWhiteSpace(error) ? statusError : error, null);
        }

        return Queued(statusError);
    }

    static BugReportSubmitResult Queued(string error) => new(BugReportSubmitStatus.Queued, null, error, null);

    static TimeSpan? RetryAfter(RetryConditionHeaderValue? header)
    {
        if (header?.Delta is TimeSpan delta)
            return delta;
        if (header?.Date is DateTimeOffset date)
        {
            TimeSpan wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    static async Task<byte[]?> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException || IsTransportFault(ex, cancellationToken))
        {
            return null;
        }
    }

    static string? TryParse(byte[] body, Func<byte[], string> read)
    {
        try
        {
            return read(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A caller cancellation is never a fault. HttpRetry rethrows the final attempt's transport fault, which is an
    // HttpRequestException or a per-attempt timeout's cancellation.
    static bool IsTransportFault(Exception ex, CancellationToken cancellationToken) =>
        (ex is HttpRequestException or OperationCanceledException) && !cancellationToken.IsCancellationRequested;
}
