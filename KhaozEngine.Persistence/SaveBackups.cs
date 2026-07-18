using System.IO;

namespace KhaozEngine.Persistence;

/// <summary>
/// Rotates numbered backup generations of a save file alongside its primary path. Generation 0 is the
/// primary path itself, generation n (n >= 1) is <c>path + ".bak" + n</c>.
/// </summary>
public static class SaveBackups
{
    /// <summary>Returns the path for <paramref name="generation"/> of <paramref name="path"/>: the primary path itself for generation 0, or <c>path + ".bak" + generation</c> for generation n >= 1.</summary>
    public static string GenerationPath(string path, int generation)
        => generation <= 0 ? path : path + ".bak" + generation;

    /// <summary>
    /// Shifts existing backup generations of <paramref name="path"/> up by one slot, dropping whatever
    /// sits at the oldest kept generation, then copies (not moves) the current primary into generation 1.
    /// The primary is left in place throughout: a write that fails after this call still finds an intact
    /// primary at <paramref name="path"/>, because the new content only ever replaces it on success.
    /// No-op when <paramref name="generations"/> is not positive or the primary does not exist.
    /// </summary>
    public static void Rotate(string path, int generations)
    {
        if (generations <= 0 || !File.Exists(path))
        {
            return;
        }

        string oldest = GenerationPath(path, generations);
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (int n = generations - 1; n >= 1; n--)
        {
            string source = GenerationPath(path, n);
            if (File.Exists(source))
            {
                File.Move(source, GenerationPath(path, n + 1));
            }
        }

        File.Copy(path, GenerationPath(path, 1), overwrite: true);
    }
}
