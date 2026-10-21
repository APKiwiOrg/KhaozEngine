using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

/// <summary>Frozen cascade receivers and finite up-light caster reach for native captures.</summary>
public sealed class ShadowCaptureDomain
{
    public IReadOnlyList<MeshBounds> ReceiverVolumes { get; }
    public IReadOnlyList<MeshBounds> CasterVolumes { get; }
    public Vector3 DirectionToLight { get; }
    internal IReadOnlyList<Vector3> Centres { get; }
    internal IReadOnlyList<float> Radii { get; }
    ShadowCaptureDomain(MeshBounds[] receivers, MeshBounds[] casters, Vector3[] centres, float[] radii, Vector3 direction)
    { ReceiverVolumes = Array.AsReadOnly(receivers); CasterVolumes = Array.AsReadOnly(casters); Centres = Array.AsReadOnly(centres); Radii = Array.AsReadOnly(radii); DirectionToLight = direction; }
    public static ShadowCaptureDomain Compute(Matrix4x4 viewProjection, Vector3 directionToLight, ShadowSettings shadows, float maxCasterDistance)
    {
        ArgumentNullException.ThrowIfNull(shadows);
        if (!float.IsFinite(maxCasterDistance) || maxCasterDistance < 0 || !float.IsFinite(directionToLight.LengthSquared()) || directionToLight.LengthSquared() < 1e-12f)
            throw new ArgumentException("Invalid capture shadow domain.");
        Vector3[] corners = new Vector3[8];
        if (!ShadowMapMath.FrustumCornersWorld(viewProjection, corners)) throw new ArgumentException("Invalid capture camera.");
        var direction = Vector3.Normalize(directionToLight);
        int count = shadows.ResolvedCascadeCount;
        var receivers = new MeshBounds[count]; var casters = new MeshBounds[count]; var centres = new Vector3[count]; var radii = new float[count];
        var splits = new float[count]; ShadowMapMath.FillCascadeSplits(splits, count, shadows.ShadowNearDistance, shadows.ResolvedMaxDistance);
        Vector3 near = (corners[0] + corners[1] + corners[2] + corners[3]) * .25f;
        Vector3 far = (corners[4] + corners[5] + corners[6] + corners[7]) * .25f;
        float range = Vector3.Distance(near, far), previous = 0;
        for (int i = 0; i < count; i++)
        {
            float end = Math.Clamp(splits[i] / MathF.Max(range, .001f), previous, 1);
            ShadowMapMath.SliceBoundingSphere(corners, previous, end, out centres[i], out radii[i]);
            var points = Enumerable.Range(0, 4).SelectMany(k => new[] { Vector3.Lerp(corners[k], corners[k + 4], previous), Vector3.Lerp(corners[k], corners[k + 4], end) }).ToArray();
            var min = points.Aggregate(new Vector3(float.PositiveInfinity), Vector3.Min);
            var max = points.Aggregate(new Vector3(float.NegativeInfinity), Vector3.Max);
            receivers[i] = new(min, max);
            var delta = direction * maxCasterDistance;
            casters[i] = new(Vector3.Min(min, min + delta), Vector3.Max(max, max + delta)); previous = end;
        }
        return new(receivers, casters, centres, radii, direction);
    }
    internal bool ContainsCaster(MeshBounds bounds) => CasterVolumes.Any(b => Intersects(b, bounds));
    internal static bool Intersects(MeshBounds a, MeshBounds b) => a.Min.X <= b.Max.X && a.Max.X >= b.Min.X &&
        a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y && a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;
}
