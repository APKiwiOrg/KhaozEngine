using System;
using System.Linq;

namespace KhaozEngine.Render3D;

public readonly record struct RenderAnchor(long X, long Y, long Z);
public readonly record struct SceneResourceHandle(long Value);
/// <summary>Complete before-work envelope. GPU payload survives publication, staging is transient, and CPU
/// partitions at input release using an independent inventory of the surviving objects.</summary>
public sealed record SceneUploadDemand(string InputDigest, long OwnedGpuBytes, long PeakStagingGpuBytes,
    long PeakCpuBytes, long MaxRetiredGpuBytes, long TransferBytes);

/// <summary>Claim precedes every allocation factory. Transient credit settles only after its ownership ends.
/// Retirement holds the retained remainder until Released confirms destruction.</summary>
public interface ISceneUploadReservation : IDisposable
{
    SceneUploadDemand Demand { get; }
    void Claim(SceneUploadDemand demand);
    void TransferCompleted();
    void Published(SceneResourceHandle handle);
    void InputsReleased(long retainedCpuBytes);
    void Retiring();
    void Released();
}

public interface ISceneResourceAccess
{
    MeshHandle MeshOf(SceneResourceHandle handle);
    void DrawResourceAnchored(SceneResourceHandle handle, in RigidInstanceDraw draw, RenderAnchor anchor);
}

/// <summary>Detached renderer input. Foliage retains explicit model and part correspondence.</summary>
public sealed class SceneUploadData
{
    readonly GltfMesh? _mesh;
    readonly GltfMaterialMaps? _maps;
    readonly byte[] _clipData;
    public GltfMesh? Mesh => _mesh is null ? null : Copy(_mesh);
    public GltfMaterialMaps? Maps => _maps is { } maps ? Copy(maps) : null;
    public SceneFoliageSource? Foliage { get; }
    public SceneGroundMaterialData? GroundMaterial { get; }
    internal string InputDigest { get; }
    internal SceneUploadDescription Description { get; }
    public ReadOnlyMemory<byte> ClipData => _clipData.ToArray();
    internal GltfMesh? BorrowMesh => _mesh;
    internal GltfMaterialMaps? BorrowMaps => _maps;
    internal ReadOnlyMemory<byte> BorrowClipData => _clipData;

    public SceneUploadData(GltfMesh? mesh, GltfMaterialMaps? maps, SceneFoliageSource? foliage, ReadOnlyMemory<byte> clipData,
        SceneGroundMaterialData? groundMaterial = null)
    {
        _mesh = mesh is null ? null : Copy(mesh); _maps = maps is { } m ? Copy(m) : null;
        Foliage = foliage; _clipData = clipData.ToArray(); GroundMaterial = groundMaterial;
        Description = new(_mesh, _maps, foliage?.Describe(), _clipData, groundMaterial?.Describe());
        InputDigest = SceneUploadFingerprint.Compute(Description);
    }
    internal static GltfMesh Copy(GltfMesh mesh) => new(mesh.Vertices.ToArray(), mesh.Indices32.ToArray());
    internal static GltfMaterialMaps Copy(GltfMaterialMaps maps) => new(Copy(maps.Albedo), Copy(maps.Normal), Copy(maps.Roughness), maps.AlphaCutoff);
    static DecodedImage? Copy(DecodedImage? image) => image is { } i ? new(i.Rgba.ToArray(), i.Width, i.Height) : null;
}
