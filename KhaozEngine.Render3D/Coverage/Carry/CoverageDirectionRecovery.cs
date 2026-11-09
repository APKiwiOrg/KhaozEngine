using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Shared local direction policy. Recovery never changes positions or actor admission.</summary>
internal static class CoverageDirectionRecovery
{
    internal static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    internal static Vector3 Resolve(Vector3 candidate, bool valid, Vector3 uncovered, Vector3 authored, bool tangent)
    {
        if (valid && Finite(candidate)) return candidate;
        if (Finite(uncovered)) return uncovered;
        if (Finite(authored)) return authored;
        return tangent ? Vector3.UnitX : Vector3.UnitZ;
    }
    internal static float Handedness(float value) => float.IsFinite(value) ? value : 1;
    internal static Vector3 Authored(CoverageRefinement topology, int vertex, bool tangent)
    {
        var record = topology.Vertices[vertex];
        int source = record.SourceVertex >= 0 ? record.SourceVertex : topology.Source.Triangles[record.SourceTriangle].A;
        return Source(topology.Source, source, tangent);
    }
    internal static Vector3 Source(CoverageSourceTopology source, int vertex, bool tangent)
    {
        if (source.SkinVertices.IsEmpty) return new(float.NaN);
        var v = source.SkinVertices[vertex];
        return tangent ? new(v.Tangent.X, v.Tangent.Y, v.Tangent.Z) : v.Normal;
    }

    internal const string ShaderSource = """
        bool covDirectionFinite(float v) { return (floatBitsToUint(v) & 0x7f800000u) != 0x7f800000u; }
        bool covDirectionFinite3(vec3 v) { return covDirectionFinite(v.x) && covDirectionFinite(v.y) && covDirectionFinite(v.z); }
        bool covDirectionZero(vec3 v) { return v.x == 0.0 && v.y == 0.0 && v.z == 0.0; }
        vec3 covRecoverDirection(vec3 candidate, bool valid, vec3 uncovered, vec3 authored, bool tangent) {
            if (valid && covDirectionFinite3(candidate)) return candidate;
            if (covDirectionFinite3(uncovered)) return uncovered;
            if (covDirectionFinite3(authored)) return authored;
            return tangent ? vec3(1.0,0.0,0.0) : vec3(0.0,0.0,1.0);
        }
        float covDirectionHandedness(float v) { return covDirectionFinite(v) ? v : 1.0; }
        """;
}
