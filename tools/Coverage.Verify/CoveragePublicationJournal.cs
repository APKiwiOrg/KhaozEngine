using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace KhaozEngine.Tools.Coverage;

/// <summary>One mutable output of a transaction. Old fields are null when the destination did not exist. The old Unix
/// mode is also null on Windows.</summary>
internal sealed record CoveragePublicationEntry(CoveragePublicationStep Step, string Destination, string Stage, string Backup,
    string Marker, long? OldLength, string? OldSha256, long NewLength, string NewSha256, int? OldMode = null);
internal sealed record CoveragePublicationPart(string Destination, long ByteLength, string Sha256, bool Created);

/// <summary>The local recovery record written beside the commit root before any mutable output is replaced. It
/// names local output locations and is never public evidence. The last entry is the commit point.</summary>
internal sealed record CoveragePublicationRecord(string Transaction, IReadOnlyList<CoveragePublicationEntry> Entries,
    IReadOnlyList<CoveragePublicationPart> Parts, IReadOnlyList<string> Directories)
{
    internal string JournalPath => Entries[^1].Destination + CoveragePublicationJournal.JournalSuffix;
}

internal static class CoveragePublicationJournal
{
    internal const string JournalSuffix = ".coverage-journal", MarkerSuffix = ".coverage-incomplete";
    const string Schema = "khaozengine.coverage-publication-journal/1";

    /// <summary>Refuses a root, table or diagnostic output that belongs to an incomplete transaction, whatever bytes
    /// it holds. Run the tool again on the same evidence path, or <see cref="CoveragePublisher.Recover"/>, first.</summary>
    internal static void EnsureReadable(string rootPath)
    {
        string root = Path.GetFullPath(rootPath);
        if (File.Exists(root + MarkerSuffix) || File.Exists(root + JournalSuffix))
            throw Required("An output belongs to an incomplete publication.");
    }

    internal static CoverageBuildValidationException Required(string message, Exception? inner = null) =>
        new(CoverageBuildValidationRefusal.OutputRecoveryRequired, message, inner);

    internal static FileStream CreatePrivate(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    internal static void Write(Stream output, CoveragePublicationRecord record)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString("schema", Schema); writer.WriteString("transaction", record.Transaction);
        writer.WriteStartArray("entries");
        foreach (var entry in record.Entries)
        {
            writer.WriteStartObject();
            writer.WriteString("step", entry.Step.ToString()); writer.WriteString("destination", entry.Destination);
            if (entry.OldSha256 == null) { writer.WriteNull("oldLength"); writer.WriteNull("oldSha256"); }
            else { writer.WriteNumber("oldLength", entry.OldLength!.Value); writer.WriteString("oldSha256", entry.OldSha256); }
            writer.WriteNumber("newLength", entry.NewLength); writer.WriteString("newSha256", entry.NewSha256);
            if (entry.OldMode is { } mode) writer.WriteNumber("oldMode", mode); else writer.WriteNull("oldMode");
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("parts");
        foreach (var part in record.Parts)
        {
            writer.WriteStartObject();
            writer.WriteString("destination", part.Destination); writer.WriteNumber("byteLength", part.ByteLength);
            writer.WriteString("sha256", part.Sha256); writer.WriteBoolean("created", part.Created);
            writer.WriteEndObject();
            writer.Flush();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("directories");
        foreach (string directory in record.Directories) writer.WriteStringValue(directory);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>Stage, backup and marker names are derived from each destination and the transaction, never read,
    /// so a journal cannot direct recovery at an unrelated file.</summary>
    internal static CoveragePublicationEntry Entry(CoveragePublicationStep step, string destination, string transaction,
        long? oldLength, string? oldSha256, long newLength, string newSha256) =>
        new(step, destination, destination + "." + transaction + ".tmp", destination + "." + transaction + ".bak",
            destination + MarkerSuffix, oldLength, oldSha256, newLength, newSha256);

    /// <summary>Reads and validates the journal beside <paramref name="root"/> before any path in it is touched.</summary>
    internal static CoveragePublicationRecord Read(string root)
    {
        try
        {
            using var document = CoverageBuildInputJson.Document(CoverageBuildInputJson.ReadBytes(root + JournalSuffix));
            var value = document.RootElement;
            CoverageBuildInputJson.Members(value, "$", "schema", "transaction", "entries", "parts", "directories");
            string transaction = CoverageBuildInputJson.Text(value, "transaction", "$");
            if (CoverageBuildInputJson.Text(value, "schema", "$") != Schema || transaction.Length != 32
                || transaction.Any(c => !char.IsAsciiHexDigitLower(c) && !char.IsAsciiDigit(c))) throw Required("Unknown publication journal.");
            var entries = Array(value, "entries").Select(e =>
            {
                CoverageBuildInputJson.Members(e, "$.entries", "step", "destination", "oldLength", "oldSha256", "newLength", "newSha256", "oldMode");
                var step = Enum.Parse<CoveragePublicationStep>(CoverageBuildInputJson.Text(e, "step", "$.entries"));
                if (step is not (CoveragePublicationStep.ReplaceTable or CoveragePublicationStep.ReplaceDiagnostic or CoveragePublicationStep.ReplaceEvidence))
                    throw Required("Unknown publication step.");
                bool absent = CoverageBuildInputJson.Get(e, "oldSha256", "$.entries").ValueKind == JsonValueKind.Null;
                if (absent != (CoverageBuildInputJson.Get(e, "oldLength", "$.entries").ValueKind == JsonValueKind.Null)) throw Required("Inconsistent old state.");
                var modeValue = CoverageBuildInputJson.Get(e, "oldMode", "$.entries");
                int? mode = modeValue.ValueKind == JsonValueKind.Null ? null : modeValue.GetInt32();
                if (mode is < 0 or > 4095 || absent && mode != null) throw Required("Invalid old mode.");
                return Entry(step, Owned(CoverageBuildInputJson.Text(e, "destination", "$.entries")), transaction,
                    absent ? null : e.GetProperty("oldLength").GetInt64(), absent ? null : CoverageBuildInputJson.Digest(e.GetProperty("oldSha256"), "$.entries"),
                    e.GetProperty("newLength").GetInt64(), CoverageBuildInputJson.Digest(e.GetProperty("newSha256"), "$.entries")) with { OldMode = mode };
            }).ToArray();
            if (entries.Length == 0 || entries[^1].Destination != Path.GetFullPath(root)
                || entries.Select(e => e.Destination).Distinct(StringComparer.Ordinal).Count() != entries.Length) throw Required("Journal does not belong to this root.");
            // Parts and generation directories must lie inside a recorded root's own parts directory.
            string[] generations = entries.Select(e => e.Destination + ".parts").ToArray();
            bool Inside(string path, bool allowRoot) => generations.Any(g => allowRoot && path == g || path.StartsWith(g + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            var parts = Array(value, "parts").Select(p =>
            {
                CoverageBuildInputJson.Members(p, "$.parts", "destination", "byteLength", "sha256", "created");
                string destination = Owned(CoverageBuildInputJson.Text(p, "destination", "$.parts"));
                if (!Inside(destination, false)) throw Required("Journal part lies outside its set.");
                return new CoveragePublicationPart(destination, p.GetProperty("byteLength").GetInt64(),
                    CoverageBuildInputJson.Digest(p.GetProperty("sha256"), "$.parts"), p.GetProperty("created").GetBoolean());
            }).ToArray();
            string[] directories = Array(value, "directories").Select(d => Owned(d.GetString()!)).ToArray();
            if (directories.Any(d => !Inside(d, true))) throw Required("Journal directory lies outside its set.");
            return new(transaction, ImmutableArray.Create(entries), ImmutableArray.Create(parts), ImmutableArray.Create(directories));
        }
        catch (Exception error) when (error is not CoverageBuildValidationException && error is JsonException or CoverageBuildDataException
            or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or FormatException or KeyNotFoundException)
        { throw Required("The publication journal cannot be read.", error); }
    }

    static IEnumerable<JsonElement> Array(JsonElement value, string name)
    {
        var array = CoverageBuildInputJson.Get(value, name, "$");
        if (array.ValueKind != JsonValueKind.Array) throw Required("Malformed publication journal.");
        return array.EnumerateArray().ToArray();
    }

    static string Owned(string path) => Path.IsPathFullyQualified(path) && Path.GetFullPath(path) == path
        ? path : throw Required("Journal path is not a canonical absolute path.");

    /// <summary>Finishes or undoes the transaction recorded beside <paramref name="root"/>. When every output holds its
    /// new bytes and every part verifies, the commit happened and only recovery files are removed. Otherwise each
    /// output holding its new bytes is restored from its checked backup, or removed when it did not exist before.
    /// Any output holding neither recorded state refuses before anything is touched.</summary>
    internal static void Recover(string root)
    {
        root = Path.GetFullPath(root);
        if (!File.Exists(root + JournalSuffix))
        {
            if (File.Exists(root + MarkerSuffix)) throw Required("An incomplete marker has no journal.");
            return;
        }
        var record = Read(root);
        try
        {
            var states = record.Entries.Select(e => State(e.Destination)).ToArray();
            bool committed = record.Entries.Select((e, i) => Matches(states[i], e.NewLength, e.NewSha256)).All(m => m)
                && record.Parts.All(p => Matches(State(p.Destination), p.ByteLength, p.Sha256));
            if (!committed)
            {
                var restore = new bool[record.Entries.Count];
                for (int i = 0; i < restore.Length; i++)
                {
                    var entry = record.Entries[i];
                    if (Matches(states[i], entry.OldLength, entry.OldSha256)) continue;
                    if (!Matches(states[i], entry.NewLength, entry.NewSha256)) throw Required("An output changed after the failed publication.");
                    if (entry.OldSha256 != null && !Matches(State(entry.Backup), entry.OldLength, entry.OldSha256)) throw Required("A backup is missing or changed.");
                    restore[i] = true;
                }
                for (int i = restore.Length - 1; i >= 0; i--)
                {
                    if (!restore[i]) continue;
                    Restore(record.Entries[i]);
                }
                foreach (var part in record.Parts.Where(p => p.Created && Matches(State(p.Destination), p.ByteLength, p.Sha256))) File.Delete(part.Destination);
                foreach (string directory in record.Directories.Reverse())
                    if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            foreach (var entry in record.Entries) { Remove(entry.Stage); Remove(entry.Backup); }
            foreach (var part in record.Parts) Remove(part.Destination + "." + record.Transaction + ".tmp");
            foreach (var entry in record.Entries) Remove(entry.Marker);
            File.Delete(record.JournalPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw Required("Recovery could not finish. Its journal and backups are kept.", error); }
    }

    /// <summary>Puts an output back as it was before the transaction. The backup waits with owner-only permissions,
    /// so the old Unix mode is reapplied before it moves into place. Hard links to the old file are not restored.</summary>
    internal static void Restore(CoveragePublicationEntry entry)
    {
        if (entry.OldSha256 == null) { File.Delete(entry.Destination); return; }
        if (entry.OldMode is { } mode && !OperatingSystem.IsWindows()) File.SetUnixFileMode(entry.Backup, (UnixFileMode)mode);
        File.Move(entry.Backup, entry.Destination, true);
    }

    internal static void Remove(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Null for an absent file. A directory or other non-file never matches a recorded state.</summary>
    internal static (long Length, string Sha256)? State(string path)
    {
        if (Directory.Exists(path)) return (-1, "");
        if (!File.Exists(path)) return null;
        return (new FileInfo(path).Length, CoverageArtifactSet.HashFile(path));
    }

    internal static bool Matches((long Length, string Sha256)? state, long? length, string? sha256) =>
        sha256 == null ? state == null : state is { } value && value.Length == length && value.Sha256 == sha256;
}
