using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    // The same stable model, motion and casting expansion is used by retained ground cover and native uploads.
    internal ScenePreparedFoliage PrepareFoliageUpload(SceneFoliageSource source, SceneUploadDemand demand)
    {
        int count = 0;
        foreach (int model in source.BorrowModelIndices) count = checked(count + source.Models[model].Parts.Count);
        var instances = new FoliageInstance[count];
        var parts = new SceneFoliageExpandedPart[count];
        Vector2[]? roots = source.TransformSpace == SceneFoliageTransformSpace.World ? new Vector2[count] : null;
        int next = 0;
        for (int row = 0; row < source.BorrowTransforms.Length; row++)
        {
            Matrix4x4 transform = source.BorrowTransforms[row];
            Vector2 root = new(transform.M41, transform.M43);
            if (source.TransformSpace == SceneFoliageTransformSpace.World)
            {
                transform.M41 = (float)((double)transform.M41 - source.Anchor.X);
                transform.M42 = (float)((double)transform.M42 - source.Anchor.Y);
                transform.M43 = (float)((double)transform.M43 - source.Anchor.Z);
            }
            int model = source.BorrowModelIndices[row];
            var binding = source.Models[model];
            FoliageModelExpansion.WriteParts(binding.Parts, transform, source.BorrowRanks[row],
                binding.Motion, binding.CastShadows, instances.AsSpan(next));
            for (int part = 0; part < binding.Parts.Count; part++)
            {
                MeshHandle mesh = binding.Parts[part];
                if (FoliageBounds(mesh) is null) throw new ArgumentException("Foliage binding was released before preparation.", nameof(source));
                parts[next] = new(row, model, part, mesh);
                if (roots is not null) roots[next] = root;
                next++;
            }
        }
        var layout = FoliagePatchLayout.Build(instances, FoliageBounds, anchor: source.Anchor, absoluteRoots: roots,
            meshOrder: FoliageMeshOrder);
        var ordered = new SceneFoliageExpandedPart[count];
        for (int i = 0; i < count; i++) ordered[i] = parts[layout.InputOrder[i]];
        var patches = new SceneFoliagePatch[layout.Patches.Length];
        for (int i = 0; i < patches.Length; i++)
        {
            var patch = layout.Patches[i];
            int original = layout.InputOrder[patch.Start];
            var transform = instances[original].Transform;
            double x = roots is not null ? roots[original].X : (double)source.Anchor.X + transform.M41;
            double z = roots is not null ? roots[original].Y : (double)source.Anchor.Z + transform.M43;
            patches[i] = new(Math.Floor(x / 8), Math.Floor(z / 8), patch.Mesh, patch.CastsShadows, patch.Start, patch.Count);
        }
        var packed = PackFoliage(layout);
        return new(source.Anchor, ordered, patches, MemoryMarshal.AsBytes(packed.AsSpan()).ToArray(), demand) { Layout = layout, PointStream = FoliagePointShadowStream.Build(layout.Instances, FoliageBounds, source.Anchor) };
    }
    ModelRenderer.FoliageInstanceData[] PackFoliage(FoliagePatchLayout layout)
    {
        var data = new ModelRenderer.FoliageInstanceData[layout.Instances.Length];
        for (int i = 0; i < data.Length; i++)
        {
            var item = layout.Instances[i];
            Mesh mesh = _meshes[item.Mesh.Index]!.Value;
            float height = mesh.Bounds.Max.Y - mesh.Bounds.Min.Y;
            data[i] = new()
            {
                Model = item.Transform,
                Motion = item.Motion.ToVector4(),
                Parameters = new(item.ThinningRank, mesh.Bounds.Min.Y, height > .00001f ? 1f / height : 0f, mesh.AlphaCutoff)
            };
        }
        return data;
    }
    internal FoliageBatch PublishPreparedFoliage(ScenePreparedFoliage prepared, IGpuBuffer? buffer, IGpuBuffer anchorBuffer, List<IDisposable> owned)
    {
        foreach (var part in prepared.ExpandedParts)
            if (FoliageBounds(part.Mesh) is null) throw new ArgumentException("Foliage part was released before publication.", nameof(prepared));
        var batch = new FoliageBatch(this, prepared.Layout!, Array.Empty<ModelRenderer.FoliageInstanceData>())
        {
            Buffer = buffer,
            Pending = null,
            PreparedAnchor = prepared.Anchor,
            AnchorBuffer = anchorBuffer,
            PreparedPointStream = prepared.PointStream
        };
        batch.AnchorSet = _model.CreatePreparedFoliageAnchorSet(anchorBuffer); owned.Add(batch.AnchorSet);
        batch.PreparedPointStream!.AttachPreparedBuffer(buffer);
        _foliageBatches.Add(batch);
        return batch;
    }
}
