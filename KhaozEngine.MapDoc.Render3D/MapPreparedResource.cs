using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

public enum MapRenderKind { Terrain, Placement, Light, Roof, Water, Foliage, Marker }
public readonly record struct MapRenderKey(MapRenderKind Kind, string OwnerId, string PartId, int Lod);
[Flags]
public enum MapDrawChannels { None = 0, Colour = 1, Shadow = 2, Camera = 4, Sky = 8, Selection = 16, Physical = 32 }
public sealed record MapResourceStamp(string ClosureHash, string SourceGeneration, string ScopeDigest, string PolicyDigest, long Generation);
public readonly record struct MapResourceReference(MapRenderKey Key, MapResourceStamp Stamp);

public sealed record MapPreparedResource(MapResourceReference Reference, MapBox3 Bounds, MapResourcePayload Payload, string PayloadDigest);

public abstract class MapResourcePayload
{
    private protected MapResourcePayload() { }
}

public sealed class MapRigidResource : MapResourcePayload
{
    readonly GltfMesh _mesh;
    readonly GltfMaterialMaps _maps;
    public GltfMesh Mesh => MapRenderCopies.Mesh(_mesh);
    public GltfMaterialMaps Maps => MapRenderCopies.Maps(_maps);
    internal GltfMesh BorrowMesh => _mesh;
    internal GltfMaterialMaps BorrowMaps => _maps;
    public MapRigidResource(GltfMesh mesh, GltfMaterialMaps maps)
    { ArgumentNullException.ThrowIfNull(mesh); _mesh = MapRenderCopies.Mesh(mesh); _maps = MapRenderCopies.Maps(maps); }
}

public sealed class MapLightResource(MapEffectiveLight light) : MapResourcePayload
{
    public MapEffectiveLight Light { get; } = light ?? throw new ArgumentNullException(nameof(light));
}

public sealed class MapWaterResource : MapResourcePayload
{
    readonly GltfMesh _mesh;
    readonly byte[] _clipData;
    public GltfMesh Mesh => MapRenderCopies.Mesh(_mesh);
    public ReadOnlyMemory<byte> ClipData => _clipData.ToArray();
    internal GltfMesh BorrowMesh => _mesh;
    internal ReadOnlyMemory<byte> BorrowClipData => _clipData;
    public MapWaterResource(GltfMesh mesh, ReadOnlyMemory<byte> clipData)
    { ArgumentNullException.ThrowIfNull(mesh); _mesh = MapRenderCopies.Mesh(mesh); _clipData = clipData.ToArray(); }
}

public sealed class MapFoliageModelBinding
{
    public string ModelId { get; }
    public IReadOnlyList<MapResourceReference> Parts { get; }
    public FoliageMotionProfile Motion { get; }
    public bool CastShadows { get; }
    public MapFoliageModelBinding(string modelId, IReadOnlyList<MapResourceReference> parts, FoliageMotionProfile motion, bool castShadows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId); ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0 || parts.Any(p => p.Stamp is null || string.IsNullOrEmpty(p.Key.OwnerId)))
            throw new ArgumentException("Foliage models require registered part references.", nameof(parts));
        ModelId = modelId; Parts = Array.AsReadOnly(parts.ToArray()); Motion = motion; CastShadows = castShadows;
    }
}

/// <summary>World rows retain generated matrix bits. AnchorLocal rows are already reduced and must not be reduced again.</summary>
public enum MapFoliageTransformSpace { World, AnchorLocal }

public sealed partial class MapFoliageResource : MapResourcePayload
{
    Matrix4x4[]? _transforms;
    float[]? _ranks;
    int[]? _indices;
    public MapSubmissionAnchor Anchor { get; }
    public MapFoliageTransformSpace TransformSpace { get; }
    public ReadOnlyMemory<Matrix4x4> WorldTransforms => RequireSpace(MapFoliageTransformSpace.World).ToArray();
    public ReadOnlyMemory<Matrix4x4> AnchorLocalTransforms => RequireSpace(MapFoliageTransformSpace.AnchorLocal).ToArray();
    public ReadOnlyMemory<float> Ranks => BorrowRanks.ToArray();
    public ReadOnlyMemory<int> ModelIndices => BorrowModelIndices.ToArray();
    public IReadOnlyList<MapFoliageModelBinding> Models { get; private set; }
    internal ReadOnlyMemory<Matrix4x4> BorrowWorldTransforms => RequireSpace(MapFoliageTransformSpace.World);
    internal ReadOnlyMemory<Matrix4x4> BorrowAnchorLocalTransforms => RequireSpace(MapFoliageTransformSpace.AnchorLocal);
    internal ReadOnlyMemory<float> BorrowRanks
    { get { RequireLive(); return _source is { } source ? source.Columns.BorrowRanks : _ranks!; } }
    internal ReadOnlyMemory<int> BorrowModelIndices
    { get { RequireLive(); return _source is { } source ? source.Columns.BorrowModelIndices : _indices!; } }

    public MapFoliageResource(MapSubmissionAnchor anchor, ReadOnlyMemory<Matrix4x4> worldTransforms,
        ReadOnlyMemory<float> ranks, ReadOnlyMemory<int> modelIndices, IReadOnlyList<MapFoliageModelBinding> models)
        : this(anchor, worldTransforms, ranks, modelIndices, models, MapFoliageTransformSpace.World) { }

    /// <summary>Registers precise explicit poses after their single anchor-relative conversion.</summary>
    public static MapFoliageResource FromAnchorLocal(MapSubmissionAnchor anchor, ReadOnlyMemory<Matrix4x4> localTransforms,
        ReadOnlyMemory<float> ranks, ReadOnlyMemory<int> modelIndices, IReadOnlyList<MapFoliageModelBinding> models) =>
        new(anchor, localTransforms, ranks, modelIndices, models, MapFoliageTransformSpace.AnchorLocal);

    ReadOnlyMemory<Matrix4x4> RequireSpace(MapFoliageTransformSpace expected)
    {
        RequireLive();
        if (TransformSpace != expected) throw new InvalidOperationException("Read foliage transforms in their declared coordinate space.");
        return _source is { } source ? source.Columns.BorrowTransforms : _transforms!;
    }

    MapFoliageResource(MapSubmissionAnchor anchor, ReadOnlyMemory<Matrix4x4> transforms,
        ReadOnlyMemory<float> ranks, ReadOnlyMemory<int> modelIndices, IReadOnlyList<MapFoliageModelBinding> models, MapFoliageTransformSpace space)
    {
        ArgumentNullException.ThrowIfNull(models);
        Anchor = anchor; TransformSpace = space; _transforms = transforms.ToArray(); _ranks = ranks.ToArray(); _indices = modelIndices.ToArray();
        Models = Array.AsReadOnly(models.ToArray());
        ValidateRows();
    }
}

internal static class MapRenderCopies
{
    internal static GltfMesh Mesh(GltfMesh mesh) => new(mesh.Vertices.ToArray(), mesh.Indices32.ToArray());
    internal static GltfMaterialMaps Maps(GltfMaterialMaps maps) => new(Image(maps.Albedo), Image(maps.Normal), Image(maps.Roughness), maps.AlphaCutoff);
    static DecodedImage? Image(DecodedImage? image) => image is { } i ? new(i.Rgba.ToArray(), i.Width, i.Height) : null;
    internal static GltfMeshPart Part(GltfMeshPart part) => new(Mesh(part.Mesh), Maps(part.Maps));
}
