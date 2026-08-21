using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The glb-backed mesh resolver: the MeshRef to path mapping, the per-archetype cache, and the
/// fall-back-and-log-once rule that keeps a half-authored kit rendering instead of throwing.</summary>
public class GltfMeshResolverTests : IDisposable
{
    static string SourceAsset => Path.Combine(AppContext.BaseDirectory, "assets", "testmodel.glb");

    readonly string _root;
    readonly List<string> _log = new();

    public GltfMeshResolverTests()
    {
        Assert.True(File.Exists(SourceAsset), $"test asset missing at {SourceAsset}");
        _root = Path.Combine(Path.GetTempPath(), "ke-gltf-resolver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "kit"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    static TileObjectArchetype Archetype(string id, string meshRef) =>
        new() { Id = id, Name = id, MeshRef = meshRef };

    static GreyboxMeshResolver Greybox() => new();

    static int VertexCount(IReadOnlyList<GltfMeshPart> parts)
    {
        int total = 0;
        foreach (GltfMeshPart part in parts) total += part.Mesh.Vertices.Length;
        return total;
    }

    string CopyKitPiece(string name)
    {
        string path = Path.Combine(_root, "kit", name + ".glb");
        File.Copy(SourceAsset, path);
        return path;
    }

    [Fact]
    public void A_mesh_ref_loads_the_glb_under_the_root_directory()
    {
        CopyKitPiece("wall");
        var resolver = new GltfMeshResolver(_root, Greybox(), _log.Add);

        IReadOnlyList<GltfMeshPart>? parts = resolver.Resolve(Archetype("wall", "kit/wall.glb"));

        Assert.NotNull(parts);
        Assert.NotEmpty(parts);
        Assert.Equal(VertexCount(GltfLoader.LoadPartsWithMaterials(SourceAsset)), VertexCount(parts));
        Assert.Empty(_log);
    }

    [Fact]
    public void A_second_resolve_hands_back_the_same_list_instance()
    {
        CopyKitPiece("wall");
        var resolver = new GltfMeshResolver(_root, Greybox(), _log.Add);
        TileObjectArchetype archetype = Archetype("wall", "kit/wall.glb");

        IReadOnlyList<GltfMeshPart>? first = resolver.Resolve(archetype);
        IReadOnlyList<GltfMeshPart>? second = resolver.Resolve(archetype);

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void A_missing_file_logs_once_and_falls_back()
    {
        var resolver = new GltfMeshResolver(_root, Greybox(), _log.Add);
        TileObjectArchetype archetype = Archetype("wall", "kit/wall.glb");

        IReadOnlyList<GltfMeshPart>? parts = resolver.Resolve(archetype);

        Assert.NotNull(parts);
        Assert.Equal(Greybox().Resolve(archetype)!.Count, parts.Count);
        string line = Assert.Single(_log);
        Assert.Contains("wall", line, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(_root, "kit", "wall.glb"), line, StringComparison.Ordinal);

        // The cached failure answers the second call: no second log line, and nothing goes near the disk again.
        Assert.Same(parts, resolver.Resolve(archetype));
        Assert.Single(_log);
    }

    [Fact]
    public void A_missing_file_with_no_fallback_resolves_to_null()
    {
        var resolver = new GltfMeshResolver(_root, fallback: null, _log.Add);

        Assert.Null(resolver.Resolve(Archetype("wall", "kit/wall.glb")));
        Assert.Single(_log);
    }

    [Fact]
    public void An_empty_mesh_ref_goes_to_the_fallback_without_touching_the_disk()
    {
        // A root that does not exist at all: an empty MeshRef must never build a path from it, let alone probe one.
        var resolver = new GltfMeshResolver(Path.Combine(_root, "nowhere"), Greybox(), _log.Add);
        TileObjectArchetype archetype = Archetype("wall", "");

        IReadOnlyList<GltfMeshPart>? parts = resolver.Resolve(archetype);

        Assert.NotNull(parts);
        Assert.Equal(Greybox().Resolve(archetype)!.Count, parts.Count);
        // Not authored yet is not a failure, so it is silent: the greybox box IS the answer.
        Assert.Empty(_log);
    }

    [Fact]
    public void An_absolute_mesh_ref_is_used_as_it_stands()
    {
        string absolute = CopyKitPiece("wall");
        // A root that holds nothing, to prove the absolute reference is not combined with it.
        var resolver = new GltfMeshResolver(Path.Combine(_root, "nowhere"), Greybox(), _log.Add);

        IReadOnlyList<GltfMeshPart>? parts = resolver.Resolve(Archetype("wall", absolute));

        Assert.NotNull(parts);
        Assert.Equal(VertexCount(GltfLoader.LoadPartsWithMaterials(SourceAsset)), VertexCount(parts));
        Assert.Empty(_log);
    }

    [Fact]
    public void A_corrupt_file_logs_the_loader_message_and_falls_back()
    {
        string path = Path.Combine(_root, "kit", "wall.glb");
        File.WriteAllBytes(path, new byte[] { 0x6E, 0x6F, 0x74, 0x20, 0x61, 0x20, 0x67, 0x6C, 0x62, 0x21 });
        var resolver = new GltfMeshResolver(_root, Greybox(), _log.Add);
        TileObjectArchetype archetype = Archetype("wall", "kit/wall.glb");

        IReadOnlyList<GltfMeshPart>? parts = resolver.Resolve(archetype);

        Assert.NotNull(parts);
        Assert.Equal(Greybox().Resolve(archetype)!.Count, parts.Count);
        string line = Assert.Single(_log);
        Assert.Contains(ExpectedLoaderMessage(path), line, StringComparison.Ordinal);

        // A cached failure, same as the missing-file one: no second line, no second parse.
        Assert.Same(parts, resolver.Resolve(archetype));
        Assert.Single(_log);
    }

    // A MeshRef out of a corrupt catalog can carry a character no path API will take. Path.GetFullPath throws
    // ArgumentException on an embedded NUL, and before the fix that throw escaped Resolve and took the whole
    // TileWorldView constructor down with it.
    [Fact]
    public void A_mesh_ref_no_path_api_accepts_falls_back_instead_of_throwing()
    {
        var resolver = new GltfMeshResolver(_root, Greybox(), _log.Add);
        TileObjectArchetype archetype = Archetype("wall", "kit/wa\0ll.glb");

        IReadOnlyList<GltfMeshPart>? parts = resolver.Resolve(archetype);

        Assert.NotNull(parts);
        Assert.Equal(Greybox().Resolve(archetype)!.Count, parts.Count);
        Assert.Single(_log);
        Assert.Same(parts, resolver.Resolve(archetype));
        Assert.Single(_log);
    }

    // The forward-slash to platform-separator mapping is a no-op on macOS and Linux, so assert it against
    // Path.Combine rather than against a literal, and the expectation holds on every leg.
    [Fact]
    public void PathFor_combines_a_relative_ref_with_the_root()
    {
        var resolver = new GltfMeshResolver(_root, Greybox(), _log.Add);

        Assert.Equal(Path.Combine(_root, "kit", "wall.glb"), resolver.PathFor("kit/wall.glb"));

        string absolute = Path.Combine(_root, "kit", "elsewhere.glb");
        Assert.Equal(absolute, resolver.PathFor(absolute));
    }

    [Fact]
    public void The_parts_handed_out_are_read_only()
    {
        CopyKitPiece("wall");
        var resolver = new GltfMeshResolver(_root, Greybox(), _log.Add);

        IReadOnlyList<GltfMeshPart>? parts = resolver.Resolve(Archetype("wall", "kit/wall.glb"));

        Assert.NotNull(parts);
        Assert.IsNotType<List<GltfMeshPart>>(parts);
        Assert.True(parts is System.Collections.IList { IsReadOnly: true }, "the cached list is writable");
    }

    // The loader's own message for this file, read from the loader rather than hard-coded, so the assertion pins
    // that the REASON reaches the log line without pinning SharpGLTF's wording.
    static string ExpectedLoaderMessage(string path)
    {
        try
        {
            GltfLoader.LoadPartsWithMaterials(path);
            throw new InvalidOperationException("the corrupt fixture loaded, so there is no message to match");
        }
        catch (Exception ex) { return ex.Message; }
    }
}
