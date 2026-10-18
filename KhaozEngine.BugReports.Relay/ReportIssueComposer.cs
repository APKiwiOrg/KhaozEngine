using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports.Relay;

internal sealed class ReportIssueComposer
{
    readonly BugReportRelayOptions _options;

    public ReportIssueComposer(BugReportRelayOptions options) => _options = options;

    public GitHubIssueDraft ComposeIssue(IssueInput input)
    {
        string start = ComposeStart(input);
        string files = ComposeFiles(input, collapse: false);
        string log = ComposeLog(input.LogTail);
        string body = start + files + log;
        if (body.Length > IssueLimits.MaxBodyChars)
        {
            files = ComposeFiles(input, collapse: true);
            body = start + files + log;
        }
        if (body.Length > IssueLimits.MaxBodyChars && log.Length > 0)
            body = start + files + "\nLog tail omitted for size. Open the viewer.\n";
        body = IssueBodyBudget.Finish(body, input.Report, Url(input, ReportLayout.ViewerFile));
        string? version = IssueFactReader.Read(input, new IssueFact("Game version", "build", "version"));
        return new GitHubIssueDraft(IssueTitle.Compose(_options.GameDisplayName, version, input.Report.Manifest.Description),
            body, _options.Labels);
    }

    public string ComposeSnapshotComment(IssueInput input)
    {
        var result = new StringBuilder("### Server snapshot\n\nReceived for report ")
            .Append(input.Report.Reference).Append(".\n\n");
        var rows = new StringBuilder();
        IssueInput snapshotInput = input with { Sections = Array.Empty<CapturedSection>() };
        foreach (IssueFact fact in _options.IssueFacts.Take(IssueLimits.MaxFacts))
        {
            if (input.Snapshot is null || !input.Snapshot.Sections.ContainsKey(fact.Section)) continue;
            string? value = IssueFactReader.Read(snapshotInput, fact);
            if (value is not null) AppendRow(rows, IssueFactText.Limit(fact.Label), IssueMarkdown.TableCell(value));
        }
        if (rows.Length > 0) result.Append("| Field | Value |\n| --- | --- |\n").Append(rows).Append('\n');
        AppendFile(result, input, "Server snapshot", ReportLayout.ServerSnapshotFile);
        AppendFile(result, input, "Viewer", ReportLayout.ViewerFile);
        return result.ToString();
    }

    string ComposeStart(IssueInput input)
    {
        ReportRecord report = input.Report;
        BugReportManifest manifest = report.Manifest;
        var result = new StringBuilder("### What happened\n\n").Append(IssueMarkdown.Quote(manifest.Description))
            .Append("\n\n### Report\n\n| Field | Value |\n| --- | --- |\n");
        AppendRow(result, "Reporter", IssueMarkdown.TableCell(report.Reporter.DisplayName));
        if (report.Reporter.Handle is not null) AppendRow(result, "Handle", IssueMarkdown.InlineCode(report.Reporter.Handle));
        AppendRow(result, "Account", IssueMarkdown.InlineCode(report.Reporter.Subject));
        AppendRow(result, "Reference", report.Reference);
        AppendRow(result, "Category", IssueMarkdown.TableCell(manifest.Category));
        AppendRow(result, "Captured", manifest.CapturedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC");
        foreach (IssueFact fact in _options.IssueFacts.Take(IssueLimits.MaxFacts))
        {
            string? value = IssueFactReader.Read(input, fact);
            if (value is not null) AppendRow(result, IssueFactText.Limit(fact.Label), IssueMarkdown.TableCell(value));
        }
        string? screenshot = manifest.Entries.FirstOrDefault(e => e.Name is BundleEntryNames.ScreenshotPng or BundleEntryNames.ScreenshotJpeg)?.Name;
        if (screenshot is not null)
            result.Append("\n### Screenshot\n\n![Screenshot](").Append(Url(input, screenshot)).Append(")\n");
        AppendClip(result, input);
        return result.ToString();
    }

    void AppendClip(StringBuilder result, IssueInput input)
    {
        bool hasMp4 = input.Report.Manifest.Entries.Any(e => e.Name == BundleEntryNames.ClipMp4);
        // An MP4 index carries timing records rather than thumbnail images.
        IReadOnlyList<ClipIndexFrame>? frames = hasMp4 ? null : input.Clip?.Frames;
        int count = frames?.Count ?? 0;
        if (count == 0 && !hasMp4) return;
        string viewer = Url(input, ReportLayout.ViewerFile);
        result.Append("\n### Clip\n\n");
        int thumbnails = Math.Min(count, IssueLimits.ClipThumbnails);
        for (int i = 0; i < thumbnails; i++)
        {
            int index = count <= IssueLimits.ClipThumbnails ? i
                : (int)Math.Round(i * (count - 1) / (double)(IssueLimits.ClipThumbnails - 1), MidpointRounding.AwayFromZero);
            if (i > 0) result.Append(' ');
            result.Append("<a href=\"").Append(viewer).Append("\"><img src=\"").Append(Url(input, frames![index].Entry))
                .Append("\" width=\"").Append(IssueLimits.ThumbnailWidth.ToString(CultureInfo.InvariantCulture)).Append("\"></a>");
        }
        if (thumbnails > 0) result.Append("\n\n");
        result.Append("[Play the clip](").Append(viewer).Append(")\n");
    }

    string ComposeFiles(IssueInput input, bool collapse)
    {
        var result = new StringBuilder("\n### Files\n\n");
        AppendFile(result, input, "Viewer", ReportLayout.ViewerFile);
        BundleEntry[] sections = input.Report.Manifest.Entries
            .Where(e => e.Name.StartsWith("sections/", StringComparison.Ordinal) && e.Name.EndsWith(".json", StringComparison.Ordinal)).ToArray();
        bool snapshot = input.Report.ServerSnapshot == ServerSnapshotState.InBody;
        if (collapse)
        {
            int count = sections.Length + (snapshot ? 1 : 0);
            result.Append("- ").Append(count.ToString(CultureInfo.InvariantCulture)).Append(" more files in the report folder\n");
        }
        else
        {
            foreach (BundleEntry section in sections) AppendFile(result, input, section.Name, section.Name);
            if (snapshot) AppendFile(result, input, "Server snapshot", ReportLayout.ServerSnapshotFile);
        }
        return result.ToString();
    }

    static string ComposeLog(IReadOnlyList<string> logTail)
    {
        if (logTail.Count == 0) return "";
        string[] lines = logTail.Skip(Math.Max(0, logTail.Count - IssueLimits.LogTailLines)).Select(line =>
        {
            if (line.Length <= IssueLimits.MaxLogLineChars) return line;
            int length = IssueLimits.MaxLogLineChars;
            if (char.IsHighSurrogate(line[length - 1]) && char.IsLowSurrogate(line[length])) length--;
            return line[..length];
        }).ToArray();
        return new StringBuilder("\n<details>\n<summary>Last 50 log lines</summary>\n\n")
            .Append(IssueMarkdown.Fenced(lines)).Append("\n\n</details>\n").ToString();
    }

    static void AppendRow(StringBuilder result, string label, string value) =>
        result.Append("| ").Append(IssueMarkdown.TableCell(label)).Append(" | ").Append(value).Append(" |\n");

    void AppendFile(StringBuilder result, IssueInput input, string label, string entry) =>
        result.Append("- [").Append(label).Append("](").Append(Url(input, entry)).Append(")\n");

    string Url(IssueInput input, string entry) =>
        ReportLayout.PublicUrl(_options.PublicMediaBase, ReportLayout.Entry(input.Report.Folder, entry)).AbsoluteUri;
}
