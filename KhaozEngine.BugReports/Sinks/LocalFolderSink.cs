using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
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
/// validator's error and nothing is written. Any other failure, such as a file system error, is Queued with
/// <see cref="WriteFailedError"/>, so the bundle is kept. Only the caller's cancellation throws.
/// </remarks>
public sealed class LocalFolderSink : IBugReportSink
{
    /// <summary>The error of a Queued result when the report could not be written.</summary>
    public const string WriteFailedError = "write_failed";

    const string LogSection = "sections/log.json";

    readonly string root;
    readonly bool openViewer;
    readonly Func<BugReportViewerModel, string> render;
    readonly Func<Uri, CancellationToken, Task<bool>> launch;

    /// <summary>Creates a sink that saves reports under one folder.</summary>
    /// <param name="root">The folder that holds one subfolder per report. It is created when missing.</param>
    /// <param name="openViewer">Whether to open each saved report's viewer page in the system browser.</param>
    public LocalFolderSink(string root, bool openViewer)
        : this(root, openViewer, BugReportViewerPage.Render)
    {
    }

    // Tests replace the page renderer to reach the unexpected failure path, and the browser launch to see its address.
    internal LocalFolderSink(string root, bool openViewer, Func<BugReportViewerModel, string> render,
        Func<Uri, CancellationToken, Task<bool>>? launch = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(render);
        this.root = Path.GetFullPath(root);
        this.openViewer = openViewer;
        this.render = render;
        this.launch = launch ?? Browser.LaunchBrowserAsync;
    }

    /// <inheritdoc />
    public async Task<BugReportSubmitResult> SubmitAsync(BugReportSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        cancellationToken.ThrowIfCancellationRequested();

        BugReportManifest manifest;
        Uri viewer;
        try
        {
            byte[] bundle = submission.Bundle.ToArray();
            BundleValidationResult validation = BundleValidator.Validate(new MemoryStream(bundle, writable: false));
            if (!validation.IsValid || validation.Manifest is null)
                return new BugReportSubmitResult(BugReportSubmitStatus.Rejected, null, validation.Error, null);
            manifest = validation.Manifest;
            string viewerPath = Save(bundle, manifest);
            // The builder escapes # and spaces but keeps % as an escape, so % is escaped first. A folder name holding
            // either stays part of the path.
            viewer = new UriBuilder
            {
                Scheme = Uri.UriSchemeFile,
                Host = "",
                Path = viewerPath.Replace("%", "%25", StringComparison.Ordinal),
            }.Uri;
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Log.Warn("Could not save bug report " + submission.ReportId + " to its local folder.", e);
            return new BugReportSubmitResult(BugReportSubmitStatus.Queued, null, WriteFailedError, null);
        }

        if (openViewer)
            await launch(viewer, cancellationToken).ConfigureAwait(false);
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
        IReadOnlyList<string> logTail = entries.TryGetValue(LogSection, out byte[]? log) ? BugReportLogTail.FromLogSection(log) : [];
        string page = render(new BugReportViewerModel(manifest, clip, sections, null, null, logTail));
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
}
