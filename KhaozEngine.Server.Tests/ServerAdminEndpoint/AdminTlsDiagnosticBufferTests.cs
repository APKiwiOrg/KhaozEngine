using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace KhaozEngine.Tests.ServerAdminEndpoint;

public sealed class AdminTlsDiagnosticBufferTests
{
    [Fact]
    public void Storage_is_bounded_and_reports_loss_without_losing_the_exception_chain()
    {
        var buffer = new AdminTlsDiagnosticBuffer("bounds", maxEvents: 4, maxBytes: 4096, maxEventBytes: 1024);
        buffer.Record("failure", error: new InvalidOperationException(new string('x', 4000), new ArgumentException("inner-proof")));
        for (int i = 0; i < 12; i++) buffer.Record("marker");

        AdminTlsDiagnosticSnapshot snapshot = buffer.Freeze();
        Assert.InRange(snapshot.Events.Count, 1, 4);
        Assert.InRange(snapshot.StoredBytes, 1, 4096);
        Assert.True(snapshot.DroppedEvents > 0);
        Assert.True(snapshot.TruncatedValues > 0);
        Assert.Contains("inner-proof", snapshot.Events[0].Exception, StringComparison.Ordinal);
        Assert.All(snapshot.Events, entry => Assert.InRange(Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(entry)), 1, 1024));
        buffer.Record("after-freeze");
        Assert.Same(snapshot, buffer.Freeze());
    }

    [Fact]
    public async Task Logger_and_tls_ingress_keep_only_owned_connections_and_safe_scope_fields()
    {
        var probe = new AdminTlsProbe("correlation", _ => { }, listenToEvents: false);
        var scopes = new LoggerExternalScopeProvider();
        probe.SetScopeProvider(scopes);
        ILogger logger = probe.CreateLogger("Microsoft.AspNetCore.Server.Kestrel.Https.Internal.HttpsConnectionMiddleware");
        probe.ConnectionObserved("owned", "127.0.0.1:1234", "127.0.0.1:5678");
        probe.TlsEvent(8, "other-host", "Tls13");
        probe.TlsEvent(8, "owned", "Tls13");
        using (scopes.Push(new Dictionary<string, object?> { ["ConnectionId"] = "owned", ["Authorization"] = "never-copy-me" }))
            logger.Log(LogLevel.Debug, new EventId(1, "AuthenticationFailed"),
                new Dictionary<string, object?> { ["RequestBody"] = "never-copy-me" },
                new InvalidOperationException("tls-proof", new ArgumentException("nested-proof")),
                (_, _) => throw new InvalidOperationException("The formatter must never be invoked."));
        await probe.RunAsync(() => Task.CompletedTask, () => Task.CompletedTask);

        string json = probe.Buffer.Freeze().ToJson();
        Assert.Contains("owned", json, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:5678", json, StringComparison.Ordinal);
        Assert.Contains("tls.start", json, StringComparison.Ordinal);
        Assert.Contains("nested-proof", json, StringComparison.Ordinal);
        Assert.DoesNotContain("other-host", json, StringComparison.Ordinal);
        Assert.DoesNotContain("never-copy-me", json, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestBody", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Finally_flushes_after_all_cleanup_and_keeps_the_original_failure()
    {
        var output = new List<string>();
        var original = new InvalidOperationException("original-proof");
        var probe = new AdminTlsProbe("exceptional", output.Add, listenToEvents: false);
        bool finalCleanup = false;
        Exception? observed = await Record.ExceptionAsync(() => probe.RunAsync(
            () => Task.FromException(original), async () =>
            {
                await probe.CleanupAsync("first", () => Task.FromException(new ArgumentException("cleanup-proof")));
                await probe.CleanupAsync("last", () => { finalCleanup = true; return Task.CompletedTask; });
            }));

        Assert.Same(original, observed);
        Assert.True(finalCleanup);
        string flushed = Assert.Single(output);
        Assert.Contains("cleanup-proof", flushed, StringComparison.Ordinal);
        Assert.Contains("last.complete", flushed, StringComparison.Ordinal);
        Assert.Contains("collector.detached", flushed, StringComparison.Ordinal);
        Assert.Contains("original-proof", flushed, StringComparison.Ordinal);
        Assert.True(flushed.IndexOf("last.complete", StringComparison.Ordinal) < flushed.IndexOf("collector.detached", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Successful_body_still_flushes_and_surfaces_cleanup_failure()
    {
        var output = new List<string>();
        var cleanup = new ArgumentException("cleanup-only-proof");
        var probe = new AdminTlsProbe("cleanup-only", output.Add, listenToEvents: false);
        Exception? observed = await Record.ExceptionAsync(() => probe.RunAsync(() => Task.CompletedTask,
            () => probe.CleanupAsync("dispose", () => Task.FromException(cleanup))));

        Assert.Same(cleanup, observed);
        Assert.Contains("cleanup-only-proof", Assert.Single(output), StringComparison.Ordinal);
    }
}
