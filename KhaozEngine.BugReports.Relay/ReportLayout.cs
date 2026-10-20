using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports.Relay;

/// <summary>
/// Blob names and headers for the relay. In the Pending area, <c>accepted/</c> must outlive the client queue's
/// 7-day <c>BugReportOfflineQueue.MaxAge</c>, <c>records/</c> lives as long as the public report folder, and
/// <c>snapshots/</c> may expire after 1 day. <c>revoked/</c> outlives both the report retention and client retries.
/// </summary>
internal static class ReportLayout
{
    const string ReferenceAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public const string ViewerFile = "viewer.html";
    public const string ServerSnapshotFile = "server.json";
    public const int MaxRecordBytes = 2 * 1024 * 1024;
    public const int MaxAcceptedBytes = 4096;

    /// <summary>Creates a capability folder with the received UTC month and a cryptographically random 128-bit token.</summary>
    public static string NewFolder(string gameId, string reportId, DateTimeOffset receivedAt)
    {
        if (!BundleEntryNames.IsValidGameId(gameId))
            throw new ArgumentException("A canonical game id is required.", nameof(gameId));
        ValidateReportId(reportId);
        string month = receivedAt.ToUniversalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture);
        string token = RandomNumberGenerator.GetHexString(32, lowercase: true);
        return $"{gameId}/{month}/{reportId}-{token}";
    }

    /// <summary>Joins a validated folder and relative entry name, including nested entries.</summary>
    public static string Entry(string folder, string name)
    {
        ValidatePath(folder, nameof(folder));
        ValidatePath(name, nameof(name));
        return folder + "/" + name;
    }

    /// <summary>The relay-private report record path in the Pending area.</summary>
    public static string ReportRecord(string reportId)
    {
        ValidateReportId(reportId);
        return "records/" + reportId + ".json";
    }

    /// <summary>The idempotency index path in the Pending area.</summary>
    public static string AcceptedIndex(string reportId)
    {
        ValidateReportId(reportId);
        return "accepted/" + reportId + ".json";
    }

    /// <summary>Normalizes a reference code, including Crockford confusable characters.</summary>
    public static bool TryNormalizeReference(string text, out string reference)
    {
        reference = string.Empty;
        if (text is null) return false;
        string normalized = text.Trim().ToUpperInvariant();
        if (normalized.Length != 8 || !normalized.StartsWith("R-", StringComparison.Ordinal)) return false;
        normalized = normalized.Replace('O', '0').Replace('I', '1').Replace('L', '1');
        foreach (char c in normalized.AsSpan(2))
            if (!ReferenceAlphabet.Contains(c)) return false;
        reference = normalized;
        return true;
    }

    /// <summary>The relay-private path mapping a reference code to one report id.</summary>
    public static string ReferenceIndex(string reference, string reportId)
    {
        ValidateReportId(reportId);
        return ReferencePrefix(reference) + reportId + ".json";
    }

    /// <summary>The relay-private listing prefix for every report with a reference code.</summary>
    public static string ReferencePrefix(string reference)
    {
        if (!TryNormalizeReference(reference, out string normalized))
            throw new ArgumentException("A valid report reference code is required.", nameof(reference));
        return "references/" + normalized.ToLowerInvariant() + "/";
    }

    /// <summary>The raw server snapshot path in the Pending area.</summary>
    public static string PendingSnapshot(string reportId)
    {
        ValidateReportId(reportId);
        return "snapshots/" + reportId + ".json";
    }

    /// <summary>The durable revocation tombstone path in the Pending area.</summary>
    public static string Revoked(string reportId)
    {
        ValidateReportId(reportId);
        return "revoked/" + reportId + ".json";
    }

    /// <summary>Returns the explicit content type and cache policy for a supported entry extension.</summary>
    public static ReportBlobHeaders HeadersFor(string name)
    {
        ValidatePath(name, nameof(name));
        return Path.GetExtension(name) switch
        {
            ".json" => new("application/json; charset=utf-8", "no-cache"),
            ".html" => new("text/html; charset=utf-8", "no-cache"),
            ".png" => new("image/png", "public, max-age=86400"),
            ".jpg" => new("image/jpeg", "public, max-age=86400"),
            ".mp4" => new("video/mp4", "public, max-age=86400"),
            _ => throw new ArgumentException("The report entry has an unsupported extension.", nameof(name)),
        };
    }

    /// <summary>Joins a validated path onto an absolute media base URL ending in <c>/</c>.</summary>
    public static Uri PublicUrl(Uri publicMediaBase, string path)
    {
        ArgumentNullException.ThrowIfNull(publicMediaBase);
        if (!publicMediaBase.IsAbsoluteUri || !publicMediaBase.AbsoluteUri.EndsWith("/", StringComparison.Ordinal))
            throw new ArgumentException("The public media base must be absolute and end in '/'.", nameof(publicMediaBase));
        ValidatePath(path, nameof(path));
        return new Uri(publicMediaBase, path);
    }

    static void ValidateReportId(string reportId)
    {
        if (!ReportIds.IsValid(reportId))
            throw new ArgumentException("A canonical version 7 report id is required.", nameof(reportId));
    }

    static void ValidatePath(string path, string parameterName)
    {
        if (!ReportPaths.IsValid(path))
            throw new ArgumentException("A valid relative report path is required.", parameterName);
    }
}
