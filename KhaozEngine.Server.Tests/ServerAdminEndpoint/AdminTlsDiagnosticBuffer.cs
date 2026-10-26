using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace KhaozEngine.Tests.ServerAdminEndpoint;

internal sealed record AdminTlsDiagnosticEvent(double ElapsedMilliseconds, string Phase, string? Category, int? EventId,
    IReadOnlyDictionary<string, string>? Fields, string? Exception);

internal sealed record AdminTlsDiagnosticSnapshot(string Scenario, string HostId, int StoredBytes, int DroppedEvents,
    int TruncatedValues, IReadOnlyList<AdminTlsDiagnosticEvent> Events, int MaxEvents, int MaxBytes, int MaxEventBytes,
    int DiagnosticErrors)
{
    public string ToJson() => JsonSerializer.Serialize(this);
}

// The byte budget covers the serialized event payloads. The fixed-size summary is outside that budget.
internal sealed class AdminTlsDiagnosticBuffer
{
    private readonly object gate = new();
    private readonly string scenario;
    private readonly string hostId = Guid.NewGuid().ToString("N");
    private readonly long origin = Stopwatch.GetTimestamp();
    private readonly int maxEvents;
    private readonly int maxBytes;
    private readonly int maxEventBytes;
    private readonly List<AdminTlsDiagnosticEvent> events = new();
    private int storedBytes;
    private int droppedEvents;
    private int truncatedValues;
    private int diagnosticErrors;
    private AdminTlsDiagnosticSnapshot? frozen;

    public AdminTlsDiagnosticBuffer(string scenario, int maxEvents = 512, int maxBytes = 262144, int maxEventBytes = 16384)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEvents, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEventBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, maxEventBytes);
        this.scenario = scenario.Length <= 128 ? scenario : scenario[..128];
        this.maxEvents = maxEvents;
        this.maxBytes = maxBytes;
        this.maxEventBytes = maxEventBytes;
    }

    public void Record(string phase, string? category = null, int? eventId = null,
        IReadOnlyDictionary<string, string>? fields = null, Exception? error = null)
    {
        lock (gate)
        {
            if (frozen is not null) return;
            if (events.Count >= maxEvents) { droppedEvents++; return; }
            Dictionary<string, string>? kept = null;
            if (fields is not null)
            {
                kept = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var field in fields)
                {
                    if (kept.Count == 12) { truncatedValues++; break; }
                    kept[Clip(field.Key, 64)] = Clip(field.Value, 256);
                }
            }
            var entry = new AdminTlsDiagnosticEvent(Stopwatch.GetElapsedTime(origin).TotalMilliseconds,
                Clip(phase, 128), category is null ? null : Clip(category, 256), eventId,
                kept is null ? null : new ReadOnlyDictionary<string, string>(kept), Describe(error));
            int size = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(entry));
            if (size > maxEventBytes || size > maxBytes - storedBytes) { droppedEvents++; return; }
            events.Add(entry);
            storedBytes += size;
        }
    }

    public void DiagnosticError()
    {
        lock (gate) { if (frozen is null) diagnosticErrors++; }
    }

    public void CapacityLoss()
    {
        lock (gate) { if (frozen is null) droppedEvents++; }
    }

    private string Clip(string value, int limit)
    {
        if (value.Length <= limit) return value;
        truncatedValues++;
        return value[..limit] + "[truncated]";
    }

    private string? Describe(Exception? error)
    {
        if (error is null) return null;
        var text = new StringBuilder();
        int messageLimit = Math.Min(256, (maxEventBytes - 400) / 24);
        int depth = 0;
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (depth++ == 8) { truncatedValues++; break; }
            text.Append(current.GetType().FullName).Append(": ").Append(Clip(current.Message, messageLimit)).Append('\n');
        }
        // Keep the chain before stacks so a large outer stack cannot hide an inner failure.
        for (Exception? current = error; current is not null && depth-- > 0; current = current.InnerException)
        {
            if (current.StackTrace is { } stack)
                text.Append(Clip(stack, Math.Max(32, maxEventBytes / 16))).Append('\n');
        }
        return text.ToString();
    }

    public AdminTlsDiagnosticSnapshot Freeze()
    {
        lock (gate)
            return frozen ??= new AdminTlsDiagnosticSnapshot(scenario, hostId, storedBytes, droppedEvents,
                truncatedValues, Array.AsReadOnly(events.ToArray()), maxEvents, maxBytes, maxEventBytes, diagnosticErrors);
    }
}
