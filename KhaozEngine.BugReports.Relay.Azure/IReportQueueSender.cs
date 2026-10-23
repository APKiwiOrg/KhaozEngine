using System;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.BugReports.Relay.Azure;

internal interface IReportQueueSender
{
    Task SendAsync(string text, TimeSpan? visibilityTimeout, CancellationToken ct);
}
