using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Goldens;

/// <summary>
/// One checked-in golden file and the expectations <c>goldens.json</c> records beside it.
/// </summary>
internal sealed class GoldenFile
{
    /// <summary>The version directory the file lives in, <c>v1</c> for the first format version.</summary>
    public required string Version { get; init; }

    /// <summary>The file name, which is also its key in <c>goldens.json</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Which of the four formats this is: <c>chunk</c>, <c>manifest</c>, <c>rules</c> or <c>text</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The content address, over the canonical bytes for a chunk and over the canonical text for a manifest.</summary>
    public required string Hash { get; init; }

    /// <summary>The canonical uncompressed length, which is not the stored length when the body compressed.</summary>
    public required int CanonicalLength { get; init; }

    /// <summary>The file exactly as it is checked in, compressed where the publisher compressed it.</summary>
    public required byte[] Stored { get; init; }

    /// <summary>The whole <c>goldens.json</c> entry, which carries the decoded field values per kind.</summary>
    public required JsonElement Values { get; init; }

    /// <summary>The file's path in the test output, for an assertion message that names something findable.</summary>
    public required string Path { get; init; }

    /// <inheritdoc />
    public override string ToString() => Version + "/" + Name;
}

/// <summary>
/// Loads the golden sets out of the test output, one set per format version directory. The csproj copies
/// <c>Goldens/v*/**</c> beside the assembly, so a NEW version directory is picked up by being added and by
/// nothing else.
/// <para>
/// The parsed <c>goldens.json</c> documents are held for the life of the process on purpose: every
/// <see cref="GoldenFile.Values"/> is a view into one of them, and disposing a document would invalidate
/// every expectation taken from it.
/// </para>
/// </summary>
internal static class GoldenLibrary
{
    /// <summary>The expectations file each version directory carries, which is not itself a golden.</summary>
    public const string ExpectationsFileName = "goldens.json";

    static readonly List<JsonDocument> Documents = new();

    static GoldenLibrary()
    {
        Root = System.IO.Path.Combine(AppContext.BaseDirectory, "Goldens");
        var versions = new List<string>();
        var files = new List<GoldenFile>();
        foreach (string directory in Directory.GetDirectories(Root))
        {
            string version = System.IO.Path.GetFileName(directory);
            versions.Add(version);
            files.AddRange(Load(directory, version));
        }

        versions.Sort(StringComparer.Ordinal);
        Versions = versions;
        Files = files;
    }

    /// <summary>The directory the version sets sit under, in the test output.</summary>
    public static string Root { get; }

    /// <summary>Every format version directory present, ascending ordinal.</summary>
    public static IReadOnlyList<string> Versions { get; }

    /// <summary>Every golden of every version.</summary>
    public static IReadOnlyList<GoldenFile> Files { get; }

    /// <summary>Every golden as theory data, one case per file, so a red case names the file it read.</summary>
    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (GoldenFile file in Files)
        {
            data.Add(file.Version, file.Name);
        }

        return data;
    }

    /// <summary>The goldens of one version, in the order <c>goldens.json</c> lists them.</summary>
    public static IReadOnlyList<GoldenFile> Of(string version)
    {
        var matches = new List<GoldenFile>();
        foreach (GoldenFile file in Files)
        {
            if (string.Equals(file.Version, version, StringComparison.Ordinal))
            {
                matches.Add(file);
            }
        }

        return matches;
    }

    /// <summary>One golden by version and name.</summary>
    public static GoldenFile Get(string version, string name)
    {
        foreach (GoldenFile file in Files)
        {
            if (string.Equals(file.Version, version, StringComparison.Ordinal)
                && string.Equals(file.Name, name, StringComparison.Ordinal))
            {
                return file;
            }
        }

        throw new InvalidOperationException(string.Create(
            CultureInfo.InvariantCulture,
            $"No golden '{name}' in version '{version}'. The set is loaded from {Root}."));
    }

    static IEnumerable<GoldenFile> Load(string directory, string version)
    {
        string expectationsPath = System.IO.Path.Combine(directory, ExpectationsFileName);
        JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(expectationsPath));
        Documents.Add(document);

        foreach (JsonElement entry in document.RootElement.GetProperty("files").EnumerateArray())
        {
            string name = entry.GetProperty("file").GetString()!;
            string path = System.IO.Path.Combine(directory, name);
            yield return new GoldenFile
            {
                Version = version,
                Name = name,
                Kind = entry.GetProperty("kind").GetString()!,
                Hash = entry.GetProperty("hash").GetString()!,
                CanonicalLength = entry.GetProperty("canonicalLength").GetInt32(),
                Stored = File.ReadAllBytes(path),
                Values = entry,
                Path = path,
            };
        }
    }
}
