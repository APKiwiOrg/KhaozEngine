using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Core;

[Collection("LoggingSerial")]
public sealed class ServerSnapshotPosterTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 17, 3, 4, 5, TimeSpan.Zero);

    static RawSection Section(string key = "world", string json = "{\"tick\":42}", int maxBytes = 1024) =>
        new(key, maxBytes, Encoding.UTF8.GetBytes(json), IsError: false);

    static ServerSnapshot Snapshot(params RawSection[] sections) => new(TestBundles.ReportId, "discord:42", Now, sections);

    static ServerSnapshotPoster Poster(HttpClient http, string relayBase = "https://relay.example/") =>
        new(http, new Uri(relayBase), "k")
        {
            RetryPolicy = HttpRelaySink.DefaultPolicy with { Backoff = _ => TimeSpan.Zero },
        };

    readonly InMemorySink logs = new();

    IDisposable CaptureLogs(out LogManager manager)
    {
        var options = new LoggerOptions { Synchronous = true };
        options.Sinks.Add(logs);
        manager = new LogManager(options);
        return Log.Use(manager);
    }

    void AssertOneWarning() => Assert.Equal(LogLevel.Warn, Assert.Single(logs.Entries).Level);

    [Theory]
    [InlineData("https://relay.example/", "https://relay.example/bug-reports/")]
    [InlineData("https://relay.example/api", "https://relay.example/api/bug-reports/")]
    [InlineData("http://relay.example/api/", "http://relay.example/api/bug-reports/")]
    public async Task Poster_posts_wire_json_with_service_key(string relayBase, string prefix)
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.Accepted);
        using var observer = new ObservingHandler(handler);
        using var http = new HttpClient(observer);

        Assert.True(await Poster(http, relayBase).PostAsync(Snapshot(Section()), CancellationToken.None));

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(prefix + TestBundles.ReportId + "/server", request.Uri?.AbsoluteUri);
        Assert.Equal("k", observer.ServiceKey);
        Assert.Equal("application/json", request.ContentType);
        ServerSnapshotWire wire = BugReportJson.DeserializeServerSnapshot(request.Body);
        Assert.Equal(BugReportFormat.Version, wire.FormatVersion);
        Assert.Equal(TestBundles.ReportId, wire.ReportId);
        Assert.Equal("discord:42", wire.Subject);
        Assert.Equal(Now, wire.CapturedAt);
        Assert.Equal(42, Assert.Single(wire.Sections).Value.GetProperty("tick").GetInt32());
        Assert.True(wire.Sections.ContainsKey("world"));
    }

    [Fact]
    public async Task Poster_serializes_off_the_calling_thread()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.Accepted);
        using var observer = new ObservingHandler(handler);
        using var http = new HttpClient(observer);
        int callerThread = 0;
        int finishThread = 0;
        var snapshot = Snapshot() with { Sections = new CallbackSections(() => finishThread = Environment.CurrentManagedThreadId) };
        var completion = new TaskCompletionSource<Task<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            try { completion.SetResult(Poster(http).PostAsync(snapshot, CancellationToken.None)); }
            catch (Exception exception) { completion.SetException(exception); }
        });

        caller.Start();
        Assert.True(await await completion.Task);

        Assert.NotEqual(0, finishThread);
        Assert.NotEqual(callerThread, finishThread);
        Assert.NotEqual(callerThread, observer.ThreadId);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Poster_redacts_sections_before_posting()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.Accepted);
        using var http = new HttpClient(handler);

        Assert.True(await Poster(http).PostAsync(Snapshot(Section(json: "{\"token\":\"Bearer abcdefgh12\"}")), CancellationToken.None));

        string body = Encoding.UTF8.GetString(Assert.Single(handler.Requests).Body);
        Assert.Contains("[redacted]", body);
        Assert.DoesNotContain("abcdefgh12", body);
    }

    [Fact]
    public async Task Poster_uses_the_injected_redactor()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.Accepted);
        using var http = new HttpClient(handler);
        var poster = new ServerSnapshotPoster(http, new Uri("https://relay.example/"), "k", new ReportRedactor("/server/private"));

        Assert.True(await poster.PostAsync(Snapshot(Section(json: "{\"path\":\"/server/private/world\"}")), CancellationToken.None));

        ServerSnapshotWire wire = BugReportJson.DeserializeServerSnapshot(Assert.Single(handler.Requests).Body);
        Assert.Equal("~/world", wire.Sections["world"].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Poster_finishes_and_truncates_sections_before_serializing()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.Accepted);
        using var http = new HttpClient(handler);
        RawSection raw = Section(json: "{\"tick\":42,\"large\":\"" + new string('x', 200) + "\"}", maxBytes: 64);

        Assert.True(await Poster(http).PostAsync(Snapshot(raw), CancellationToken.None));

        ServerSnapshotWire wire = BugReportJson.DeserializeServerSnapshot(Assert.Single(handler.Requests).Body);
        Assert.True(wire.Sections["world"].GetProperty("truncated").GetBoolean());
        Assert.True(Encoding.UTF8.GetByteCount(wire.Sections["world"].GetRawText()) <= 64);
    }

    [Fact]
    public async Task Poster_failure_returns_false_with_a_warning()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.InternalServerError)
            .Respond(HttpStatusCode.InternalServerError).Respond(HttpStatusCode.InternalServerError);
        using var http = new HttpClient(handler);
        using IDisposable scope = CaptureLogs(out LogManager manager);
        using var ownedManager = manager;

        Assert.False(await Poster(http).PostAsync(Snapshot(), CancellationToken.None));

        Assert.Equal(3, handler.Requests.Count);
        AssertOneWarning();
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, 3)]
    [InlineData(HttpStatusCode.Unauthorized, 1)]
    [InlineData(HttpStatusCode.Forbidden, 1)]
    [InlineData((HttpStatusCode)429, 1)]
    public async Task Poster_uses_the_shared_retry_status_policy(HttpStatusCode status, int attempts)
    {
        var handler = new FakeHttpHandler().Respond(status).Respond(status).Respond(status);
        using var http = new HttpClient(handler);
        using IDisposable scope = CaptureLogs(out LogManager manager);
        using var ownedManager = manager;

        Assert.False(await Poster(http).PostAsync(Snapshot(), CancellationToken.None));

        Assert.Equal(attempts, handler.Requests.Count);
        AssertOneWarning();
    }

    [Fact]
    public void Poster_reuses_the_shared_default_policy()
    {
        using var http = new HttpClient();
        var poster = new ServerSnapshotPoster(http, new Uri("https://relay.example/"), "k");

        Assert.Same(HttpRelaySink.DefaultPolicy, poster.RetryPolicy);
    }

    [Fact]
    public async Task Oversized_snapshot_is_not_posted()
    {
        var handler = new FakeHttpHandler();
        using var http = new HttpClient(handler);
        using IDisposable scope = CaptureLogs(out LogManager manager);
        using var ownedManager = manager;
        var snapshot = Snapshot(Section()) with { Subject = new string('s', BugReportLimits.MaxServerSnapshotBytes) };

        Assert.False(await Poster(http).PostAsync(snapshot, CancellationToken.None));

        Assert.Empty(handler.Requests);
        AssertOneWarning();
    }

    [Fact]
    public async Task Snapshot_whose_sections_fill_the_budget_is_posted()
    {
        const int SectionBytes = 256 * 1024;
        const int LastSectionBytes = 240 * 1024;
        string json = "{\"large\":" + new string('1', SectionBytes - 10) + "}";
        // The truncated final section costs 27 framing bytes, plus this numeric leaf.
        string lastJson = "{\"large\":" + new string('1', LastSectionBytes - 27)
            + ",\"overflow\":" + new string('1', 16 * 1024) + "}";
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.Accepted);
        using var http = new HttpClient(handler);
        using IDisposable scope = CaptureLogs(out LogManager manager);
        using var ownedManager = manager;
        var snapshot = Snapshot(Section("one", json, SectionBytes), Section("two", json, SectionBytes),
            Section("three", json, SectionBytes), Section("four", lastJson, SectionBytes), Section("dropped"));

        Assert.True(await Poster(http).PostAsync(snapshot, CancellationToken.None));

        byte[] body = Assert.Single(handler.Requests).Body;
        Assert.True(body.Length <= BugReportLimits.MaxServerSnapshotBytes);
        ServerSnapshotWire wire = BugReportJson.DeserializeServerSnapshot(body);
        Assert.Equal(4, wire.Sections.Count);
        Assert.Equal(1008 * 1024, wire.Sections.Values.Sum(section => Encoding.UTF8.GetByteCount(section.GetRawText())));
        Assert.True(wire.Sections["four"].GetProperty("truncated").GetBoolean());
        Assert.False(wire.Sections["four"].TryGetProperty("overflow", out _));
        AssertOneWarning();
        Assert.Contains("dropped", Assert.Single(logs.Entries).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../world")]
    [InlineData("World")]
    [InlineData("")]
    [InlineData("world/world")]
    public async Task Invalid_section_key_is_not_posted(string key)
    {
        var handler = new FakeHttpHandler();
        using var http = new HttpClient(handler);
        using IDisposable scope = CaptureLogs(out LogManager manager);
        using var ownedManager = manager;

        Assert.False(await Poster(http).PostAsync(Snapshot(Section(key)), CancellationToken.None));

        Assert.Empty(handler.Requests);
        AssertOneWarning();
    }

    [Fact]
    public async Task Duplicate_section_key_is_not_posted()
    {
        var handler = new FakeHttpHandler();
        using var http = new HttpClient(handler);
        using IDisposable scope = CaptureLogs(out LogManager manager);
        using var ownedManager = manager;

        Assert.False(await Poster(http).PostAsync(Snapshot(Section(), Section()), CancellationToken.None));

        Assert.Empty(handler.Requests);
        AssertOneWarning();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Finish_fault_is_a_failure_even_if_the_caller_cancels(bool cancel)
    {
        using var caller = new CancellationTokenSource();
        var handler = new FakeHttpHandler();
        using var http = new HttpClient(handler);
        using IDisposable scope = CaptureLogs(out LogManager manager);
        using var ownedManager = manager;
        var snapshot = Snapshot() with
        {
            Sections = new CallbackSections(() =>
            {
                if (cancel) caller.Cancel();
                throw new InvalidOperationException("finish failed");
            }),
        };

        Assert.False(await Poster(http).PostAsync(snapshot, caller.Token));

        Assert.Empty(handler.Requests);
        AssertOneWarning();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http_fault_returns_false_with_one_warning(bool cancellationException)
    {
        Exception exception = cancellationException ? new OperationCanceledException() : new HttpRequestException("offline");
        var handler = new FakeHttpHandler().Throw(exception).Throw(exception).Throw(exception);
        using var http = new HttpClient(handler);
        using IDisposable scope = CaptureLogs(out LogManager manager);
        using var ownedManager = manager;

        Assert.False(await Poster(http).PostAsync(Snapshot(), CancellationToken.None));

        Assert.Equal(3, handler.Requests.Count);
        AssertOneWarning();
    }

    [Fact]
    public async Task Http_fault_warning_redacts_the_service_key_and_exception_message_chain()
    {
        const string ServiceKey = "dummy-service-credential";
        var exception = new HttpRequestException("transport " + ServiceKey + " " + ServiceKey,
            new InvalidOperationException("reading /server/private/world with Bearer abcdefgh12",
                new ArgumentException("last " + ServiceKey)));
        var handler = new FakeHttpHandler().Throw(exception).Throw(exception).Throw(exception);
        using var http = new HttpClient(handler);
        using IDisposable scope = CaptureLogs(out LogManager manager);
        using var ownedManager = manager;
        var poster = new ServerSnapshotPoster(http, new Uri("https://relay.example/"), ServiceKey, new ReportRedactor("/server/private"))
        {
            RetryPolicy = HttpRelaySink.DefaultPolicy with { Backoff = _ => TimeSpan.Zero },
        };

        Assert.False(await poster.PostAsync(Snapshot(), CancellationToken.None));

        AssertOneWarning();
        string formatted = LogFormatter.Format(Assert.Single(logs.Entries));
        Assert.DoesNotContain(ServiceKey, formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefgh12", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("/server/private", formatted, StringComparison.Ordinal);
        Assert.Contains("System.Net.Http.HttpRequestException: transport [redacted] [redacted]", formatted, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: reading ~/world with Bearer [redacted]", formatted, StringComparison.Ordinal);
        Assert.Contains("System.ArgumentException: last [redacted]", formatted, StringComparison.Ordinal);
        Assert.Null(Assert.Single(logs.Entries).Exception);
    }

    [Fact]
    public async Task Http_fault_with_unreadable_inner_message_returns_false_with_one_warning()
    {
        var exception = new HttpRequestException("offline", new UnreadableMessageException());
        var handler = new FakeHttpHandler().Throw(exception).Throw(exception).Throw(exception);
        using var http = new HttpClient(handler);
        using IDisposable scope = CaptureLogs(out LogManager manager);
        using var ownedManager = manager;

        Assert.False(await Poster(http).PostAsync(Snapshot(), CancellationToken.None));

        AssertOneWarning();
        string formatted = LogFormatter.Format(Assert.Single(logs.Entries));
        Assert.Contains("System.Net.Http.HttpRequestException: offline", formatted, StringComparison.Ordinal);
        Assert.Contains(typeof(UnreadableMessageException).FullName + ": " + ReportRedactor.Mask, formatted, StringComparison.Ordinal);
        Assert.Null(Assert.Single(logs.Entries).Exception);
    }

    [Theory]
    [InlineData("k")]
    [InlineData("")]
    public async Task Unexpected_http_fault_returns_false_with_one_warning(string serviceKey)
    {
        var handler = new FakeHttpHandler().Throw(new InvalidOperationException("handler failed"));
        using var http = new HttpClient(handler);
        using IDisposable scope = CaptureLogs(out LogManager manager);
        using var ownedManager = manager;

        var poster = new ServerSnapshotPoster(http, new Uri("https://relay.example/"), serviceKey);

        Assert.False(await poster.PostAsync(Snapshot(), CancellationToken.None));

        Assert.Single(handler.Requests);
        AssertOneWarning();
    }

    [Fact]
    public async Task Serialization_fault_returns_false_with_one_warning()
    {
        var snapshot = Snapshot() with { Subject = null! };
        Assert.Throws<JsonException>(() => BugReportJson.SerializeServerSnapshot(new ServerSnapshotWire(
            BugReportFormat.Version, snapshot.ReportId, snapshot.Subject, snapshot.CapturedAt, new Dictionary<string, JsonElement>())));
        var handler = new FakeHttpHandler();
        using var http = new HttpClient(handler);
        using IDisposable scope = CaptureLogs(out LogManager manager);
        using var ownedManager = manager;

        Assert.False(await Poster(http).PostAsync(snapshot, CancellationToken.None));

        Assert.Empty(handler.Requests);
        AssertOneWarning();
    }

    [Fact]
    public async Task Caller_cancellation_propagates_without_a_warning()
    {
        using var caller = new CancellationTokenSource();
        var handler = new FakeHttpHandler().CancelThenHang(caller);
        using var http = new HttpClient(handler);
        using IDisposable scope = CaptureLogs(out LogManager manager);
        using var ownedManager = manager;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Poster(http).PostAsync(Snapshot(), caller.Token));

        Assert.Single(handler.Requests);
        Assert.Empty(logs.Entries);
    }

    [Fact]
    public async Task Precancelled_caller_does_no_work_and_does_not_warn()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var handler = new FakeHttpHandler();
        using var http = new HttpClient(handler);
        using IDisposable scope = CaptureLogs(out LogManager manager);
        using var ownedManager = manager;
        bool finished = false;
        var snapshot = Snapshot() with { Sections = new CallbackSections(() => finished = true) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Poster(http).PostAsync(snapshot, caller.Token));

        Assert.False(finished);
        Assert.Empty(handler.Requests);
        Assert.Empty(logs.Entries);
    }

    [Theory]
    [InlineData("ftp://relay.example/")]
    [InlineData("file:///tmp/")]
    [InlineData("api", UriKind.Relative)]
    public void Non_http_relay_base_is_refused(string address, UriKind kind = UriKind.Absolute)
    {
        using var http = new HttpClient();
        Assert.Throws<ArgumentException>(() => new ServerSnapshotPoster(http, new Uri(address, kind), "k"));
    }

    sealed class UnreadableMessageException : Exception
    {
        public override string Message => throw new InvalidOperationException("message getter failed");
    }

    sealed class ObservingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public int ThreadId { get; private set; }
        public string? ServiceKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ThreadId = Environment.CurrentManagedThreadId;
            ServiceKey = request.Headers.TryGetValues(RelayHeaders.ServiceKey, out IEnumerable<string>? values)
                ? values.Single() : null;
            return base.SendAsync(request, cancellationToken);
        }
    }

    sealed class CallbackSections(Action onRead) : IReadOnlyList<RawSection>
    {
        public int Count => 0;
        public RawSection this[int index] => throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<RawSection> GetEnumerator()
        {
            onRead();
            return Enumerable.Empty<RawSection>().GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
