using System;
using System.IO;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class CoveragePackedInputPathTests
{
    static readonly CoverageInputLocation Location = new("$.poseChunks[0].path", "synthetic.kepp", 0);

    [Theory]
    [InlineData("../outside.kepp")]
    [InlineData("/outside.kepp")]
    [InlineData("C:/outside.kepp")]
    [InlineData("C:outside.kepp")]
    [InlineData("//server/share/outside.kepp")]
    [InlineData("\\\\server\\share\\outside.kepp")]
    [InlineData("\\\\?\\C:\\outside.kepp")]
    [InlineData("poses\\000000.kepp")]
    [InlineData("poses/name:stream")]
    [InlineData("")]
    [InlineData("poses//000000.kepp")]
    [InlineData("poses/./000000.kepp")]
    [InlineData("poses/../000000.kepp")]
    [InlineData("poses/")]
    [InlineData("poses/\0name.kepp")]
    public void Packed_paths_stay_below_the_declaring_directory(string path)
    {
        using var files = new PathFiles();
        var error = Assert.Throws<CoverageBuildDataException>(() => CoverageInputPath.ResolveContained(files.Root, path, Location));
        Assert.Equal(CoverageBuildInputRefusal.PackedPathEscape, error.Reason);
        Assert.Equal(Location.Path, error.Path);
        Assert.Equal(0, error.ChunkIndex);
        AssertControl(files);
    }

    [DirectoryLinkTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Packed_paths_refuse_linked_components_even_with_an_inside_target(bool leaf, bool inside)
    {
        using var files = new PathFiles();
        string target = inside ? Path.Combine(files.Root, "poses") : files.Outside;
        string link = Path.Combine(files.Root, leaf ? "linked.kepp" : "linked");
        if (leaf) File.CreateSymbolicLink(link, Path.Combine(target, "000000.kepp"));
        else Directory.CreateSymbolicLink(link, target);
        try
        {
            string relative = leaf ? "linked.kepp" : "linked/000000.kepp";
            var error = Assert.Throws<CoverageBuildDataException>(() => CoverageInputPath.ResolveContained(files.Root, relative, Location));
            Assert.Equal(CoverageBuildInputRefusal.PackedPathLink, error.Reason);
            Assert.Equal(Location.Path, error.Path);
            AssertControl(files);
        }
        finally { if (leaf) File.Delete(link); else Directory.Delete(link); }
    }

    [DirectoryLinkTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Packed_paths_allow_a_base_directory_alias(int aliasKind)
    {
        using var files = new PathFiles();
        string link = Path.Combine(files.Folder, "alias");
        string redirect = Path.Combine(files.Folder, "redirect");
        string actualDirectory = files.Root;
        byte[] expectedBytes = CoveragePackedFixture.ChunkBytes();
        if (aliasKind == 2)
        {
            // The runtime normalizes alias/.. to its lexical parent before opening the manifest.
            actualDirectory = files.Folder;
            expectedBytes[^1] ^= 1;
            Directory.CreateDirectory(Path.Combine(actualDirectory, "poses"));
            File.WriteAllBytes(Path.Combine(actualDirectory, "poses", "000000.kepp"), expectedBytes);
        }
        if (aliasKind >= 3)
        {
            // Regression #1814: the parent segment inside the alias target follows the nested redirect.
            actualDirectory = Path.Combine(files.Folder, "actual", "input");
            Directory.CreateDirectory(Path.Combine(actualDirectory, "child"));
            Directory.CreateDirectory(Path.Combine(actualDirectory, "poses"));
            File.WriteAllBytes(Path.Combine(actualDirectory, "poses", "000000.kepp"), CoveragePackedFixture.ChunkBytes());
            Directory.CreateSymbolicLink(redirect, Path.Combine(actualDirectory, "child"));
            if (aliasKind == 3)
            {
                Directory.CreateDirectory(Path.Combine(files.Folder, "poses"));
                File.WriteAllBytes(Path.Combine(files.Folder, "poses", "000000.kepp"), [0xff]);
            }
        }
        string target = aliasKind switch
        {
            1 => files.Folder,
            2 => Path.Combine(files.Root, "poses"),
            3 or 4 => Path.Combine("redirect", ".."),
            _ => files.Root
        };
        Directory.CreateSymbolicLink(link, target);
        try
        {
            string baseDirectory = aliasKind == 1 ? Path.Combine(link, "input") : aliasKind == 2 ? Path.Combine(link, "..") : link;
            string resolved = CoverageInputPath.ResolveContained(baseDirectory, "poses/000000.kepp", Location);
            string expectedBase = aliasKind == 1 ? Path.Combine(link, "input") : aliasKind == 2 ? files.Folder : link;
            Assert.Equal(expectedBytes, File.ReadAllBytes(resolved));
            Assert.Equal(Path.Combine(expectedBase, "poses", "000000.kepp"), resolved);
        }
        finally
        {
            Directory.Delete(link);
            if (aliasKind >= 3) Directory.Delete(redirect);
        }
    }

    [DirectoryLinkTheory]
    [InlineData("missing.kepp")]
    [InlineData("cycle.kepp")]
    public void Packed_paths_fail_closed_for_dangling_or_cyclic_links(string target)
    {
        using var files = new PathFiles();
        string link = Path.Combine(files.Root, "cycle.kepp");
        File.CreateSymbolicLink(link, target);
        try
        {
            var error = Assert.Throws<CoverageBuildDataException>(() => CoverageInputPath.ResolveContained(files.Root, "cycle.kepp", Location));
            Assert.Equal(CoverageBuildInputRefusal.PackedPathLink, error.Reason);
            AssertControl(files);
        }
        finally { File.Delete(link); }
    }

    [Fact]
    public void Packed_paths_report_a_missing_chunk_with_its_manifest_location()
    {
        using var files = new PathFiles();
        var error = Assert.Throws<CoverageBuildDataException>(() => CoverageInputPath.ResolveContained(files.Root, "poses/missing.kepp", Location));
        Assert.Equal(CoverageBuildInputRefusal.PackedChunkMissing, error.Reason);
        Assert.Equal(Location.Path, error.Path);
        AssertControl(files);
    }

    static void AssertControl(PathFiles files)
    {
        string resolved = CoverageInputPath.ResolveContained(files.Root, "poses/000000.kepp", Location);
        Assert.Equal(CoveragePackedFixture.ChunkBytes(), File.ReadAllBytes(resolved));
    }

    sealed class PathFiles : IDisposable
    {
        internal string Folder { get; } = Path.Combine(Path.GetTempPath(), "khaozengine-coverage-packed-input", "task-1", Guid.NewGuid().ToString("N"));
        internal string Root => Path.Combine(Folder, "input");
        internal string Outside => Path.Combine(Folder, "outside");
        internal PathFiles()
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(Folder);
            else Directory.CreateDirectory(Folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(Path.Combine(Root, "poses"));
            Directory.CreateDirectory(Outside);
            File.WriteAllBytes(Path.Combine(Root, "poses", "000000.kepp"), CoveragePackedFixture.ChunkBytes());
            File.WriteAllBytes(Path.Combine(Outside, "000000.kepp"), CoveragePackedFixture.ChunkBytes());
        }
        public void Dispose() => Directory.Delete(Folder, true);
    }
}
