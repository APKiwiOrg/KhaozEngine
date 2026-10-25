using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Physics;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>The collider and selection shapes one native asset carries, read from a verified closure. Shapes stay in
/// asset source units. <see cref="SourceUnitsToMetres"/> converts them. An asset is solid exactly when it declares a
/// collider. A selection volume alone makes an asset examinable without blocking.</summary>
public sealed class MapAssetShapes : IDisposable
{
    internal MapPreparationReservation? PreparationOwnership { get; set; }
    /// <summary>Releases the decoded output's preparation credit after its last consumer.</summary>
    public void Dispose() => PreparationOwnership?.Dispose();
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

    /// <summary>Detached descriptors decoded and validated by MapDoc before effective support sampling. Their
    /// frames already declare metres, so SourceUnitsToMetres is never applied to them again.</summary>
    public IReadOnlyList<MapSupportResource> SupportResources { get; }

    internal MapAssetShapes(string assetId, PhysicsShape? collider, string? colliderSha256, PhysicsShape? selection,
        string? selectionSha256, float sourceUnitsToMetres)
        : this(assetId, collider, colliderSha256, selection, selectionSha256, sourceUnitsToMetres, Array.Empty<MapSupportResource>()) { }

    internal MapAssetShapes(string assetId, PhysicsShape? collider, string? colliderSha256, PhysicsShape? selection,
        string? selectionSha256, float sourceUnitsToMetres, IReadOnlyList<MapSupportResource> supportResources)
    {
        AssetId = assetId;
        Collider = collider;
        ColliderSha256 = colliderSha256;
        Selection = selection;
        SelectionSha256 = selectionSha256;
        SourceUnitsToMetres = sourceUnitsToMetres;
        SupportResources = supportResources;
    }

    /// <summary>Reads the collider and selection resources of <paramref name="assetId"/>. Throws
    /// <see cref="MapDocumentException"/> for an asset outside the closure, a payload that does not read as exactly one
    /// <see cref="PropCollisionFormat"/> shape, a triangle mesh inside a compound, which the physics backend cannot
    /// install, a hull or mesh with no points, and any declared support resource lacking a validated MapDoc payload.</summary>
    public static MapAssetShapes Read(MapAssetClosure closure, string assetId)
    {
        ArgumentNullException.ThrowIfNull(closure);
        ArgumentNullException.ThrowIfNull(assetId);
        var admission = MapSceneAdmission.Current;
        using var preparation = admission?.BeginPreparation();
        MapResolvedAsset asset = closure.GetAsset(assetId);
        admission?.ReserveBytes(checked(128L + 128L * asset.SupportResourceIds.Count));
        var borrowed = asset.SupportResourceIds.Select(id => closure.BorrowSupport(id)).ToArray();
        admission?.ReserveBytes(borrowed.Sum(s => checked(128L + 8L * s.References.Count + 64L * s.Surfaces.Patches.Count +
            128L * s.Surfaces.Refs.Count + s.Surfaces.Refs.Sum(r => 8L * (r.IndoorSpan?.DomainTags.Count ?? 0)))));
        var support = borrowed.Select(r => r with
        { Surfaces = r.Surfaces.Clone(), References = Array.AsReadOnly(r.References.ToArray()) }).ToArray();

        (PhysicsShape? collider, string? colliderSha256) =
            ReadShape(closure, asset.Id, asset.CollisionResourceId, MapResourceKind.Collider);
        (PhysicsShape? selection, string? selectionSha256) =
            ReadShape(closure, asset.Id, asset.SelectionResourceId, MapResourceKind.Selection);
        var result = new MapAssetShapes(asset.Id, collider, colliderSha256, selection, selectionSha256, asset.SourceUnitsToMetres, Array.AsReadOnly(support));
        result.PreparationOwnership = preparation?.Complete(new MapPhysicsInventory().Asset(result));
        return result;
    }

    static (PhysicsShape? Shape, string? Sha256) ReadShape(MapAssetClosure closure, string assetId, string? resourceId,
        MapResourceKind kind)
    {
        if (resourceId is null) return (null, null);
        var current = MapSceneAdmission.Current;
        using var preparation = current?.BeginPreparation();
        MapResolvedResource resource = closure.GetResource(resourceId);
        if (resource.Kind != kind)
            throw new MapDocumentException(
                $"Asset '{assetId}' requires {kind} resource '{resourceId}', which is {resource.Kind}.");

        if (MapSceneAdmission.Current is { } admission)
        {
            // Shared decode demand is charged once per asset read, separately from every placement's output.
            admission.ReserveBytes(checked(256L + 15L * resource.ByteLength));
            admission.BeforePayload(resource.ByteLength);
        }
        // Bytes already returns a fresh copy, so read that buffer in place rather than copying it again.
        ReadOnlyMemory<byte> bytes = resource.Bytes;
        ArraySegment<byte> segment = MemoryMarshal.TryGetArray(bytes, out ArraySegment<byte> array)
            ? array : new ArraySegment<byte>(bytes.ToArray());
        PhysicsShape shape;
        using (var stream = new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false))
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
        if (preparation is not null) preparation.Complete(new MapPhysicsInventory().Shape(shape));
        return (shape, resource.Reference.Sha256);
    }

    static void RefuseUninstallable(PhysicsShape shape, string assetId, string resourceId)
    {
        // An empty point set has no extent, so it must never reach bounds or envelope construction.
        if (shape is ConvexHullShape { Points.Length: 0 } or TriangleMeshShape { Vertices.Length: 0 })
            throw new MapDocumentException(
                $"Asset '{assetId}' collision payload '{resourceId}' has a {shape.GetType().Name} with no points.");
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
