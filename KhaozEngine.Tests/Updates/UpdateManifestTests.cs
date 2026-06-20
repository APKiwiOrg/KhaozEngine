using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using KhaozEngine.Updates;
using Xunit;

namespace KhaozEngine.Tests.Updates;

public sealed class UpdateManifestTests
{
    private static UpdateManifest Manifest(string version, params (string path, string sha, long size)[] files)
    {
        var m = new UpdateManifest { Version = version, Platform = "win-x64" };
        foreach ((string path, string sha, long size) in files)
        {
            m.Files.Add(new ManifestFileEntry { Path = path, Sha256 = sha, Size = size });
        }
        return m;
    }

    [Fact]
    public void ComputeDiff_NewRemoteFile_IsDownloaded()
    {
        UpdateManifest local = Manifest("1.0.0", ("a.dll", "aaa", 10));
        UpdateManifest remote = Manifest("1.1.0", ("a.dll", "aaa", 10), ("b.dll", "bbb", 20));

        ManifestDiff diff = UpdateManifest.ComputeDiff(local, remote);

        Assert.Equal(new[] { "b.dll" }, diff.FilesToDownload.Select(f => f.Path));
        Assert.Empty(diff.FilesToDelete);
    }

    [Fact]
    public void ComputeDiff_ChangedHash_IsDownloaded()
    {
        UpdateManifest local = Manifest("1.0.0", ("a.dll", "old", 10));
        UpdateManifest remote = Manifest("1.1.0", ("a.dll", "new", 12));

        ManifestDiff diff = UpdateManifest.ComputeDiff(local, remote);

        Assert.Equal(new[] { "a.dll" }, diff.FilesToDownload.Select(f => f.Path));
    }

    [Fact]
    public void ComputeDiff_UnchangedFile_IsNotDownloaded()
    {
        UpdateManifest local = Manifest("1.0.0", ("a.dll", "same", 10));
        UpdateManifest remote = Manifest("1.1.0", ("a.dll", "same", 10));

        ManifestDiff diff = UpdateManifest.ComputeDiff(local, remote);

        Assert.Empty(diff.FilesToDownload);
        Assert.Empty(diff.FilesToDelete);
    }

    [Fact]
    public void ComputeDiff_RemovedFile_IsDeleted()
    {
        UpdateManifest local = Manifest("1.0.0", ("a.dll", "aaa", 10), ("gone.dll", "ggg", 5));
        UpdateManifest remote = Manifest("1.1.0", ("a.dll", "aaa", 10));

        ManifestDiff diff = UpdateManifest.ComputeDiff(local, remote);

        Assert.Empty(diff.FilesToDownload);
        Assert.Equal(new[] { "gone.dll" }, diff.FilesToDelete);
    }

    [Fact]
    public void ComputeDiff_TotalDownloadBytes_SumsSizes()
    {
        UpdateManifest local = Manifest("1.0.0");
        UpdateManifest remote = Manifest("1.1.0", ("a", "x", 30), ("b", "y", 12));

        ManifestDiff diff = UpdateManifest.ComputeDiff(local, remote);

        Assert.Equal(42, diff.TotalDownloadBytes);
    }

    [Fact]
    public void GenerateFromDirectory_ProducesSortedForwardSlashEntries()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ke-updates-gen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "zeta.txt"), "zeta");
        File.WriteAllText(Path.Combine(dir, "sub", "alpha.txt"), "alpha");

        try
        {
            UpdateManifest m = UpdateManifest.GenerateFromDirectory(dir, "2.0.0", "linux-x64");

            Assert.Equal("2.0.0", m.Version);
            Assert.Equal(new[] { "sub/alpha.txt", "zeta.txt" }, m.Files.Select(f => f.Path));
            ManifestFileEntry alpha = m.Files[0];
            Assert.Equal(5, alpha.Size);
            // SHA256("alpha") lowercase hex.
            Assert.Matches("^[0-9a-f]{64}$", alpha.Sha256);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SerializeDeserialize_RoundTrips()
    {
        UpdateManifest original = Manifest("3.1.4", ("game.dll", "deadbeef", 999));
        original.Platform = "osx-arm64";

        UpdateManifest? restored = UpdateManifest.Deserialize(original.Serialize());

        Assert.NotNull(restored);
        Assert.Equal("3.1.4", restored!.Version);
        Assert.Equal("osx-arm64", restored.Platform);
        Assert.Single(restored.Files);
        Assert.Equal("game.dll", restored.Files[0].Path);
        Assert.Equal("deadbeef", restored.Files[0].Sha256);
        Assert.Equal(999, restored.Files[0].Size);
    }

    [Fact]
    public void Serialize_UsesCamelCaseWireFormat()
    {
        string json = Manifest("1.0.0", ("a", "h", 1)).Serialize();

        Assert.Contains("\"version\"", json);
        Assert.Contains("\"publishedAtUtc\"", json);
        Assert.Contains("\"sha256\"", json);
    }

    [Fact]
    public void Manifest_RequiredFlag_RoundTripsThroughJson()
    {
        var manifest = new UpdateManifest { Version = "2.0.0", Platform = "win-x64", Required = true };

        UpdateManifest? parsed = UpdateManifest.Deserialize(manifest.Serialize());

        Assert.NotNull(parsed);
        Assert.True(parsed!.Required);
    }

    [Fact]
    public void Manifest_RequiredFlag_DefaultsFalseWhenAbsent()
    {
        UpdateManifest? parsed = UpdateManifest.Deserialize("{\"version\":\"2.0.0\",\"platform\":\"win-x64\",\"files\":[]}");

        Assert.NotNull(parsed);
        Assert.False(parsed!.Required);
    }
}

public sealed class UpdateVersionTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.1", true)]
    [InlineData("1.0.0", "1.1.0", true)]
    [InlineData("1.2.9", "1.2.10", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("2.0.0", "1.9.9", false)]
    [InlineData("1.2", "1.2.0", false)]
    [InlineData("1.2.0", "1.2", false)]
    public void IsNewer(string current, string candidate, bool expected)
    {
        Assert.Equal(expected, UpdateVersion.IsNewer(current, candidate));
    }
}

public sealed class UpdatePlatformTests
{
    [Theory]
    [InlineData(true, false, Architecture.X64, "win-x64")]
    [InlineData(true, false, Architecture.Arm64, "win-x64")]
    [InlineData(false, true, Architecture.Arm64, "osx-arm64")]
    [InlineData(false, true, Architecture.X64, "osx-x64")]
    [InlineData(false, false, Architecture.X64, "linux-x64")]
    public void Map(bool isWindows, bool isMacOs, Architecture arch, string expected)
    {
        Assert.Equal(expected, UpdatePlatform.Map(isWindows, isMacOs, arch));
    }
}
