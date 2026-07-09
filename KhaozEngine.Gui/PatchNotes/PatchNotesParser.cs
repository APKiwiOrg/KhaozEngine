using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace KhaozEngine.Gui;

/// <summary>
/// Parses a player-facing changelog written in the shared <c>docs/CHANGELOG-STYLE.md</c> shape
/// (<c># Title</c>, <c>## YYYY-MM-DD</c> dates, <c>### Build X.Y.Z (Name)</c> builds, <c>- **Category**</c>
/// groups, indented note bullets with wrapped continuation lines) into a <see cref="PatchNotesDocument"/>.
/// A line-based state machine: it never throws, degrading unrecognised input to <see cref="PatchNoteCategory.Other"/>
/// groups or an <see cref="PatchNotesDocument.Empty"/> document rather than failing.
/// </summary>
public static class PatchNotesParser
{
    static readonly Regex BuildHeader = new(@"^###\s+Build\s+(\S+)\s+\((.+)\)\s*$", RegexOptions.Compiled);
    static readonly Regex DateHeader = new(@"^##\s+(\d{4}-\d{2}-\d{2})\s*$", RegexOptions.Compiled);
    static readonly Regex CategoryLine = new(@"^-\s+\*\*(.+?)\*\*\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Parses <paramref name="text"/> into a <see cref="PatchNotesDocument"/>. Never throws: null, blank, or
    /// unrecognisable input yields <see cref="PatchNotesDocument.Empty"/>, and any line that does not match a
    /// known shape is either folded into the current note or ignored.
    /// </summary>
    public static PatchNotesDocument Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return PatchNotesDocument.Empty;

        var title = string.Empty;
        var currentDate = string.Empty;
        var builds = new List<PatchNotesBuild>();

        List<PatchNoteGroup>? groups = null;   // non-null while a build is open
        string? version = null, name = null, date = null;
        var category = PatchNoteCategory.Other;
        List<PatchNote>? notes = null;         // non-null while a group is open
        StringBuilder? pending = null;         // the note currently accumulating text

        void FlushNote()
        {
            if (pending is null || notes is null) return;
            notes.Add(new PatchNote(SplitSpans(pending.ToString())));
            pending = null;
        }
        void CloseGroup()
        {
            FlushNote();
            if (notes is { Count: > 0 } && groups is not null)
                groups.Add(new PatchNoteGroup(category, notes));
            notes = null;
        }
        void CloseBuild()
        {
            CloseGroup();
            if (version is not null && groups is not null)
                builds.Add(new PatchNotesBuild(version, name ?? string.Empty, date ?? string.Empty, groups));
            groups = null; version = null; name = null; date = null;
            category = PatchNoteCategory.Other;
        }
        void StartNote(string content)
        {
            if (groups is null) return;        // stray text outside any build: ignore
            FlushNote();
            notes ??= new List<PatchNote>();
            pending = new StringBuilder(content.Trim());
        }

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            var trimmed = line.TrimStart();
            var indented = line.Length > trimmed.Length;

            if (trimmed.StartsWith("### ", StringComparison.Ordinal))
            {
                CloseBuild();
                var m = BuildHeader.Match(trimmed);
                if (m.Success)
                {
                    groups = new List<PatchNoteGroup>();
                    version = m.Groups[1].Value;
                    name = m.Groups[2].Value;
                    date = currentDate;
                }
                continue;
            }
            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                var m = DateHeader.Match(trimmed);
                if (m.Success) currentDate = m.Groups[1].Value;
                continue;
            }
            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                if (title.Length == 0) title = trimmed[2..].Trim();
                continue;
            }
            if (trimmed == "---") continue;

            if (!indented)
            {
                var cat = CategoryLine.Match(trimmed);
                if (cat.Success) { CloseGroup(); category = MapCategory(cat.Groups[1].Value); continue; }
            }
            if (trimmed.StartsWith("- ", StringComparison.Ordinal)) { StartNote(trimmed[2..]); continue; }

            if (pending is not null && indented) { pending.Append(' ').Append(trimmed); continue; }
            if (groups is not null) StartNote(trimmed);
        }
        CloseBuild();
        return new PatchNotesDocument(title, builds);
    }

    static PatchNoteCategory MapCategory(string label) => label.Trim().ToLowerInvariant() switch
    {
        "new" => PatchNoteCategory.New,
        "major" => PatchNoteCategory.Major,
        "minor" => PatchNoteCategory.Minor,
        "rebalance" or "balance" => PatchNoteCategory.Rebalance,
        "bug" => PatchNoteCategory.Bug,
        _ => PatchNoteCategory.Other,
    };

    static IReadOnlyList<PatchNoteSpan> SplitSpans(string text)
    {
        var parts = text.Split('`');
        if (parts.Length % 2 == 0) return new[] { new PatchNoteSpan(text, false) };
        var spans = new List<PatchNoteSpan>();
        for (var i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0) spans.Add(new PatchNoteSpan(parts[i], i % 2 == 1));
        if (spans.Count == 0) spans.Add(new PatchNoteSpan(string.Empty, false));
        return spans;
    }
}
