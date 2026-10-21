using System;
using System.Numerics;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Physics;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>The volume a placement is picked and reached through, under <see cref="MapInteractionPolicy"/>. Without
/// a raise it is the scaled source shape unchanged, so apertures such as a doorway's opening survive. With a raise
/// every member is swept straight up in world space by <see cref="RaiseMetres"/>. A vertical cylinder is lengthened
/// from its base. A tilted cylinder becomes the hull of a circumscribed 32-gon at each end cap and the same points
/// raised. This shapes only the interaction envelope, never physics.</summary>
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

    internal const int CapSides = PropQueryGeometry.CapSides;

    /// <summary>Builds the shared upward envelope after admitting its allocation cost.</summary>
    internal static MapInteractionEnvelope Build(string placementId, PhysicsShape source, bool derivedFromCollider,
        Pose worldPose)
    {
        // Preserve the released bounds refusal before charging sweep preparation.
        MapBox3 unrotated = MapShapeBounds.Of(source, Pose.At(worldPose.Position));
        double height = unrotated.MaxY - unrotated.MinY;
        float raise = (float)Math.Max(0d, MapInteractionPolicy.MinimumVerticalReachHeightMetres - height);
        if (raise != 0f) MapSceneAdmission.Current?.ReserveBytes(MapPlacementPreparation.SweepBytes(source));
        try
        {
            var envelope = PropQueryGeometry.RaiseEnvelope(source, worldPose, MapInteractionPolicy.MinimumVerticalReachHeightMetres);
            return new(placementId, envelope.Shape, envelope.Pose, MapShapeBounds.Of(envelope.Shape, envelope.Pose),
                envelope.RaiseMetres, derivedFromCollider);
        }
        catch (NotSupportedException)
        {
            throw new MapDocumentException($"Placement '{placementId}' interaction envelope cannot sweep a {source.GetType().Name}.");
        }
    }
}
