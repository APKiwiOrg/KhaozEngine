using System;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;

namespace KhaozEngine.Tests.BugReports.Relay.Azure;

/// <summary>Retains the SDK send receipt so live cleanup can delete its own message even when receiving fails.</summary>
internal sealed class ReceiptTrackingQueueClient : QueueClient
{
    public ReceiptTrackingQueueClient(Uri uri, QueueClientOptions options) : base(uri, options) { }
    public ReceiptTrackingQueueClient(Uri uri, TokenCredential credential, QueueClientOptions options) : base(uri, credential, options) { }
    public SendReceipt? Sent { get; private set; }

    public override async Task<Response<SendReceipt>> SendMessageAsync(string messageText, CancellationToken cancellationToken)
    {
        Response<SendReceipt> response = await base.SendMessageAsync(messageText, cancellationToken);
        Sent = response.Value;
        return response;
    }
}
