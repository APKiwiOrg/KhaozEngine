using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

/// <summary>Inventory of surviving upload ownership, independent of preparation and transfer peak sizing.</summary>
internal static class SceneResourceCpuInventory
{
    const long OwnerMetadataBytes = 1024, GpuObjectMetadataBytes = 256, MeshMetadataBytes = 256, FoliageMetadataBytes = 512;

    internal static long Measure(List<IDisposable> owned, bool mesh, ScenePreparedFoliage? foliage, int pendingBindings = 0,
        int createdTextures = 0)
    {
        // Bounded metadata for the upload, reservation, demand/digests, tracking entries and list headers.
        // Each remaining GPU object has a separate wrapper/binding allowance. GPU payload is charged separately. A
        // resident material texture the upload created survives under its reservation until destroyed, so it counts
        // as an owned object does. One it only shares is another owner's.
        long objects = checked(owned.Count + pendingBindings + createdTextures);
        long cpu = checked(OwnerMetadataBytes + ((long)owned.Capacity + createdTextures) * IntPtr.Size + objects * GpuObjectMetadataBytes + (mesh ? MeshMetadataBytes + CoverageUploadEvidence.RetainedCpuBytes : 0));
        if (foliage is null) return cpu;
        var layout = foliage.Layout!;
        var points = foliage.PointStream!;
        // Product/read-only wrappers, layout, point stream and batch control metadata, excluding its embedded
        // MotionCurrent and MotionPrevious uniform snapshots. Shared arrays count once.
        return checked(cpu + FoliageMetadataBytes + 2L * Unsafe.SizeOf<ModelRenderer.FoliageUniforms>() +
            ArrayBytes<int>(foliage.BorrowExpandedSourceRows.Length) +
            ArrayBytes<byte>(foliage.BorrowPackedInstances.Length) +
            ArrayBytes<SceneFoliageExpandedPart>(foliage.ExpandedParts.Count) +
            ArrayBytes<SceneFoliagePatch>(foliage.Patches.Count) +
            ArrayBytes<FoliageInstance>(layout.Instances.Length) +
            ArrayBytes<FoliagePatch>(layout.Patches.Length) +
            ArrayBytes<int>(layout.InputOrder.Length) +
            ArrayBytes<FoliagePointShadowCaster>(points.Casters.Length) +
            ArrayBytes<Matrix4x4>(points.LocalTransformCount));
    }

    internal static long UpperBound(SceneUploadDescription data, long expanded)
    {
        bool mesh = data.Mesh is not null, foliage = data.Foliage is not null;
        long objects = mesh ? 3 : 0;
        if (!data.ClipData.IsEmpty) objects++;
        if (data.GroundMaterial is { } ground) objects += 3 + (ground.Sampler.HasValue ? 1 : 0);
        if (data.Maps is { } maps)
        {
            bool albedo = maps.Albedo.HasValue, normal = maps.Normal.HasValue, roughness = maps.Roughness.HasValue,
                emissive = maps.Emissive.HasValue, glows = maps.EmissiveFactor != Vector3.Zero;
            // Each map's texture counts as this upload's even when the scene shares it, so the bound never depends on
            // what else is resident. A glow owns its parameter block.
            objects += (albedo ? 1 : 0) + (normal ? 1 : 0) + (roughness ? 1 : 0) + (emissive ? 1 : 0) + (glows ? 1 : 0) +
                SceneUploadPublication.MaterialBindings(albedo, albedo || normal || roughness || emissive || glows, maps.AlphaCutoff);
        }
        if (foliage) objects += 2 + (expanded > 0 ? 1 : 0);
        long cpu = checked(OwnerMetadataBytes + objects * (IntPtr.Size + GpuObjectMetadataBytes) + (mesh ? MeshMetadataBytes + CoverageUploadEvidence.RetainedCpuBytes : 0));
        if (!foliage) return cpu;
        // Every expanded instance may occupy a separate patch and cast a point shadow. This bound needs no
        // live mesh lookup, sorting or expansion, and keeps the original complete preparation peak unchanged.
        return checked(cpu + FoliageMetadataBytes + 2L * Unsafe.SizeOf<ModelRenderer.FoliageUniforms>() +
            ArrayBytes<int>(expanded) + ArrayBytes<byte>(expanded * ScenePreparedFoliage.InstanceStride) +
            ArrayBytes<SceneFoliageExpandedPart>(expanded) + ArrayBytes<SceneFoliagePatch>(expanded) +
            ArrayBytes<FoliageInstance>(expanded) + ArrayBytes<FoliagePatch>(expanded) + ArrayBytes<int>(expanded) +
            ArrayBytes<FoliagePointShadowCaster>(expanded) + ArrayBytes<Matrix4x4>(expanded));
    }

    static long ArrayBytes<T>(long count) => count == 0 ? 0 : checked(24 + Scene3D.Align(count * Unsafe.SizeOf<T>(), 8));
}
