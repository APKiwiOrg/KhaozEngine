using System;
using System.IO;
using System.Security.Cryptography;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.TileWorld;
using SharpGLTF.Materials;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>The identity a loaded material image carries and the decode it shares (issue #1776). An embedded image
/// is named by the SHA-256 of its encoded bytes and an external one by its absolute path, so two glbs that embed one
/// PNG, or reference one file, decode it once and a scene uploads it once. Every loader entry point that reads
/// material maps resolves an external image against its own glb's folder.</summary>
public sealed class MaterialImageIdentityTests
{
    public enum Entry { RigidParts, RigidSingle, SkinnedParts, SkinnedSingle }

    [Theory]
    [InlineData(Entry.RigidParts, false)]
    [InlineData(Entry.RigidParts, true)]
    [InlineData(Entry.RigidSingle, true)]
    [InlineData(Entry.SkinnedParts, true)]
    [InlineData(Entry.SkinnedSingle, true)]
    public void Two_glbs_naming_one_image_share_its_identity_and_one_decode(Entry entry, bool external)
    {
        using var dir = new TempDir();
        byte[] png = MaterialGlbFixture.UniquePng();
        bool rigged = entry is Entry.SkinnedParts or Entry.SkinnedSingle;
        string[]? satellite = external ? ["oak.png"] : null;
        string a = MaterialGlbFixture.Write(dir.Path, "a.glb",
            new MaterialBuilder("oak").WithBaseColor(new SharpGLTF.Memory.MemoryImage(png)), rigged, satellite);
        string b = MaterialGlbFixture.Write(dir.Path, "b.glb",
            new MaterialBuilder("oak").WithBaseColor(new SharpGLTF.Memory.MemoryImage(png)), rigged, satellite);

        DecodedImage first = LoadAlbedo(a, entry);
        DecodedImage second = LoadAlbedo(b, entry);

        string expected = external
            ? "file:" + Path.GetFullPath(Path.Combine(dir.Path, "oak.png"))
            : "sha256:" + Convert.ToHexStringLower(SHA256.HashData(png));
        Assert.Equal(expected, first.Identity);
        Assert.Equal(expected, second.Identity);
        // One decode: both loads hand back the same pixel array while the first image is still referenced.
        Assert.Same(first.Rgba, second.Rgba);
    }

    static DecodedImage LoadAlbedo(string path, Entry entry) => entry switch
    {
        Entry.RigidParts => GltfLoader.LoadPartsWithMaterials(path)[0].Maps.Albedo!.Value,
        Entry.RigidSingle => GltfLoader.LoadWithMaterial(path).Maps.Albedo!.Value,
        Entry.SkinnedParts => GltfLoader.LoadSkinnedPartsWithMaterials(path)[0].Maps.Albedo!.Value,
        Entry.SkinnedSingle => GltfLoader.LoadSkinnedWithMaterial(path).Maps.Albedo!.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(entry)),
    };
}
