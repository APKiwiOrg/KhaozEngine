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
                    Model = ToRender(draw.Model), Tint = draw.Tint,
                    Emissive = dissolve ? draw.DissolveEdgeColor : draw.Material.Emissive,
                    SpecParams = new(draw.Material.Specular, draw.Material.Shininess, entry.AlphaCutoff, 0),
                    IsDynamic = 1, Dissolve = dissolve ? new(draw.Dissolve, draw.DissolveEdgeWidth) : Vector2.Zero,
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
        Span<double> low = stackalloc double[3] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        Span<double> high = stackalloc double[3] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        Span<float> matrix = stackalloc float[16];
        System.Runtime.InteropServices.MemoryMarshal.Cast<Matrix4x4, float>(
            System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(in p.Model, 1)).CopyTo(matrix);
        for (int bits = 0; bits < 8; bits++)
            for (int c = 0; c < 3; c++)
            {
                CoverageScalarBounds coordinate = new(matrix[12 + c], matrix[12 + c]);
                for (int axis = 0; axis < 3; axis++)
                {
                    float value = (bits & (1 << axis)) == 0 ? p.UnionBounds.Min[axis] : p.UnionBounds.Max[axis];
                    coordinate = CoverageIntervals.Add(coordinate, CoverageIntervals.Multiply(
                        new(value, value), new(matrix[axis * 4 + c], matrix[axis * 4 + c])));
                }
                low[c] = Math.Min(low[c], coordinate.Min); high[c] = Math.Max(high[c], coordinate.Max);
            }
        double margin = 0;
        double attributes = CoverageUploadEvidence.Attributes(p.Candidate);
        for (int i = 0; i < p.Draws.Length; i++)
        {
            if (p.Draws[i] is { } draw)
                margin = Math.Max(margin, Perturbation(p.Model, p.UnionBounds, attributes,
                    DrawInputs(draw.Tint, draw.Material, draw.DissolveEdgeColor,
                        draw.Dissolve > 0 || draw.DissolveComplement > 0, true)));
            if (p.Outlines[i] is not null) margin = Math.Max(margin, Perturbation(p.Model, p.UnionBounds, attributes, 0));
        }
        Matrix4x4 render = ToRender(p.Model);
        double error = 0;
        for (int c = 0; c < 3; c++) error = Math.Max(error, CoverageShaderPosition.ModelError(render, p.UnionBounds, c));
        double residual = 0;
        for (int c = 0; c < 3; c++)
        {
            float reduced = c == 0 ? render.M41 : c == 1 ? render.M42 : render.M43;
            CoverageScalarBounds originResidual = CoverageIntervals.Subtract(
                CoverageIntervals.Add(new(reduced, reduced), new(_frameOrigin[c], _frameOrigin[c])),
                new(matrix[12 + c], matrix[12 + c]));
            residual = Math.Max(residual, Math.Max(Math.Abs(originResidual.Min), Math.Abs(originResidual.Max)));
        }
        margin = CoverageShaderPosition.Sum(margin, CoverageShaderPosition.Sum(error, residual));
        Vector3 center = new((float)((low[0] + high[0]) * .5), (float)((low[1] + high[1]) * .5), (float)((low[2] + high[2]) * .5));
        double squared = 0;
        for (int c = 0; c < 3; c++)
        {
            double reach = CoverageShaderPosition.Sum(Math.Max(Math.Abs(low[c] - center[c]), Math.Abs(high[c] - center[c])), margin);
            squared = CoverageShaderPosition.Sum(squared, Math.BitIncrement(reach * reach));
        }
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
