using System;
using System.Numerics;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.Physics;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>The volume a placement is picked and reached through, under <see cref="MapInteractionPolicy"/>. Without
/// a raise it is the scaled source shape unchanged, so apertures such as a doorway's opening survive. With a raise
/// every member is swept straight up in world space by <see cref="RaiseMetres"/>.</summary>
public sealed class MapInteractionEnvelope
{
    /// <summary>The placement this envelope belongs to.</summary>
    public string PlacementId { get; }

    /// <summary>The envelope shape in metres, placed at <see cref="WorldPose"/>.</summary>
    public PhysicsShape Shape { get; }

    /// <summary>The world pose of <see cref="Shape"/>.</summary>
    public Pose WorldPose { get; }

    /// <summary>The world bounds of <see cref="Shape"/> at <see cref="WorldPose"/>, after the raise.</summary>
    public MapBox3 Bounds { get; }

    /// <summary>How far every member was swept up, or zero when the source already reaches the minimum height.</summary>
    public float RaiseMetres { get; }

    /// <summary>True when the source was the collider of a solid asset, false when it was a selection volume.</summary>
    public bool DerivedFromCollider { get; }

    internal MapInteractionEnvelope(string placementId, PhysicsShape shape, Pose worldPose, MapBox3 bounds,
        float raiseMetres, bool derivedFromCollider)
    {
        PlacementId = placementId;
        Shape = shape;
        WorldPose = worldPose;
        Bounds = bounds;
        RaiseMetres = raiseMetres;
        DerivedFromCollider = derivedFromCollider;
    }

    // A cylinder whose axis leans further than this from world vertical, relative to the raise, cannot be swept.
    const float VerticalTolerance = 1e-5f;

    /// <summary>Builds the envelope of <paramref name="source"/>, already scaled to metres, at
    /// <paramref name="worldPose"/>.</summary>
    internal static MapInteractionEnvelope Build(string placementId, PhysicsShape source, bool derivedFromCollider,
        Pose worldPose)
    {
        MapBox3 sourceBounds = MapShapeBounds.Of(source, worldPose);
        double height = sourceBounds.MaxY - sourceBounds.MinY;
        float raise = (float)Math.Max(0d, MapInteractionPolicy.MinimumVerticalReachHeightMetres - height);
        if (raise == 0f)
            return new MapInteractionEnvelope(placementId, source, worldPose, sourceBounds, 0f, derivedFromCollider);

        // World up expressed in the shape's own frame. A yaw-only placement keeps it exactly vertical.
        Vector3 up = Vector3.Transform(new Vector3(0f, raise, 0f), Quaternion.Conjugate(worldPose.Orientation));
        (PhysicsShape swept, Vector3 offset) = Sweep(placementId, source, up);
        var pose = new Pose(worldPose.Position + Vector3.Transform(offset, worldPose.Orientation), worldPose.Orientation);
        return new MapInteractionEnvelope(placementId, swept, pose, MapShapeBounds.Of(swept, pose), raise,
            derivedFromCollider);
    }

    // Returns the swept shape and the offset its pose takes in the parent frame. Raise is world up in the shape frame.
    static (PhysicsShape Shape, Vector3 Offset) Sweep(string placementId, PhysicsShape shape, Vector3 raise)
    {
        switch (shape)
        {
            case BoxShape b:
                return (Hull(Corners(b.HalfExtents), raise), Vector3.Zero);
            case ConvexHullShape h:
                return (Hull(h.Points, raise), Vector3.Zero);
            case TriangleMeshShape m:
                // A hull of the mesh is coarse, which is acceptable only because a raise applies to objects under
                // the minimum reach height.
                return (Hull(m.Vertices, raise), Vector3.Zero);
            case CylinderShape c:
                {
                    float length = raise.Length();
                    if (MathF.Abs(raise.X) > VerticalTolerance * length || MathF.Abs(raise.Z) > VerticalTolerance * length)
                        throw new MapDocumentException(
                            $"Placement '{placementId}' interaction envelope cannot sweep a tilted cylinder.");
                    // The cylinder stands on its base. Pointing up it keeps its base and grows. Pointing down its base
                    // moves up by the raise.
                    return (new CylinderShape(c.Radius, c.Length + length), raise.Y < 0f ? raise : Vector3.Zero);
                }
            case CompoundShape co:
                {
                    var children = new CompoundChild[co.Children.Length];
                    for (int i = 0; i < children.Length; i++)
                    {
                        CompoundChild child = co.Children[i];
                        Quaternion orientation = child.Local.Orientation;
                        Vector3 childRaise = Vector3.Transform(raise, Quaternion.Conjugate(orientation));
                        (PhysicsShape swept, Vector3 offset) = Sweep(placementId, child.Shape, childRaise);
                        children[i] = new CompoundChild(swept,
                            new Pose(child.Local.Position + Vector3.Transform(offset, orientation), orientation));
                    }
                    return (new CompoundShape(children), Vector3.Zero);
                }
            default:
                throw new MapDocumentException(
                    $"Placement '{placementId}' interaction envelope cannot sweep a {shape.GetType().Name}.");
        }
    }

    static Vector3[] Corners(Vector3 h) => new[]
    {
        new Vector3(-h.X, -h.Y, -h.Z), new Vector3(h.X, -h.Y, -h.Z), new Vector3(-h.X, h.Y, -h.Z), new Vector3(h.X, h.Y, -h.Z),
        new Vector3(-h.X, -h.Y, h.Z), new Vector3(h.X, -h.Y, h.Z), new Vector3(-h.X, h.Y, h.Z), new Vector3(h.X, h.Y, h.Z),
    };

    static ConvexHullShape Hull(Vector3[] points, Vector3 raise)
    {
        var swept = new Vector3[points.Length * 2];
        for (int i = 0; i < points.Length; i++)
        {
            swept[i] = points[i];
            swept[points.Length + i] = points[i] + raise;
        }
        return new ConvexHullShape(swept);
    }
}
