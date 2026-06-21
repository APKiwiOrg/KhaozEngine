namespace KhaozEngine.Sfx;

/// <summary>
/// Filesystem seam used by the bake command. Extends the planner's read-only probe with the writes a real
/// bake needs, so the whole flow is unit-testable against an in-memory fake.
/// </summary>
public interface ISfxFileSystem : ISfxFileProbe
{
    /// <summary>Reads a file's full text. Throws if it does not exist.</summary>
    string ReadAllText(string path);
    /// <summary>Writes bytes, creating parent directories as needed.</summary>
    void WriteAllBytes(string path, byte[] data);
    /// <summary>Writes text, creating parent directories as needed.</summary>
    void WriteAllText(string path, string text);
    /// <summary>Ensures the parent directory of <paramref name="filePath"/> exists.</summary>
    void EnsureDirectoryFor(string filePath);
    /// <summary>Returns a fresh temp file path with the given suffix (e.g. ".mp3").</summary>
    string NewTempPath(string suffix);
    /// <summary>Deletes a file if present (best-effort temp cleanup).</summary>
    void DeleteFile(string path);
}
