using System.Diagnostics.Tracing;

namespace KhaozEngine.Tests.ServerAdminEndpoint;

// EventSource is process-wide. Dispatch only connection IDs this host observed, never other tests' traffic.
internal sealed class AdminTlsProbeEvents : EventListener
{
    private readonly object callbackGate = new();
    private AdminTlsProbe? owner;

    public AdminTlsProbeEvents(AdminTlsProbe probe)
    {
        owner = probe;
        foreach (EventSource source in EventSource.GetSources()) EnableKestrel(source);
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        // EventListener's base constructor calls this before derived fields are initialized.
        if (owner is not null) EnableKestrel(eventSource);
    }

    private void EnableKestrel(EventSource source)
    {
        if (source.Name == "Microsoft-AspNetCore-Server-Kestrel")
            EnableEvents(source, EventLevel.Informational, EventKeywords.None);
    }

    protected override void OnEventWritten(EventWrittenEventArgs data)
    {
        if (data.EventSource.Name != "Microsoft-AspNetCore-Server-Kestrel") return;
        lock (callbackGate)
        {
            if (owner is null) return;
            if (data.EventId == 0) { owner.Buffer.DiagnosticError(); return; }
            if (data.EventId is not (2 or 8 or 9 or 10)) return;
            if (data.Payload is not { Count: > 0 } payload || payload[0] is not string connection)
            {
                owner.Buffer.DiagnosticError();
                return;
            }
            // The protocol is safe. Event 9 also has ALPN and SNI, neither of which is collected.
            string? protocol = data.EventId is 8 or 9 && payload.Count > 1 ? payload[1] as string : null;
            owner.TlsEvent(data.EventId, connection, protocol);
        }
    }

    public override void Dispose()
    {
        // Drain callbacks before freezing. No callback can outlive the owner after this lock.
        lock (callbackGate) owner = null;
        base.Dispose();
    }
}
