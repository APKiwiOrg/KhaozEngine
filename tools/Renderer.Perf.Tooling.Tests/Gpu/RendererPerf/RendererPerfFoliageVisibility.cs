using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>Foliage patches and candidate instances the main pass submits, by the engine's own rules: the patch
/// layout <c>CreateFoliageBatch</c> builds, the rank prefix <c>DrawFoliage</c> queues around the focus, and the
/// bounding-sphere frustum test <c>PrepareFoliageFrame</c> applies. No interactors, as in the recipes.</summary>
internal sealed class RendererPerfFoliageVisibility
{
    readonly FoliagePatchLayout _layout;
    readonly FoliageRenderSettings _settings;

    internal RendererPerfFoliageVisibility(ReadOnlySpan<FoliageInstance> instances, GltfMesh mesh, float patchSize,
        FoliageRenderSettings settings)
    {
        MeshBounds bounds = MeshBounds.FromVertices(mesh.Vertices);
        _layout = FoliagePatchLayout.Build(instances, _ => bounds, patchSize);
        _settings = settings;
    }

    internal int Patches => _layout.Patches.Length;

    internal (int Patches, int Candidates) Submitted(Vector3 focus, Matrix4x4 viewProjection)
    {
        FrustumPlanes frustum = FrustumPlanes.Extract(viewProjection);
        int patches = 0, candidates = 0;
        foreach (FoliagePatch patch in _layout.Patches)
        {
            int count = patch.CandidateCount(_layout.Instances, focus, _settings);
            if (count == 0) continue;
            // Mirrors DrawFoliage: legacy patches pad by blade bend, others by their animated bounds.
            bool legacy = !patch.CastsShadows && !patch.HasCustomMotion;
            MeshBounds bounds = legacy ? patch.Bounds : patch.AnimatedBounds(_settings.WindStrength);
            float radius = legacy ? patch.CullingRadius(_settings.WindStrength > 0f) : bounds.Radius;
            if (!frustum.IntersectsSphere(bounds.Center, radius)) continue;
            patches++;
            candidates += count;
        }
        return (patches, candidates);
    }

    /// <summary>Submitted patch and candidate ranges over the frames, with the same edge tolerance as
    /// <see cref="RendererPerfMainPassVisibility.Range"/>.</summary>
    internal (RendererPerfVisibleRange Patches, RendererPerfVisibleRange Candidates) Range(
        IEnumerable<(Vector3 Focus, Matrix4x4 ViewProjection)> frames)
    {
        int minPatches = int.MaxValue, maxPatches = 0, minCandidates = int.MaxValue, maxCandidates = 0;
        foreach (var (focus, viewProjection) in frames)
        {
            var narrow = Submitted(focus, RendererPerfMainPassVisibility.Narrowed(viewProjection));
            var wide = Submitted(focus, RendererPerfMainPassVisibility.Widened(viewProjection));
            minPatches = Math.Min(minPatches, narrow.Patches);
            minCandidates = Math.Min(minCandidates, narrow.Candidates);
            maxPatches = Math.Max(maxPatches, wide.Patches);
            maxCandidates = Math.Max(maxCandidates, wide.Candidates);
        }
        return (new(minPatches, maxPatches), new(minCandidates, maxCandidates));
    }
}
