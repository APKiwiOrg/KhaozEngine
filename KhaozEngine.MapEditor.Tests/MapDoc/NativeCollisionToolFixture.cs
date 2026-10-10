using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapEdit;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>A saved resolver 1 native document and its resources in an isolated directory, open in a
/// <see cref="MapEditSession"/> with a <see cref="NativeCollisionService"/>. <c>wall-variant</c> is a compound of two
/// boxes in half-metre source units spanning 0 to 2.5 m, placed as <c>wall-1</c>. <c>rock-mesh</c> is a baked
/// triangle mesh collider, placed as <c>rock-1</c>.</summary>
internal sealed class NativeCollisionToolFixture : IDisposable
{
    public const string DocumentName = "world.map.json";

    readonly string _root;

    public MapEditSession Session { get; } = new();
    public NativeCollisionService Service { get; }
    public string Root => _root;
    public string DocumentPath => Path.Combine(_root, DocumentName);

    public NativeCollisionToolFixture()
        : this(Path.Combine(Path.GetTempPath(), "native-collision-" + Guid.NewGuid().ToString("N")), author: true)
    {
    }

    NativeCollisionToolFixture(string root, bool author)
    {
        _root = root;
        if (author) Author(root);
        Session.Open(DocumentPath);
        Service = new NativeCollisionService(Session);
    }

    /// <summary>Saves the open session, then opens the saved document in a fresh session over the same directory.</summary>
    public NativeCollisionToolFixture Reopen()
    {
        Session.Save();
        return new NativeCollisionToolFixture(_root, author: false);
    }

    /// <summary>SHA-256 over every file under the directory, its ordinal relative path and its bytes.</summary>
    public byte[] ReadAssetDirectoryDigest()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/')).Order(StringComparer.Ordinal))
        {
            byte[] name = Encoding.UTF8.GetBytes(file);
            hash.AppendData(BitConverter.GetBytes(name.Length));
            hash.AppendData(name);
            byte[] bytes = File.ReadAllBytes(Path.Combine(_root, file));
            hash.AppendData(BitConverter.GetBytes(bytes.LongLength));
            hash.AppendData(bytes);
        }
        return hash.GetHashAndReset();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    static void Author(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "kit"));
        // Half-metre source units: the boxes span 0 to 5 units, 0 to 2.5 m.
        var wall = new CompoundShape(new[]
        {
            new CompoundChild(new BoxShape(new Vector3(2f, 1f, 0.4f)), Pose.At(new Vector3(0, 1f, 0))),
            new CompoundChild(new BoxShape(new Vector3(1.6f, 1.5f, 0.4f)), Pose.At(new Vector3(0, 3.5f, 0))),
        });
        var rock = new TriangleMeshShape(
            new[] { new Vector3(-1, 0, -1), new Vector3(1, 0, -1), new Vector3(0, 0, 1), new Vector3(0, 1.5f, 0) },
            new[] { 0, 1, 2, 0, 3, 1, 1, 3, 2, 2, 3, 0 });

        MapAssetRef wallMesh = Write(root, "wall.mesh", "kit/wall-body.glb", NativeEditorAssetFixtures.QuadGlb(0f, 5f));
        MapAssetRef wallCollider = Write(root, "wall.collider", "kit/wall-body.coll", Collider(wall));
        MapAssetRef rockMesh = Write(root, "rock.mesh", "kit/rock-body.glb", NativeEditorAssetFixtures.QuadGlb(0f, 1.5f));
        MapAssetRef rockCollider = Write(root, "rock.collider", "kit/rock-body.coll", Collider(rock));
        string manifest = NativeEditorAssetFixtures.Manifest(
            new[]
            {
                NativeEditorAssetFixtures.Asset("wall-variant", "wall.mesh", "wall.collider", 0.5f,
                    new Vector3(-2, 0, -0.4f), new Vector3(2, 5, 0.4f)),
                NativeEditorAssetFixtures.Asset("rock-mesh", "rock.mesh", "rock.collider", 1f,
                    new Vector3(-1, 0, -1), new Vector3(1, 1.5f, 1)),
            },
            new[]
            {
                NativeEditorAssetFixtures.Resource(wallMesh, "Mesh"),
                NativeEditorAssetFixtures.Resource(wallCollider, "Collider"),
                NativeEditorAssetFixtures.Resource(rockMesh, "Mesh"),
                NativeEditorAssetFixtures.Resource(rockCollider, "Collider"),
            });
        MapAssetRef kit = Write(root, "kit", "kit/walls.manifest.json", Encoding.UTF8.GetBytes(manifest));

        var doc = new MapDocument
        {
            Id = "native-collision",
            Bounds = new() { MinX = -100, MinZ = -100, MaxX = 100, MaxZ = 100 },
            PlayableBounds = new() { MinX = -50, MinZ = -50, MaxX = 50, MaxZ = 50 },
            ResolverIdentity = new(1, 1),
            NativeAssets = new() { kit },
        };
        doc.Terrain.Biomes.Add(new MapBiomeBand());
        doc.Placements.Add(new MapPlacement { Id = "wall-1", Kind = "prop", AssetId = "wall-variant", X = 4, Z = 5 });
        doc.Placements.Add(new MapPlacement { Id = "rock-1", Kind = "prop", AssetId = "rock-mesh", X = -6, Z = 3 });
        MapDocumentFile.Save(doc, Path.Combine(root, DocumentName));
    }

    static byte[] Collider(PhysicsShape shape)
    {
        using var stream = new MemoryStream();
        PropCollisionFormat.Write(shape, stream);
        return stream.ToArray();
    }

    static MapAssetRef Write(string root, string id, string relativePath, byte[] bytes)
    {
        File.WriteAllBytes(Path.GetFullPath(relativePath, root), bytes);
        return new MapAssetRef(id, relativePath, NativeEditorAssetFixtures.Digest(bytes), 1);
    }
}
