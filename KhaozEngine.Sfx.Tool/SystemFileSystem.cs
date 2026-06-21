using System;
using System.IO;

namespace KhaozEngine.Sfx;

/// <summary>Real <see cref="ISfxFileSystem"/> over <see cref="System.IO"/>.</summary>
public sealed class SystemFileSystem : ISfxFileSystem
{
    /// <inheritdoc/>
    public bool FileExists(string path) => File.Exists(path);
    /// <inheritdoc/>
    public string? TryReadText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
    /// <inheritdoc/>
    public string ReadAllText(string path) => File.ReadAllText(path);

    /// <inheritdoc/>
    public void WriteAllBytes(string path, byte[] data)
    {
        EnsureDirectoryFor(path);
        File.WriteAllBytes(path, data);
    }

    /// <inheritdoc/>
    public void WriteAllText(string path, string text)
    {
        EnsureDirectoryFor(path);
        File.WriteAllText(path, text);
    }

    /// <inheritdoc/>
    public void EnsureDirectoryFor(string filePath)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    /// <inheritdoc/>
    public string NewTempPath(string suffix) =>
        Path.Combine(Path.GetTempPath(), "ke-sfxbake-" + Guid.NewGuid().ToString("N") + suffix);

    /// <inheritdoc/>
    public void DeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* best-effort temp cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }
}
