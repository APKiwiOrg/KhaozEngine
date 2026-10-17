using Azure.Storage.Queues;

namespace KhaozEngine.BugReports.Relay.Azure;

/// <summary>Queue client options matching the Azure Functions queue trigger's default encoding.</summary>
public static class ReportQueueClientOptions
{
    /// <summary>Creates fresh options with Base64 message encoding for work and poison queues.</summary>
    public static QueueClientOptions Create() => new() { MessageEncoding = QueueMessageEncoding.Base64 };
}
