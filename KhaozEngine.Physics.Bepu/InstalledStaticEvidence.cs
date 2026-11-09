using System;
using System.Numerics;
using BepuPhysics;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Writer-refreshed evidence metadata. Recipes reuse their storage across rebases and retain no query
/// results or world vertices. A conservative feature upper bound authorizes skipping excluded geometry.</summary>
internal sealed class InstalledStaticEvidence(InstalledSweepGeometry? geometry)
{
    internal InstalledSweepGeometry? Geometry { get; } = geometry;
    internal RigidPose Root;
    internal InstalledStaticEvidence[] Children = [];
    internal StaticEvidenceMeshIndex? Mesh;
    internal PhysicsEvidenceStatus Status;
    internal int Features;

    internal bool TryCountUpper(Vector3 minimum, Vector3 maximum, int limit, out int count)
    {
        count = 0;
        if (Geometry?.Mesh is not null && Status != PhysicsEvidenceStatus.Complete) return true;
        foreach (var child in Children)
        {
            if (!child.TryCountUpper(minimum, maximum, limit - count, out int next)) return false;
            count += next;
        }
        if (Mesh is { } mesh) return mesh.TryCountUpper(minimum, maximum, limit, out count);
        if (Features > limit - count) return false;
        count += Features;
        return true;
    }
}
