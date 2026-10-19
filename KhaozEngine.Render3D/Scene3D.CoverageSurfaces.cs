using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    internal Action<bool>? CoverageSurfaceAllocationObserverForTests { get; set; }

    void AppendCoverageSurfaces(FrustumPlanes camera, bool shadows, int cascades, bool points)
    {
        CoverageSurfaceAllocationObserverForTests?.Invoke(true);
        foreach (CoveragePreparation p in _coverageArena.Preparations)
        {
            if (p.Owner.Disposed || p.Outcome.Presentation != CoveragePresentation.Covered) continue;
            for (int i = 0; i < p.Owner.Meshes.Length; i++)
            {
                SkinnedInstanceDraw? submitted = p.Draws[i];
                SkinnedInstanceDraw? outlined = p.Outlines[i];
                if (submitted is null && outlined is null) continue;
                SkinnedInstanceDraw draw = submitted ?? outlined!.Value;
                SkinnedMeshEntry entry = _skinnedMeshes[draw.Mesh.Index]!;
                var range = p.Owner.Topology.MaterialRanges[i];
                int start = _cpuSkinnedVerts.Count;
                p.BaseVertices[i] = start;
                foreach (ref readonly ModelVertex vertex in p.Candidate.AsSpan(range.FirstVertex, range.VertexCount))
                    _cpuSkinnedVerts.Add(vertex);
                var (main, shadow) = ClassifyCoverageVisibility(p, camera, shadows && draw.CastsShadows, cascades);
                main &= submitted is not null && !draw.ShadowOnly;
                PointShadowCasterSphere sphere = CoverageSphere(p);
                bool point = points && draw.CastsShadows && PointRequestCanRetainSkinned(sphere, Post.Quality.Shadows.PointShadows);
                if (main) _drawnSkinnedInstances++;
                else if (submitted is not null && !draw.ShadowOnly) _culledSkinnedInstances++;
                bool dissolve = draw.Dissolve > 0 || draw.DissolveComplement > 0;
                _cpuSkinnedInstances.Add(new ModelRenderer.InstanceData
                {
                    Model = ToRender(draw.Model),
                    Tint = draw.Tint,
                    Emissive = dissolve ? draw.DissolveEdgeColor : draw.Material.Emissive,
                    SpecParams = new(draw.Material.Specular, draw.Material.Shininess, entry.AlphaCutoff, 0),
                    IsDynamic = 1,
                    Dissolve = dissolve ? new(draw.Dissolve, draw.DissolveEdgeWidth) : Vector2.Zero,
                    DissolveComplement = draw.DissolveComplement,
                });
                ShadowCastKind kind = submitted is null || !draw.CastsShadows || !shadow && !main && !point
                    ? ShadowCastKind.None : ShadowDepthSelection.ClassifySkinnedCaster(new SkinnedSceneInstances.Instance(draw));
                _cpuSkinnedDraws.Add(new CpuSkinnedDraw(p.Owner.Indices[i], range.IndexCount, GpuIndexFormat.UInt32,
                    start, entry.MaterialSet, dissolve, main, kind, sphere, draw.Motion, -1, range.VertexCount));
            }
        }
        CoverageSurfaceAllocationObserverForTests?.Invoke(false);
    }

    PointShadowCasterSphere CoverageSphere(CoveragePreparation p)
    {
        System.Diagnostics.Debug.Assert(p.SphereView == _coverageView, "Coverage spheres are frozen once per render view.");
        return p.Sphere;
    }

    PointShadowCasterSphere ComputeCoverageSphere(CoveragePreparation p)
    {
        MeshBounds world = p.WorldBounds;
        Vector3 center = new((float)(((double)world.Min.X + world.Max.X) * .5),
            (float)(((double)world.Min.Y + world.Max.Y) * .5), (float)(((double)world.Min.Z + world.Max.Z) * .5));
        double squared = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            double reach = Math.BitIncrement(Math.Max(Math.Abs((double)world.Min[axis] - center[axis]),
                Math.Abs((double)world.Max[axis] - center[axis])));
            squared = Math.BitIncrement(squared + Math.BitIncrement(reach * reach));
        }
        CoverageSphereEvaluationsForTests++;
        return new(center, MathF.BitIncrement((float)Math.BitIncrement(Math.Sqrt(squared))));
    }

    (bool Main, bool Shadow) ClassifyCoverageVisibility(CoveragePreparation p, FrustumPlanes camera, bool shadows, int cascades)
    {
        PointShadowCasterSphere sphere = CoverageSphere(p);
        bool main = !FrustumCulling || camera.IntersectsSphere(sphere.Center, sphere.Radius);
        bool shadow = false;
        if (shadows)
            for (int c = 0; c < cascades && !shadow; c++)
                shadow = _shadowFrustums[c].IntersectsSphereExceptPlane(sphere.Center, sphere.Radius, 4);
        return (main, shadow);
    }
}
