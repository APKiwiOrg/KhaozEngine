using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports.Relay;

/// <summary>The shared inputs read from a validated report folder for viewers and issue composition.</summary>
internal sealed record ReportFolderContents(IReadOnlyList<CapturedSection> Sections, ClipIndex? Clip,
    IReadOnlyList<string> LogTail);

/// <summary>Reads report sections and the clip index under their aggregate format caps.</summary>
internal sealed class ReportFolderReader(IReportMediaStore media)
{
    public const int MaxClipIndexBytes = 64 * 1024;

    public async Task<ReportFolderContents> ReadAsync(ReportRecord record, CancellationToken ct)
    {
        var sections = new List<CapturedSection>();
        ClipIndex? clip = null;
        IReadOnlyList<string> logTail = [];
        int remaining = BugReportLimits.MaxSectionBytesTotal;
        foreach (BundleEntry entry in record.Manifest.Entries)
        {
            if (entry.Name.StartsWith("sections/", StringComparison.Ordinal))
            {
                ReadOnlyMemory<byte> json = await ReadAsync(record.Folder, entry.Name, remaining, ct).ConfigureAwait(false);
                remaining -= json.Length;
                string key = entry.Name["sections/".Length..^".json".Length];
                sections.Add(new CapturedSection(key, json));
                if (key == "log") logTail = BugReportLogTail.FromLogSection(json);
            }
            else if (entry.Name == BundleEntryNames.ClipIndex)
            {
                ReadOnlyMemory<byte> json = await ReadAsync(record.Folder, entry.Name, MaxClipIndexBytes, ct).ConfigureAwait(false);
                clip = BugReportJson.DeserializeClipIndex(json.Span);
            }
        }
        return new ReportFolderContents(sections, clip, logTail);
    }

    async Task<ReadOnlyMemory<byte>> ReadAsync(string folder, string name, int maxBytes, CancellationToken ct)
        => await media.GetAsync(ReportArea.Public, ReportLayout.Entry(folder, name), maxBytes, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("A stored report entry is missing: " + name);
}
