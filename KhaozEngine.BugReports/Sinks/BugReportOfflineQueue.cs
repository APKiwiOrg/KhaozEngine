using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.BugReports;

/// <summary>
/// Keeps report bundles on disk until they reach the signed-in account's destination. Each entry is
/// <c>&lt;reportId&gt;.zip</c> beside <c>&lt;reportId&gt;.json</c>. Both are written to <c>.tmp</c> and moved into
/// place, the metadata last, so a metadata file marks a complete entry. Every enqueue and flush expires entries older
/// than <see cref="MaxAge"/>, then evicts the oldest until at most <see cref="MaxReports"/> entries and
/// <see cref="MaxBytes"/> bytes remain. A scan deletes orphans, unreadable metadata and stale <c>.tmp</c> files, and
/// leaves every file without a report id name alone. A null subject binds to the first account that flushes.
/// </summary>
public sealed class BugReportOfflineQueue
{
    /// <summary>The most entries kept after pruning.</summary>
    public const int MaxReports = 3;

    /// <summary>The most bundle bytes kept after pruning.</summary>
    public const long MaxBytes = 30 * 1024 * 1024;

    /// <summary>Entries queued longer ago than this expire.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    const string ZipSuffix = ".zip";
    const string JsonSuffix = ".json";
    const string TmpSuffix = ".tmp";

    readonly string directory;
    readonly TimeProvider time;

    // Guards every file mutation and scan, and the in-flight set. Never held across an await or a send.
    readonly object sync = new();
    readonly HashSet<string> inFlight = new(StringComparer.Ordinal);

    // Serialises flush loops only.
    readonly SemaphoreSlim flushGate = new(1, 1);

    /// <summary>Creates a queue over <paramref name="directory"/>, which is created on the first enqueue.</summary>
    /// <param name="directory">The pending directory, for example <c>&lt;stateDir&gt;/bug-reports/pending</c>.</param>
    /// <param name="time">The clock that stamps <c>queuedAt</c> and drives expiry.</param>
    public BugReportOfflineQueue(string directory, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(time);
        this.directory = Path.GetFullPath(directory);
        this.time = time;
    }

    /// <summary>The number of complete entries, after deleting orphans and unreadable metadata.</summary>
    public int Count
    {
        get
        {
            lock (sync)
                return Scan().Count;
        }
    }

    /// <summary>Writes <paramref name="submission"/> to the queue, then prunes.</summary>
    /// <remarks>A null or blank subject is stored as null and binds to the first account that flushes.</remarks>
    /// <exception cref="ArgumentException">The report id is not a canonical version 7 id.</exception>
    /// <exception cref="IOException">The directory or a file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The directory is not writable.</exception>
    public void Enqueue(BugReportSubmission submission) => Write(submission, claim: false);

    // Enqueues and marks the id in flight before its metadata becomes visible, so no flush sends it meanwhile.
    // The caller ends the claim with Complete.
    internal void EnqueueInFlight(BugReportSubmission submission) => Write(submission, claim: true);

    // Applies a send's result and ends the claim. Sent and Rejected delete the entry. Queued or no result keeps it.
    internal void Complete(string reportId, BugReportSubmitResult? result)
    {
        lock (sync)
        {
            if (result is { Status: BugReportSubmitStatus.Sent or BugReportSubmitStatus.Rejected })
                DeleteEntry(reportId);
            inFlight.Remove(reportId);
        }
    }

    /// <summary>
    /// Prunes, then sends <paramref name="subject"/>'s entries and every unbound entry oldest first, binding each
    /// unbound entry to <paramref name="subject"/> before its send. Stops at the first Queued result. Entries already
    /// in flight are skipped.
    /// </summary>
    /// <returns>The number of entries the sink accepted.</returns>
    public async Task<int> FlushAsync(string subject, IBugReportSink sink, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(sink);
        await flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<QueuedReportMetadata> entries;
            lock (sync)
                entries = ScanAndPrune();

            int sent = 0;
            foreach (QueuedReportMetadata entry in entries)
            {
                if (entry.Subject is not null && !string.Equals(entry.Subject, subject, StringComparison.Ordinal))
                    continue;
                BugReportSubmitResult? result = await SendOneAsync(entry.ReportId, subject, sink, cancellationToken).ConfigureAwait(false);
                if (result is null)
                    continue;
                if (result.Status == BugReportSubmitStatus.Sent)
                    sent++;
                else if (result.Status == BugReportSubmitStatus.Queued)
                    break;
            }
            return sent;
        }
        finally
        {
            flushGate.Release();
        }
    }

    // Claims, binds and sends one entry, then applies the result. Null when nothing was sent: the id is already in
    // flight, the entry is gone or unreadable, or it belongs to another account.
    internal async Task<BugReportSubmitResult?> SendOneAsync(string reportId, string subject, IBugReportSink sink, CancellationToken cancellationToken)
    {
        if (!ReportIds.IsValid(reportId))
            throw new ArgumentException("A canonical version 7 report id is required.", nameof(reportId));
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (!inFlight.Add(reportId))
                return null;
        }

        BugReportSubmitResult? result = null;
        try
        {
            if (!TryBind(reportId, subject))
                return null;
            byte[]? bundle = TryReadBundle(reportId);
            if (bundle is null)
                return null;
            result = await sink.SubmitAsync(new BugReportSubmission(reportId, subject, bundle), cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            Complete(reportId, result);
        }
    }

    void Write(BugReportSubmission submission, bool claim)
    {
        ArgumentNullException.ThrowIfNull(submission);
        string id = submission.ReportId;
        if (!ReportIds.IsValid(id))
            throw new ArgumentException("A canonical version 7 report id is required.", nameof(submission));

        string? subject = string.IsNullOrWhiteSpace(submission.Subject) ? null : submission.Subject;
        var metadata = new QueuedReportMetadata(id, subject, time.GetUtcNow(), submission.Bundle.Length);
        string zip = ZipPath(id), json = JsonPath(id);

        lock (sync)
        {
            bool claimed = false;
            try
            {
                Directory.CreateDirectory(directory);
                WriteFile(zip + TmpSuffix, submission.Bundle.Span);
                WriteFile(json + TmpSuffix, Serialize(metadata));
                if (claim)
                    claimed = inFlight.Add(id);
                File.Move(zip + TmpSuffix, zip, overwrite: true);
                File.Move(json + TmpSuffix, json, overwrite: true);
            }
            catch
            {
                TryDelete(zip + TmpSuffix);
                TryDelete(json + TmpSuffix);
                if (claimed)
                    inFlight.Remove(id);
                throw;
            }
            ScanAndPrune();
        }
    }

    // Takes the lock. Rewrites a null subject to the flushing account. False when the entry is gone,
    // unreadable, bound to another account or could not be rebound.
    bool TryBind(string reportId, string subject)
    {
        lock (sync)
        {
            if (ReadMetadata(reportId, out QueuedReportMetadata? metadata) != MetadataRead.Ok)
                return false;
            if (metadata!.Subject is not null)
                return string.Equals(metadata.Subject, subject, StringComparison.Ordinal);

            string json = JsonPath(reportId);
            try
            {
                WriteFile(json + TmpSuffix, Serialize(metadata with { Subject = subject }));
                File.Move(json + TmpSuffix, json, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(json + TmpSuffix);
                Log.Warn("Queued bug report " + reportId + " could not be bound to its account.", ex);
                return false;
            }
        }
    }

    // Outside the lock: a prune may delete the bundle meanwhile, which reads as nothing to send.
    byte[]? TryReadBundle(string reportId)
    {
        try
        {
            return File.ReadAllBytes(ZipPath(reportId));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Queued bug report " + reportId + " could not be read.", ex);
            return null;
        }
    }

    // Under the lock. Scans, then expires and evicts. Returns the kept entries oldest first.
    List<QueuedReportMetadata> ScanAndPrune()
    {
        List<QueuedReportMetadata> scanned = Scan();
        DateTimeOffset now = time.GetUtcNow();
        var kept = new List<QueuedReportMetadata>(scanned.Count);
        long bytes = 0;
        foreach (QueuedReportMetadata entry in scanned)
        {
            if (now - entry.QueuedAt > MaxAge)
            {
                DeleteEntry(entry.ReportId);
                continue;
            }
            kept.Add(entry);
            bytes += entry.Bytes;
        }

        int evict = 0;
        while (kept.Count - evict > MaxReports || bytes > MaxBytes)
        {
            DeleteEntry(kept[evict].ReportId);
            bytes -= kept[evict].Bytes;
            evict++;
        }
        kept.RemoveRange(0, evict);
        return kept;
    }

    // Under the lock, so no enqueue is mid-write and every owned .tmp file is stale.
    List<QueuedReportMetadata> Scan()
    {
        string[] paths;
        try
        {
            paths = Directory.GetFiles(directory);
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("The bug report queue at " + directory + " could not be read.", ex);
            return [];
        }

        var zips = new HashSet<string>(StringComparer.Ordinal);
        var jsons = new List<string>();
        foreach (string path in paths)
        {
            switch (Classify(Path.GetFileName(path), out string id))
            {
                case OwnedFile.Tmp:
                    TryDelete(path);
                    break;
                case OwnedFile.Zip:
                    zips.Add(id);
                    break;
                case OwnedFile.Json:
                    jsons.Add(id);
                    break;
            }
        }

        var entries = new List<QueuedReportMetadata>(jsons.Count);
        foreach (string id in jsons)
        {
            if (!zips.Remove(id))
            {
                TryDelete(JsonPath(id));
                continue;
            }
            switch (ReadMetadata(id, out QueuedReportMetadata? metadata))
            {
                case MetadataRead.Ok:
                    entries.Add(metadata!);
                    break;
                case MetadataRead.Corrupt:
                    Log.Warn("Deleted queued bug report " + id + " with unreadable metadata.");
                    DeleteEntry(id);
                    break;
                case MetadataRead.Missing:
                    TryDelete(ZipPath(id));
                    break;
            }
        }
        foreach (string id in zips)
            TryDelete(ZipPath(id));

        entries.Sort(static (a, b) =>
        {
            int byTime = a.QueuedAt.CompareTo(b.QueuedAt);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.ReportId, b.ReportId);
        });
        return entries;
    }

    MetadataRead ReadMetadata(string reportId, out QueuedReportMetadata? metadata)
    {
        metadata = null;
        byte[] json;
        try
        {
            json = File.ReadAllBytes(JsonPath(reportId));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return MetadataRead.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Queued bug report metadata " + reportId + " could not be read.", ex);
            return MetadataRead.Unreadable;
        }

        try
        {
            metadata = JsonSerializer.Deserialize(json, QueuedReportJsonContext.Default.QueuedReportMetadata);
        }
        catch (JsonException)
        {
            return MetadataRead.Corrupt;
        }
        return metadata is not null && string.Equals(metadata.ReportId, reportId, StringComparison.Ordinal) && metadata.Bytes >= 0
            ? MetadataRead.Ok
            : MetadataRead.Corrupt;
    }

    // The metadata goes first, so a partly deleted entry is an orphan bundle the next scan removes.
    void DeleteEntry(string reportId)
    {
        TryDelete(JsonPath(reportId));
        TryDelete(ZipPath(reportId));
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Bug report queue file " + path + " could not be deleted.", ex);
        }
    }

    static void WriteFile(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
    }

    static byte[] Serialize(QueuedReportMetadata metadata) =>
        JsonSerializer.SerializeToUtf8Bytes(metadata, QueuedReportJsonContext.Default.QueuedReportMetadata);

    // Only <id>.zip, <id>.json and their .tmp forms with a canonical report id belong to the queue.
    static OwnedFile Classify(string name, out string id)
    {
        foreach ((string suffix, OwnedFile kind) in OwnedSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                id = name[..^suffix.Length];
                return ReportIds.IsValid(id) ? kind : OwnedFile.None;
            }
        }
        id = "";
        return OwnedFile.None;
    }

    static readonly (string Suffix, OwnedFile Kind)[] OwnedSuffixes =
    [
        (ZipSuffix + TmpSuffix, OwnedFile.Tmp),
        (JsonSuffix + TmpSuffix, OwnedFile.Tmp),
        (ZipSuffix, OwnedFile.Zip),
        (JsonSuffix, OwnedFile.Json),
    ];

    string ZipPath(string reportId) => Path.Combine(directory, reportId + ZipSuffix);

    string JsonPath(string reportId) => Path.Combine(directory, reportId + JsonSuffix);

    enum OwnedFile { None, Tmp, Zip, Json }

    enum MetadataRead { Ok, Missing, Corrupt, Unreadable }
}
