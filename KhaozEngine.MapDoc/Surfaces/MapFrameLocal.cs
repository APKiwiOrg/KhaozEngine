using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;

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
        return ToFrame(patch.Anchor, patch.Offsets, patch.Faces, frame);
    }

    public static MapFrameMesh ToFrame(MapCompiledStrip strip, WorldFrame frame)
    {
        ArgumentNullException.ThrowIfNull(strip);
        return ToFrame(strip.Anchor, strip.Offsets, strip.Faces, frame);
    }

    /// <summary>Composes exact prefab vertices in double before the final frame-local float rounding.</summary>
    public static MapFrameMesh CompileInFrame(MapCompiledPatch local, MapTransform placement, WorldFrame frame)
    {
        ArgumentNullException.ThrowIfNull(local);
        if (!float.IsFinite(placement.Position.X) || !float.IsFinite(placement.Position.Y) ||
            !float.IsFinite(placement.Position.Z) || !float.IsFinite(placement.YawRadians) ||
            !float.IsFinite(placement.Scale) || placement.Scale <= 0)
            throw new MapDocumentException("frame-local placement must be finite with positive scale");

        // Reduce translation before composing, while the float placement is exact in double.
        double tx = (double)placement.Position.X - (long)frame.X * (long)WorldFrame.Grid;
        double ty = placement.Position.Y;
        double tz = (double)placement.Position.Z - (long)frame.Z * (long)WorldFrame.Grid;
        double c = Math.Cos(placement.YawRadians), s = Math.Sin(placement.YawRadians);
        double scale = placement.Scale;
        var positions = new Vector3[local.ExactVertices.Count];
        for (int i = 0; i < positions.Length; i++)
        {
            var vertex = local.ExactVertices[i];
            double x = vertex.X.ToDouble() * scale, y = vertex.Y.ToDouble() * scale,
                z = vertex.Z.ToDouble() * scale;
            Vector3 position = new((float)(tx + c * x + s * z), (float)(ty + y),
                (float)(tz - s * x + c * z));
            ValidatePosition(position);
            positions[i] = position;
        }

        var faces = new MapCompiledFace[local.Faces.Count];
        for (int i = 0; i < faces.Length; i++)
        {
            MapCompiledFace face = local.Faces[i];
            Vector3 normal = face.Normal;
            // Positive uniform scale preserves winding and normal length. Only yaw changes direction.
            faces[i] = face with
            {
                Normal = new((float)(c * normal.X + s * normal.Z), normal.Y,
                    (float)(-s * normal.X + c * normal.Z)),
            };
        }
        return new(frame, positions, faces);
    }

    static MapFrameMesh ToFrame(MapSubmissionAnchor anchor, IReadOnlyList<Vector3> offsets,
        IReadOnlyList<MapCompiledFace> faces, WorldFrame frame)
    {
        long dx, dz;
        try
        {
            dx = checked(anchor.X - (long)frame.X * (long)WorldFrame.Grid);
            dz = checked(anchor.Z - (long)frame.Z * (long)WorldFrame.Grid);
        }
        catch (OverflowException error)
        {
            throw new MapDocumentException("compiled geometry exceeds frame radius", error);
        }

        float x = dx, y = anchor.Y, z = dz;
        var positions = new Vector3[offsets.Count];
        for (int i = 0; i < positions.Length; i++)
        {
            Vector3 offset = offsets[i];
            Vector3 position = new(x + offset.X, y + offset.Y, z + offset.Z);
            ValidatePosition(position);
            positions[i] = position;
        }
        return new(frame, positions, faces);
    }

    static void ValidatePosition(Vector3 position)
    {
        // Check the planar magnitude in double so the radius test adds no float rounding.
        double x = position.X, z = position.Z;
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Z) ||
            x * x + z * z > (double)WorldFrame.MaxLocalRadius * WorldFrame.MaxLocalRadius)
            throw new MapDocumentException("compiled geometry exceeds frame radius");
        if (!float.IsFinite(position.Y))
            throw new MapDocumentException("frame-local height is not representable");
    }
}
