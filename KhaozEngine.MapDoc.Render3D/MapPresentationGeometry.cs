using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Physics;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Prepared verified placement geometry in its integer anchor's local frame. Selection alone is raised.</summary>
public sealed class MapPresentationPlacement
{
    readonly PreparedShapeRay? _collider, _selection;
    readonly PhysicsShape? _colliderShape, _selectionShape;
    internal MapPresentationPlacement(MapResolvedPlacement placement, MapSubmissionAnchor anchor, Pose pose,
        PhysicsShape? collider, PhysicsShape? selection, Pose selectionPose, float raise, MapSceneAdmission admission)
    {
        OwnerId = placement.PlacementId; NumericId = placement.NumericId; Anchor = anchor; Pose = pose;
        _colliderShape = collider; _selectionShape = selection; SelectionPose = selectionPose; RaiseMetres = raise;
        if (collider is not null) { admission.ReserveBytes(PreparedShapeRay.Bytes(collider)); _collider = new(collider, pose); ColliderBounds = Bounds(collider, pose); }
        if (selection is not null) { admission.ReserveBytes(PreparedShapeRay.Bytes(selection)); _selection = new(selection, selectionPose); SelectionBounds = Bounds(selection, selectionPose); }
    }
    public string OwnerId { get; }
    public long? NumericId { get; }
    public MapSubmissionAnchor Anchor { get; }
    public Pose Pose { get; }
    public PhysicsShape? Collider => Copy(_colliderShape);
    public PhysicsShape? Selection => Copy(_selectionShape);
    public Pose SelectionPose { get; }
    public float RaiseMetres { get; }
    public MapBox3? ColliderBounds { get; }
    public MapBox3? SelectionBounds { get; }
    /// <summary>The ray is already relative to Anchor. No world binary32 position is reconstructed.</summary>
    public bool Raycast(in ShapeRay ray, bool selection, out ShapeRayHit hit)
    {
        hit = default;
        return (selection ? _selection : _collider)?.Cast(ray, out hit) == true;
    }
    static PhysicsShape? Copy(PhysicsShape? shape) => shape switch
    {
        ConvexHullShape hull => new ConvexHullShape((System.Numerics.Vector3[])hull.Points.Clone()),
        TriangleMeshShape mesh => new TriangleMeshShape((System.Numerics.Vector3[])mesh.Vertices.Clone(), (int[])mesh.Indices.Clone()),
        CompoundShape compound => new CompoundShape(compound.Children.Select(c => new CompoundChild(Copy(c.Shape)!, c.Local)).ToArray()),
        _ => shape,
    };
    static MapBox3 Bounds(PhysicsShape shape, Pose pose)
    { var b = PropQueryGeometry.Bounds(shape, pose); return new(b.MinX, b.MinY, b.MinZ, b.MaxX, b.MaxY, b.MaxZ); }
}

internal readonly record struct MapPresentationFace(string OwnerId, double Ax, double Ay, double Az,
    double Bx, double By, double Bz, double Cx, double Cy, double Cz);

/// <summary>One immutable scene's exact collider and canonical face inventory, prepared before any frame query.</summary>
public sealed class MapPresentationGeometry
{
    public IReadOnlyList<MapPresentationPlacement> Placements { get; }
    internal MapPresentationFace[] Faces { get; }
    internal MapPresentationPicker.Workspace PickWorkspace { get; }
    MapPresentationGeometry(MapPresentationPlacement[] placements, MapPresentationFace[] faces)
    { Placements = Array.AsReadOnly(placements); Faces = faces; PickWorkspace = new(checked(placements.Length + faces.Length)); }
    public static MapPresentationGeometry Prepare(MapScopedScene scene, MapAssetClosure assets,
        IReadOnlyList<MapTerrainCanonicalFace>? faces = null)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(assets);
        if (!scene.Witness.CompleteForScope || scene.Witness.ClosureHash != assets.Hash)
            throw new MapRenderAssetRefusal(MapRenderAssetStatus.Stale, "Presentation geometry requires the complete matching scoped closure.");
        var admission = MapSceneAdmission.Current ?? new MapSceneAdmission(scene.Source.Data.Limits);
        using var activation = admission.Enter();
        try
        {
            admission.ReserveBytes(checked(512L + 128L * scene.Placements.Count + 128L * (faces?.Count ?? 0)));
            var rows = new List<MapPresentationPlacement>();
            var shapes = new Dictionary<string, (PhysicsShape? Collider, PhysicsShape? Selection)>();
            foreach (var placement in scene.Placements.OrderBy(p => p.PlacementId, StringComparer.Ordinal))
            {
                var asset = assets.GetAsset(placement.AssetId);
                if (!shapes.TryGetValue(asset.Id, out var pair))
                {
                    pair = (Read(asset.CollisionResourceId, MapResourceKind.Collider), Read(asset.SelectionResourceId, MapResourceKind.Selection));
                    shapes.Add(asset.Id, pair);
                }
                if (pair.Collider is null && pair.Selection is null) continue;
                var transform = NativeMapPlacements.Pose(scene, placement);
                float scale = (float)(asset.SourceUnitsToMetres * transform.Scale);
                if (!float.IsFinite(scale) || scale <= 0) throw new MapDocumentException("Presentation scale must be finite and positive.");
                // Covers copies, scaled compound children, and the largest circumscribed cylinder envelope.
                admission.ReserveBytes(checked(2048 + CopyBytes(pair.Collider) + CopyBytes(pair.Selection)));
                var collider = pair.Collider is null ? null : PhysicsShapeScale.Uniform(pair.Collider, scale);
                var selection = pair.Selection is null ? collider! : PhysicsShapeScale.Uniform(pair.Selection, scale);
                admission.ReserveBytes(SweepBytes(selection));
                var anchor = new MapSubmissionAnchor(checked((long)Math.Floor(transform.X)), checked((long)Math.Floor(transform.Y)), checked((long)Math.Floor(transform.Z)));
                var (sin, cos) = Math.SinCos(transform.YawRadians * .5);
                var pose = new Pose(new((float)(transform.X - anchor.X), (float)(transform.Y - anchor.Y), (float)(transform.Z - anchor.Z)), new(0, (float)sin, 0, (float)cos));
                var envelope = PropQueryGeometry.RaiseEnvelope(selection, pose, 1);
                rows.Add(new(placement, anchor, pose, collider, envelope.Shape, envelope.Pose, envelope.RaiseMetres, admission));
            }
            var canonical = new MapPresentationFace[faces?.Count ?? 0];
            for (int i = 0; i < canonical.Length; i++)
            {
                var f = faces![i]; var t = f.Triangle;
                canonical[i] = new(f.Face.Key.OwnerId, t.A.X.ToDouble(), t.A.Y.ToDouble(), t.A.Z.ToDouble(),
                    t.B.X.ToDouble(), t.B.Y.ToDouble(), t.B.Z.ToDouble(), t.C.X.ToDouble(), t.C.Y.ToDouble(), t.C.Z.ToDouble());
            }
            return new(rows.ToArray(), canonical);

            PhysicsShape? Read(string? id, MapResourceKind kind)
            {
                if (id is null) return null;
                var resource = assets.GetResource(id);
                if (resource.Kind != kind) throw new MapDocumentException("Presentation shape has the wrong resource kind.");
                admission.ReserveBytes(checked(512 + 16L * resource.ByteLength)); admission.BeforePayload(resource.ByteLength);
                using var stream = new MemoryStream(resource.Bytes.ToArray(), false);
                var shape = PropCollisionFormat.Read(stream);
                if (stream.Position != stream.Length) throw new MapDocumentException("Presentation shape has trailing bytes.");
                _ = PropQueryGeometry.Bounds(shape, Pose.Identity);
                return shape;
            }
        }
        catch (MapSceneCapacityException) { throw new MapRenderAssetRefusal(MapRenderAssetStatus.CapacityExceeded, "Presentation geometry exceeds scene admission."); }
        catch (EndOfStreamException) { throw new MapRenderAssetRefusal(MapRenderAssetStatus.Invalid, "Presentation shape is truncated."); }
    }
    static long CopyBytes(PhysicsShape? shape) => checked(shape switch
    {
        null => 0,
        ConvexHullShape hull => 256 + 24L * hull.Points.Length,
        TriangleMeshShape mesh => 256 + 24L * mesh.Vertices.Length,
        CompoundShape compound => 256 + 64L * compound.Children.Length + compound.Children.Sum(c => CopyBytes(c.Shape)),
        _ => 256,
    });
    static long SweepBytes(PhysicsShape shape) => checked(shape switch
    {
        ConvexHullShape hull => 256 + 48L * hull.Points.Length,
        TriangleMeshShape mesh => 256 + 48L * mesh.Vertices.Length,
        CompoundShape compound => 256 + 64L * compound.Children.Length + compound.Children.Sum(c => SweepBytes(c.Shape)),
        CylinderShape => 8192,
        _ => 1024,
    });
}
