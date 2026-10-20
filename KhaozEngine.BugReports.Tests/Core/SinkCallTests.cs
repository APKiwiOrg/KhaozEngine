using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Core;

public sealed class SinkCallTests
{
    static readonly BugReportSubmission Submission = new(TestBundles.ReportId, "a", new byte[] { 1 });

    [Fact]
    public async Task Sink_failure_racing_the_callers_cancel_is_a_failure_not_a_throw()
    {
        using var cancel = new CancellationTokenSource();
        var sink = new FakeSink().CancelThenThrow(cancel, new IOException("disk"));

        Assert.Null(await SinkCall.TrySubmitAsync(sink, Submission, cancel.Token));
    }

    [Fact]
    public async Task Callers_cancellation_still_throws()
    {
        using var cancel = new CancellationTokenSource();
        var sink = new FakeSink().CancelThenThrow(cancel, new OperationCanceledException(cancel.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SinkCall.TrySubmitAsync(sink, Submission, cancel.Token));
    }
}
