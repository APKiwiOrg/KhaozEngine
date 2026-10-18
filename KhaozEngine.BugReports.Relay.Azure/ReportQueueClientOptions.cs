using System.Threading.Tasks;
using Azure.Storage.Queues;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.BugReports.Relay.Azure;

/// <summary>Queue client options matching the Azure Functions queue trigger's default encoding.</summary>
public static class ReportQueueClientOptions
{
    /// <summary>
    /// Creates the required Base64 options for work and poison clients. Undecodable messages remain in the queue
    /// until their visibility lease expires, with one warning naming only the queue and message id per encounter.
    /// </summary>
    /// <param name="logger">The warning logger. Null uses <c>Log.Get("BugReports.Relay")</c>.</param>
    public static QueueClientOptions Create(ILogger? logger = null)
    {
        ILogger log = logger ?? Log.Get("BugReports.Relay");
        var options = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };
        options.MessageDecodingFailed += args =>
        {
            if (args.ReceivedMessage is not null) ReportQueueDecodingFailures.Record(args.Queue);
            string? id = args.ReceivedMessage?.MessageId ?? args.PeekedMessage?.MessageId;
            log.Warn($"Queue {args.Queue.Name} message {id} could not be decoded.");
            return Task.CompletedTask;
        };
        return options;
    }
}
