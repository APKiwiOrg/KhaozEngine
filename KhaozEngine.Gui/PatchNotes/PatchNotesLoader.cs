using System;
using System.IO;
using System.Reflection;

namespace KhaozEngine.Gui;

/// <summary>
/// Loads the player-facing changelog (<c>docs/PLAY_CHANGELOG.md</c>, copied next to the running app as
/// <see cref="ResourceName"/>): a disk copy wins if present, else an embedded resource in the given
/// assembly, else <see cref="PatchNotesDocument.Empty"/>. Mirrors <c>KhaozEngine.Content.ConfigLoader</c>'s
/// disk-then-embedded convention without adding a package dependency from Gui to Content. Unlike
/// <c>ConfigLoader</c>, this never throws: every IO attempt is wrapped so a read failure just falls
/// through to the next source, and finding nothing anywhere yields the empty document.
/// </summary>
public static class PatchNotesLoader
{
    /// <summary>The fixed disk file name and embedded manifest-resource logical name.</summary>
    public const string ResourceName = "PLAY_CHANGELOG.md";

    /// <summary>
    /// Loads via <see cref="Assembly.GetEntryAssembly"/> and <see cref="AppContext.BaseDirectory"/>.
    /// Returns <see cref="PatchNotesDocument.Empty"/> when there is no entry assembly (some test hosts).
    /// </summary>
    public static PatchNotesDocument Load()
    {
        Assembly? entryAssembly = Assembly.GetEntryAssembly();
        return entryAssembly is null ? PatchNotesDocument.Empty : Load(entryAssembly);
    }

    /// <summary>
    /// Loads <see cref="ResourceName"/> from disk under <paramref name="baseDirectory"/> (defaults to
    /// <see cref="AppContext.BaseDirectory"/>) if it exists, else from an embedded resource of the same
    /// name in <paramref name="assembly"/>, else returns <see cref="PatchNotesDocument.Empty"/>.
    /// </summary>
    public static PatchNotesDocument Load(Assembly assembly, string? baseDirectory = null)
    {
        string? text = TryReadDisk(baseDirectory ?? AppContext.BaseDirectory) ?? TryReadEmbedded(assembly);
        return PatchNotesParser.Parse(text);
    }

    static string? TryReadDisk(string baseDirectory)
    {
        try
        {
            string path = Path.Combine(baseDirectory, ResourceName);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    static string? TryReadEmbedded(Assembly assembly)
    {
        try
        {
            using Stream? stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream is null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }
}
