using System;

namespace KhaozEngine.BugReports;

/// <summary>A structured refusal of a bundle that would break its entry or byte limit. Existing
/// <see cref="InvalidOperationException"/> catches remain valid.</summary>
public sealed class BugReportBundleLimitException : InvalidOperationException
{
    /// <summary>Creates a refusal with its required reason.</summary>
    /// <param name="reason">The limit the bundle would break.</param>
    /// <param name="message">The developer-facing explanation, not a classification token.</param>
    public BugReportBundleLimitException(BugReportBundleRefusal reason, string message)
        : base(message) => Reason = reason;

    /// <summary>The limit the bundle would break: <see cref="BugReportBundleRefusal.EntryCountExceeded"/>,
    /// <see cref="BugReportBundleRefusal.PayloadBytesExceeded"/> or
    /// <see cref="BugReportBundleRefusal.ArchiveBytesExceeded"/>.</summary>
    public BugReportBundleRefusal Reason { get; }

    /// <summary>The zip entries the bundle needs, the manifest included, for
    /// <see cref="BugReportBundleRefusal.EntryCountExceeded"/>.</summary>
    public int? ActualCount { get; init; }

    /// <summary>The inclusive entry limit, for <see cref="BugReportBundleRefusal.EntryCountExceeded"/>.</summary>
    public int? MaximumCount { get; init; }

    /// <summary>The bytes of every entry after the manifest, for <see cref="BugReportBundleRefusal.PayloadBytesExceeded"/>,
    /// or the length of the written archive, for <see cref="BugReportBundleRefusal.ArchiveBytesExceeded"/>.</summary>
    public long? ActualBytes { get; init; }

    /// <summary>The inclusive bundle byte limit, for <see cref="BugReportBundleRefusal.PayloadBytesExceeded"/> and
    /// <see cref="BugReportBundleRefusal.ArchiveBytesExceeded"/>.</summary>
    public long? MaximumBytes { get; init; }
}
