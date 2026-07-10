using System;
using System.IO;
using System.Reflection;
using KhaozEngine.Gui;
using Xunit;

namespace KhaozEngine.Tests.Gui;

/// <summary>
/// Covers <see cref="PatchNotesLoader"/>'s disk-then-embedded fallback: a disk copy of
/// <see cref="PatchNotesLoader.ResourceName"/> next to <c>baseDirectory</c> wins over the embedded
/// resource, the embedded resource (<c>Gui/Fixtures/PLAY_CHANGELOG.md</c>, embedded under the exact
/// logical name <c>PLAY_CHANGELOG.md</c>) is used when there is no disk file, and neither present
/// yields <see cref="PatchNotesDocument.Empty"/> without throwing.
/// </summary>
public class PatchNotesLoaderTests
{
    static readonly Assembly TestAssembly = typeof(PatchNotesLoaderTests).Assembly;

    const string DiskChangelog = """
        # DiskFixture - Player Changelog

        ---

        ## 2026-02-02

        ### Build 9.9.9 (Disk)

        - **Minor**
          - Loaded from disk, must win over the embedded fixture.

        ---
        """;

    static string CreateEmptyTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ke-patchnotes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Load_DiskFileExists_WinsOverEmbeddedResource()
    {
        string dir = CreateEmptyTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, PatchNotesLoader.ResourceName), DiskChangelog);

            var doc = PatchNotesLoader.Load(TestAssembly, dir);

            Assert.Equal("DiskFixture - Player Changelog", doc.Title);
            Assert.Equal("9.9.9", doc.Builds[0].Version);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Load_NoDiskFile_FallsBackToEmbeddedResource()
    {
        string dir = CreateEmptyTempDir();
        try
        {
            var doc = PatchNotesLoader.Load(TestAssembly, dir);

            Assert.Equal("EmbeddedFixture - Player Changelog", doc.Title);
            Assert.Equal("0.1.0", doc.Builds[0].Version);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Load_NothingOnDiskOrEmbedded_YieldsEmptyDocumentWithoutThrowing()
    {
        string dir = CreateEmptyTempDir();
        try
        {
            // The Gui assembly itself never embeds PLAY_CHANGELOG.md, so this has neither source.
            var doc = PatchNotesLoader.Load(typeof(PatchNotesDocument).Assembly, dir);

            Assert.True(doc.IsEmpty);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Load_EntryAssemblyOverload_NeverThrows()
    {
        // The test host's entry assembly (xunit's testhost) has neither a disk copy of ResourceName next
        // to it nor an embedded PLAY_CHANGELOG.md resource, so this deterministically falls through both
        // sources to PatchNotesDocument.Empty - a well-formed document, not a null or a half-built one.
        var doc = PatchNotesLoader.Load();

        Assert.NotNull(doc);
        Assert.NotNull(doc.Builds);
        Assert.True(doc.IsEmpty);
        Assert.Empty(doc.Builds);
        Assert.Equal(string.Empty, doc.Title);
    }
}
