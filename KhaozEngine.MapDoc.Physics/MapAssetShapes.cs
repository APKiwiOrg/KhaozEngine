using System;
using System.IO;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.Physics;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>The collider and selection shapes one native asset carries, read from a verified closure. Shapes stay in
/// asset source units. <see cref="SourceUnitsToMetres"/> converts them. An asset is solid exactly when it declares a
/// collider. A selection volume alone makes an asset examinable without blocking.</summary>
public sealed class MapAssetShapes
{
    /// <summary>The asset these shapes belong to.</summary>
    public string AssetId { get; }

    /// <summary>The blocking shape in asset source units, or null when the asset declares no collider.</summary>
    public PhysicsShape? Collider { get; }

    /// <summary>The selection shape in asset source units, or null when the asset declares no selection volume.</summary>
    public PhysicsShape? Selection { get; }

    /// <summary>The verified SHA-256 digest of the collider resource, or null without a collider.</summary>
    public string? ColliderSha256 { get; }

    /// <summary>The verified SHA-256 digest of the selection resource, or null without a selection volume.</summary>
    public string? SelectionSha256 { get; }

    /// <summary>True exactly when <see cref="Collider"/> is not null.</summary>
    public bool IsSolid => Collider is not null;

    /// <summary>The asset's source unit scale in metres, applied by the caller when it places the shapes.</summary>
    public float SourceUnitsToMetres { get; }

    internal MapAssetShapes(string assetId, PhysicsShape? collider, string? colliderSha256, PhysicsShape? selection,
        string? selectionSha256, float sourceUnitsToMetres)
    {
        AssetId = assetId;
        Collider = collider;
        ColliderSha256 = colliderSha256;
        Selection = selection;
        SelectionSha256 = selectionSha256;
        SourceUnitsToMetres = sourceUnitsToMetres;
    }

    /// <summary>Reads the collider and selection resources of <paramref name="assetId"/>. Throws
    /// <see cref="MapDocumentException"/> for an asset outside the closure, a payload that does not read as exactly one
    /// <see cref="PropCollisionFormat"/> shape, a triangle mesh inside a compound, which the physics backend cannot
    /// install, and any declared support resource, since placement-local support surfaces arrive with R5.</summary>
    public static MapAssetShapes Read(MapAssetClosure closure, string assetId)
    {
        ArgumentNullException.ThrowIfNull(closure);
        ArgumentNullException.ThrowIfNull(assetId);
        MapResolvedAsset asset = closure.GetAsset(assetId);
        if (asset.SupportResourceIds.Count != 0)
            throw new MapDocumentException(
                $"Asset '{asset.Id}' declares support resource '{asset.SupportResourceIds[0]}', and placement-local support surfaces arrive with R5.");

        (PhysicsShape? collider, string? colliderSha256) =
            ReadShape(closure, asset.Id, asset.CollisionResourceId, MapResourceKind.Collider);
        (PhysicsShape? selection, string? selectionSha256) =
            ReadShape(closure, asset.Id, asset.SelectionResourceId, MapResourceKind.Selection);
        return new MapAssetShapes(asset.Id, collider, colliderSha256, selection, selectionSha256, asset.SourceUnitsToMetres);
    }

    static (PhysicsShape? Shape, string? Sha256) ReadShape(MapAssetClosure closure, string assetId, string? resourceId,
        MapResourceKind kind)
    {
        if (resourceId is null) return (null, null);
        MapResolvedResource resource = closure.GetResource(resourceId);
        if (resource.Kind != kind)
            throw new MapDocumentException(
                $"Asset '{assetId}' requires {kind} resource '{resourceId}', which is {resource.Kind}.");

        PhysicsShape shape;
        using (var stream = new MemoryStream(resource.Bytes.ToArray(), writable: false))
        {
            try
            {
                shape = PropCollisionFormat.Read(stream);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or OverflowException)
            {
                throw new MapDocumentException(
                    $"Asset '{assetId}' collision payload '{resourceId}' cannot be read: {ex.Message}", ex);
            }
            if (stream.Position != stream.Length)
                throw new MapDocumentException(
                    $"Asset '{assetId}' collision payload '{resourceId}' has {stream.Length - stream.Position} bytes after its shape.");
        }

        RefuseUninstallable(shape, assetId, resourceId);
        return (shape, resource.Reference.Sha256);
    }

    static void RefuseUninstallable(PhysicsShape shape, string assetId, string resourceId)
    {
        if (shape is not CompoundShape compound) return;
        foreach (CompoundChild child in compound.Children)
        {
            if (child.Shape is TriangleMeshShape)
                throw new MapDocumentException(
                    $"Asset '{assetId}' resource '{resourceId}' places a triangle mesh inside a compound, which the physics backend cannot install.");
            RefuseUninstallable(child.Shape, assetId, resourceId);
        }
    }
}
