using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Relay.Azure;

namespace KhaozEngine.Tests.BugReports.Relay.Azure;

internal sealed class RecordingQueueSender : IReportQueueSender
{
    public List<string> Messages { get; } = new();
    public Exception? Failure { get; set; }
    public Action? BeforeSend { get; set; }

    public Task SendAsync(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        BeforeSend?.Invoke();
        if (Failure is not null)
            throw Failure;
        Messages.Add(text);
        return Task.CompletedTask;
    }
}
