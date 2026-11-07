using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.MapDoc.Prefabs;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>An immutable mesh in a horizontal world frame, with world-datum heights.</summary>
public sealed class MapFrameMesh
{
    public WorldFrame Frame { get; }
    public IReadOnlyList<Vector3> LocalPositions { get; }
    public IReadOnlyList<MapCompiledFace> Faces { get; }

    internal MapFrameMesh(WorldFrame frame, IEnumerable<Vector3> localPositions, IEnumerable<MapCompiledFace> faces)
    {
        Frame = frame;
        LocalPositions = Array.AsReadOnly(localPositions.ToArray());
        Faces = Array.AsReadOnly(faces.ToArray());
    }
}

/// <summary>Converts compiled geometry without an absolute world-float round trip.</summary>
public static class MapFrameLocal
{
    public static MapFrameMesh ToFrame(MapCompiledPatch patch, WorldFrame frame)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return ToFrame(patch.Anchor, patch.Offsets, patch.Faces, frame, patch.Key);
    }

    public static MapFrameMesh ToFrame(MapCompiledStrip strip, WorldFrame frame)
    {
        ArgumentNullException.ThrowIfNull(strip);
        return ToFrame(strip.Anchor, strip.Offsets, strip.Faces, frame, null);
    }

    /// <summary>Composes exact prefab vertices in double before the final frame-local float rounding.</summary>
    public static MapFrameMesh CompileInFrame(MapCompiledPatch local, MapTransform placement, WorldFrame frame)
    {
        ArgumentNullException.ThrowIfNull(local);
        var transform = new MapResourceTransform(MapEffectiveTransform.From(placement));
        var vertices = placement == new MapTransform(Vector3.Zero, 0, 1) ? local.ExactVertices.ToArray() : local.ExactVertices.Select(transform.Point).ToArray();
        var positions = vertices.Select(p => transform.ToFrame(p, frame)).ToArray();
        var faces = local.Faces.Select(f => f with { Normal = MapEffectiveGeometry.Normal(new(vertices[f.A], vertices[f.B], vertices[f.C])) }).ToArray();
        return new(frame, positions, faces);
    }

    static MapFrameMesh ToFrame(MapSubmissionAnchor anchor, IReadOnlyList<Vector3> offsets,
        IReadOnlyList<MapCompiledFace> faces, WorldFrame frame, MapPatchKey? patch)
    {
        long dx, dz;
        try
        {
            dx = checked(anchor.X - (long)frame.X * (long)WorldFrame.Grid);
            dz = checked(anchor.Z - (long)frame.Z * (long)WorldFrame.Grid);
        }
        catch (OverflowException error)
        {
            throw new MapDocumentException(MapDocumentRefusal.FrameRadiusExceeded, "compiled geometry exceeds frame radius", error)
            { Patch = patch, Limit = (long)WorldFrame.MaxLocalRadius, Operation = nameof(ToFrame) };
        }

        float x = dx, y = anchor.Y, z = dz;
        var positions = new Vector3[offsets.Count];
        for (int i = 0; i < positions.Length; i++)
        {
            Vector3 offset = offsets[i];
            Vector3 position = new(x + offset.X, y + offset.Y, z + offset.Z);
            ValidatePosition(position, patch);
            positions[i] = position;
        }
        return new(frame, positions, faces);
    }

    static void ValidatePosition(Vector3 position, MapPatchKey? patch)
    {
        // Check the planar magnitude in double so the radius test adds no float rounding.
        double x = position.X, z = position.Z;
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Z) ||
            x * x + z * z > (double)WorldFrame.MaxLocalRadius * WorldFrame.MaxLocalRadius)
            throw new MapDocumentException(!float.IsFinite(position.X) || !float.IsFinite(position.Z)
                ? MapDocumentRefusal.ValueNonfinite : MapDocumentRefusal.FrameRadiusExceeded, "compiled geometry exceeds frame radius")
            {
                Patch = patch,
                Operation = nameof(ToFrame),
                PositionX = x,
                PositionZ = z,
                Limit = (long)WorldFrame.MaxLocalRadius,
                FieldPath = !float.IsFinite(position.X) ? "$.position.x" : !float.IsFinite(position.Z) ? "$.position.z" : null,
                ActualNumber = !float.IsFinite(position.X) ? x : !float.IsFinite(position.Z) ? z : null,
            };
        if (!float.IsFinite(position.Y))
            throw new MapDocumentException(MapDocumentRefusal.ValueNonfinite, "frame-local height is not representable")
            { Patch = patch, Operation = nameof(ToFrame), FieldPath = "$.position.y", PositionY = position.Y, ActualNumber = position.Y };
    }
}
