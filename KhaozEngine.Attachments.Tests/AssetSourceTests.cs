using System;
using System.IO;
using System.Runtime.InteropServices;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public sealed class AssetSourceTests : IDisposable
{
    readonly string root = Path.Combine(AppContext.BaseDirectory, "attachment-source-" + Guid.NewGuid().ToString("N"));

    public AssetSourceTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void Read_ReturnsFreshFileSnapshot()
    {
        Directory.CreateDirectory(Path.Combine(root, "parts"));
        string path = Path.Combine(root, "parts", "plate.glb");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        var source = new DirectoryAttachmentAssetSource(root);
        ReadOnlyMemory<byte> first = source.Read("parts/plate.glb");
        Assert.True(MemoryMarshal.TryGetArray(first, out ArraySegment<byte> storage));
        storage.Array![storage.Offset] = 9;
        Assert.Equal(new byte[] { 1, 2, 3 }, source.Read("parts/plate.glb").ToArray());
        File.WriteAllBytes(path, new byte[] { 4, 5 });
        Assert.Equal(new byte[] { 9, 2, 3 }, first.ToArray());
        Assert.Equal(new byte[] { 4, 5 }, source.Read("parts/plate.glb").ToArray());
    }

    [Fact]
    public void Read_ReportsMissingFileWithKeyContext()
    {
        var source = new DirectoryAttachmentAssetSource(root);
        Assert.Contains("missing.glb", Assert.Throws<InvalidDataException>(() => source.Read("missing.glb")).Message);
    }

    [Theory]
    [InlineData("../outside.glb")]
    [InlineData("/outside.glb")]
    [InlineData("parts\\plate.glb")]
    [InlineData("https://example.test/plate.glb")]
    public void Read_RejectsInvalidKeys(string key) => Assert.Throws<InvalidDataException>(() => new DirectoryAttachmentAssetSource(root).Read(key));

    [Theory]
    [InlineData('\uD800')]
    [InlineData('\uDC00')]
    public void Read_RejectsMalformedUtf16BeforeFilesystemAccess(char surrogate)
    {
        File.WriteAllBytes(Path.Combine(root, "plate\uFFFD.glb"), new byte[] { 1 });
        var source = new DirectoryAttachmentAssetSource(root);
        string key = "plate" + surrogate + ".glb";

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => source.Read(key));

        Assert.Contains(key, error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("plate\U0001F9B4.glb")]
    [InlineData("plate\uFFFD.glb")]
    public void Read_PreservesValidUnicodeKeys(string key)
    {
        File.WriteAllBytes(Path.Combine(root, key), new byte[] { 1, 2, 3 });

        Assert.Equal(new byte[] { 1, 2, 3 }, new DirectoryAttachmentAssetSource(root).Read(key).ToArray());
    }

    [Fact]
    public void Read_RejectsLinkedDirectoryAndLinkedFile()
    {
        string target = Path.Combine(root, "real");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "plate.glb"), new byte[] { 1 });
        Directory.CreateSymbolicLink(Path.Combine(root, "linked"), target);
        File.CreateSymbolicLink(Path.Combine(root, "plate.glb"), Path.Combine(target, "plate.glb"));
        var source = new DirectoryAttachmentAssetSource(root);
        Assert.Throws<InvalidDataException>(() => source.Read("linked/plate.glb"));
        Assert.Throws<InvalidDataException>(() => source.Read("plate.glb"));
    }

    [Fact]
    public void Read_RechecksRootWhenItBecomesALink()
    {
        string real = Path.Combine(root, "real");
        string sourceRoot = Path.Combine(root, "source");
        Directory.CreateDirectory(real);
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllBytes(Path.Combine(real, "plate.glb"), new byte[] { 1 });
        var source = new DirectoryAttachmentAssetSource(sourceRoot);
        Directory.Delete(sourceRoot);
        Directory.CreateSymbolicLink(sourceRoot, real);
        Assert.Throws<InvalidDataException>(() => source.Read("plate.glb"));
    }

    [Fact]
    public void Constructor_RejectsRootThroughALink()
    {
        string target = Path.Combine(root, "real");
        Directory.CreateDirectory(Path.Combine(target, "nested"));
        string link = Path.Combine(root, "linked");
        Directory.CreateSymbolicLink(link, target);
        Assert.ThrowsAny<ArgumentException>(() => new DirectoryAttachmentAssetSource(Path.Combine(link, "nested")));
    }
}
