using System;
using System.Collections.Generic;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports;

/// <summary>
/// Limits report requests per account over a rolling hour and captures raw sections on the simulation thread.
/// Register sections and call <see cref="Capture"/> on that thread. <see cref="TryAccept"/> is thread-safe.
/// </summary>
public sealed class BugReportServerCapture
{
    static readonly TimeSpan Window = TimeSpan.FromHours(1);

    readonly TimeProvider time;
    readonly int perAccountPerHour;
    readonly object gate = new();
    readonly Dictionary<string, Queue<DateTimeOffset>> accepted = new(StringComparer.Ordinal);
    readonly BugReportSectionSet sections = new();

    /// <summary>Creates a capture with the supplied clock and a positive per-account hourly quota.</summary>
    public BugReportServerCapture(TimeProvider time, int perAccountPerHour = 5)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perAccountPerHour);
        this.time = time;
        this.perAccountPerHour = perAccountPerHour;
    }

    /// <summary>Registers a section to capture on the simulation thread.</summary>
    public void Add(IBugReportSection section) => sections.Add(section);

    /// <summary>Reserves one request for this account. Call before scheduling any capture work.</summary>
    public bool TryAccept(string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        lock (gate)
        {
            DateTimeOffset now = time.GetUtcNow();
            Prune(now);
            if (!accepted.TryGetValue(subject, out Queue<DateTimeOffset>? timestamps))
            {
                timestamps = new Queue<DateTimeOffset>();
                accepted.Add(subject, timestamps);
            }
            if (timestamps.Count >= perAccountPerHour) return false;
            timestamps.Enqueue(now);
            return true;
        }
    }

    /// <summary>Captures raw sections synchronously, with no redaction or truncation. Call on the simulation thread.</summary>
    /// <exception cref="ArgumentException"><paramref name="reportId"/> is not a canonical version 7 id.</exception>
    public ServerSnapshot Capture(string reportId, string subject)
    {
        if (!ReportIds.IsValid(reportId))
            throw new ArgumentException("A canonical version 7 report id is required.", nameof(reportId));
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        DateTimeOffset capturedAt = time.GetUtcNow();
        return new ServerSnapshot(reportId, subject, capturedAt,
            sections.CaptureAll(new BugReportContext(reportId, capturedAt, subject)));
    }

    void Prune(DateTimeOffset now)
    {
        List<string>? expired = null;
        foreach ((string subject, Queue<DateTimeOffset> timestamps) in accepted)
        {
            while (timestamps.TryPeek(out DateTimeOffset timestamp) && now - timestamp >= Window)
                timestamps.Dequeue();
            if (timestamps.Count == 0)
                (expired ??= []).Add(subject);
        }
        if (expired is not null)
            foreach (string subject in expired)
                accepted.Remove(subject);
    }
}
