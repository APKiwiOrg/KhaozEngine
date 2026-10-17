using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Diagnostics;
using KhaozEngine.Platform;

namespace KhaozEngine.BugReports;

/// <summary>
/// Saves each report to a folder on this machine, for offline play and local testing. A bundle that passes
/// <see cref="BundleValidator"/> is written to <c>&lt;root&gt;/&lt;reportId&gt;/</c> as <c>bundle.zip</c>, its
/// validated entries extracted beside it, and <c>viewer.html</c> from <see cref="BugReportViewerPage"/>. Saving the
/// same report again overwrites its files in place and leaves other files alone. Each file is written to
/// <c>&lt;name&gt;.tmp</c> first and moved into place.
/// </summary>
/// <remarks>
/// A valid bundle is Sent with <see cref="ReportIds.ReferenceCode"/> of its id. An invalid one is Rejected with the
/// validator's error and nothing is written. A file system failure is Queued with <see cref="WriteFailedError"/>,
/// so the bundle is kept.
/// </remarks>
public sealed class LocalFolderSink : IBugReportSink
{
    /// <summary>The error of a Queued result when the report could not be written.</summary>
    public const string WriteFailedError = "write_failed";

    const string LogSection = "sections/log.json";
    const int LogTailLines = 50;

    readonly string root;
    readonly bool openViewer;

    /// <summary>Creates a sink that saves reports under one folder.</summary>
    /// <param name="root">The folder that holds one subfolder per report. It is created when missing.</param>
    /// <param name="openViewer">Whether to open each saved report's viewer page in the system browser.</param>
    public LocalFolderSink(string root, bool openViewer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        this.root = Path.GetFullPath(root);
        this.openViewer = openViewer;
    }

    /// <inheritdoc />
    public async Task<BugReportSubmitResult> SubmitAsync(BugReportSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        cancellationToken.ThrowIfCancellationRequested();

        byte[] bundle = submission.Bundle.ToArray();
        BundleValidationResult validation = BundleValidator.Validate(new MemoryStream(bundle, writable: false));
        if (!validation.IsValid || validation.Manifest is not { } manifest)
            return new BugReportSubmitResult(BugReportSubmitStatus.Rejected, null, validation.Error, null);

        string viewerPath;
        try
        {
            viewerPath = Save(bundle, manifest);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Could not save bug report " + manifest.ReportId + " to its local folder.", e);
            return new BugReportSubmitResult(BugReportSubmitStatus.Queued, null, WriteFailedError, null);
        }

        if (openViewer)
            await Browser.LaunchBrowserAsync(new Uri(viewerPath), cancellationToken).ConfigureAwait(false);
        return new BugReportSubmitResult(BugReportSubmitStatus.Sent, ReportIds.ReferenceCode(manifest.ReportId), null, null);
    }

    // Every name written is the manifest itself or a name the validator matched against the BundleEntryNames
    // whitelist, so no entry can leave the report folder.
    string Save(byte[] bundle, BugReportManifest manifest)
    {
        string folder = Path.Combine(root, manifest.ReportId);
        Directory.CreateDirectory(folder);
        WriteFile(folder, "bundle.zip", bundle);

        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var archive = new ZipArchive(new MemoryStream(bundle, writable: false), ZipArchiveMode.Read))
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                using Stream stream = entry.Open();
                var data = new MemoryStream((int)entry.Length);
                stream.CopyTo(data);
                entries.Add(entry.FullName, data.ToArray());
            }
        }

        WriteFile(folder, BundleEntryNames.Manifest, entries[BundleEntryNames.Manifest]);
        var sections = new List<CapturedSection>();
        foreach (BundleEntry entry in manifest.Entries)
        {
            byte[] data = entries[entry.Name];
            WriteFile(folder, entry.Name, data);
            if (entry.Name.StartsWith("sections/", StringComparison.Ordinal))
                sections.Add(new CapturedSection(entry.Name["sections/".Length..^".json".Length], data));
        }

        ClipIndex? clip = entries.TryGetValue(BundleEntryNames.ClipIndex, out byte[]? index) ? BugReportJson.DeserializeClipIndex(index) : null;
        IReadOnlyList<string> logTail = entries.TryGetValue(LogSection, out byte[]? log) ? LogTail(log) : [];
        string page = BugReportViewerPage.Render(new BugReportViewerModel(manifest, clip, sections, null, null, logTail));
        return WriteFile(folder, "viewer.html", Encoding.UTF8.GetBytes(page));
    }

    static string WriteFile(string folder, string name, byte[] data)
    {
        string path = Path.Combine(folder, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, overwrite: true);
        return path;
    }

    // The log section is {"lines":[...]}. Anything else, or a line that is not a string, gives no lines.
    static IReadOnlyList<string> LogTail(byte[] json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, BugReportJson.SectionDocumentOptions);
            if (!document.RootElement.TryGetProperty("lines", out JsonElement lines) || lines.ValueKind != JsonValueKind.Array)
                return [];
            var tail = new List<string>();
            foreach (JsonElement line in lines.EnumerateArray())
                if (line.ValueKind == JsonValueKind.String) tail.Add(line.GetString()!);
            return tail.Count > LogTailLines ? tail.GetRange(tail.Count - LogTailLines, LogTailLines) : tail;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return [];
        }
    }
}
