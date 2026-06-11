using System;
using System.IO;
using System.Text.Json;
using KhaozEngine.App;

namespace KhaozEngine.Persistence;

/// <summary>
/// Writes text or JSON to disk crash-safely: content goes to a sibling <c>.tmp</c> file which is then
/// moved over the target, so a crash mid-write never leaves a half-written destination. Synchronous and
/// <b>throws</b> on IO failure; the caller decides whether to catch. For fire-and-forget background
/// writes that coalesce and retry, use <see cref="PersistenceQueue"/>.
/// </summary>
public static class AtomicJsonWriter
{
    private static readonly JsonSerializerOptions DefaultOptions = new() { WriteIndented = true };

    /// <summary>Atomically writes <paramref name="contents"/> to <paramref name="path"/>, creating the parent directory if needed.</summary>
    public static void WriteText(string path, string contents)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(contents);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, contents);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>Serializes <paramref name="value"/> to JSON (indented by default) and atomically writes it to <paramref name="path"/>.</summary>
    public static void Write<T>(string path, T value, JsonSerializerOptions? options = null)
        => WriteText(path, JsonSerializer.Serialize(value, options ?? DefaultOptions));

    /// <summary>Atomically writes <paramref name="contents"/> to <paramref name="fileName"/> inside the app-data directory.</summary>
    public static void WriteText(AppDataPaths paths, string fileName, string contents)
    {
        ArgumentNullException.ThrowIfNull(paths);
        WriteText(paths.GetFilePath(fileName), contents);
    }

    /// <summary>Serializes <paramref name="value"/> and atomically writes it to <paramref name="fileName"/> inside the app-data directory.</summary>
    public static void Write<T>(AppDataPaths paths, string fileName, T value, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Write(paths.GetFilePath(fileName), value, options);
    }
}
