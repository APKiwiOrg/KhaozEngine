using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Diagnostics;
using KhaozEngine.Http;

namespace KhaozEngine.BugReports;

/// <summary>Finishes and posts a server snapshot off the simulation thread, warning once when posting fails.</summary>
public sealed class ServerSnapshotPoster
{
    readonly HttpClient http;
    readonly Uri relayBase;
    readonly string serviceKey;
    readonly ReportRedactor redactor;

    /// <summary>Creates a poster. The client remains owned by the caller, and a null redactor uses the default rules.</summary>
    /// <param name="http">The client to post with.</param>
    /// <param name="relayBase">An absolute http or https relay base. A missing trailing slash is added.</param>
    /// <param name="serviceKey">The relay service credential, sent in <c>X-Report-Service-Key</c>.</param>
    /// <param name="redactor">The rules applied to section strings before serialization.</param>
    public ServerSnapshotPoster(HttpClient http, Uri relayBase, string serviceKey, ReportRedactor? redactor = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(relayBase);
        ArgumentNullException.ThrowIfNull(serviceKey);
        if (!relayBase.IsAbsoluteUri || (relayBase.Scheme != Uri.UriSchemeHttp && relayBase.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("The relay base must be an absolute http or https address.", nameof(relayBase));

        var builder = new UriBuilder(relayBase);
        if (!builder.Path.EndsWith('/')) builder.Path += "/";
        this.http = http;
        this.relayBase = builder.Uri;
        this.serviceKey = serviceKey;
        this.redactor = redactor ?? new ReportRedactor();
    }

    /// <summary>The shared relay retry policy, overridable for tests or a host's transport requirements.</summary>
    public HttpRetryPolicy RetryPolicy { get; init; } = HttpRelaySink.DefaultPolicy;

    /// <summary>Redacts, serializes and posts off-thread. Returns false on every failure except caller cancellation.</summary>
    public async Task<bool> PostAsync(ServerSnapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<CapturedSection> finished = BugReportSectionSet.Finish(
                    snapshot.Sections, redactor, BugReportSectionSet.DefaultFinishBudget,
                    BugReportLimits.MaxServerSnapshotBytes - BugReportLimits.ServerSnapshotEnvelopeReserveBytes);
                var sections = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (CapturedSection section in finished)
                {
                    if (!BundleEntryNames.IsValidSectionKey(section.Key) || sections.ContainsKey(section.Key))
                        return Fail("invalid or duplicate section key");
                    using JsonDocument document = JsonDocument.Parse(section.Json, BugReportJson.SectionDocumentOptions);
                    sections.Add(section.Key, document.RootElement.Clone());
                }

                byte[] body = BugReportJson.SerializeServerSnapshot(new ServerSnapshotWire(
                    BugReportFormat.Version, snapshot.ReportId, snapshot.Subject, snapshot.CapturedAt, sections));
                if (body.Length > BugReportLimits.MaxServerSnapshotBytes)
                    return Fail("snapshot exceeds its byte limit");

                var route = new Uri(relayBase, RelayRoutes.ServerSnapshot(snapshot.ReportId));
                HttpRequestMessage NewRequest()
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = new ByteArrayContent(body) };
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    request.Headers.Add(RelayHeaders.ServiceKey, serviceKey);
                    return request;
                }

                using HttpResponseMessage response = await HttpRetry.SendAsync(http, NewRequest, RetryPolicy, cancellationToken).ConfigureAwait(false);
                return response.IsSuccessStatusCode || Fail("HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Fail("processing or transport failure", exception);
        }
    }

    bool Fail(string reason, Exception? exception = null)
    {
        var text = new StringBuilder("Bug report server snapshot was not posted: ").Append(reason);
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            string detail;
            try { detail = current.Message; }
            catch (Exception) { detail = ReportRedactor.Mask; }
            text.Append(" -> ").Append(current.GetType().FullName).Append(": ").Append(detail);
        }
        string message = redactor.Redact(text.Append('.').ToString());
        if (serviceKey.Length != 0)
            message = message.Replace(serviceKey, ReportRedactor.Mask, StringComparison.Ordinal);
        Log.Warn(message);
        return false;
    }
}
