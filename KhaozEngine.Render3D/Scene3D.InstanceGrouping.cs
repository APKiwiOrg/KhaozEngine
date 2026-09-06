using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D
{
    public sealed partial class Scene3D
    {
        readonly List<uint> _groupWriteCursors = new();

        /// <summary>
        /// Group queued <paramref name="items"/> by mesh handle into <paramref name="instanceData"/> (a flat array
        /// ordered so all instances of one mesh are contiguous) and <paramref name="runs"/> (one
        /// <see cref="MeshRun"/> per unique mesh handle, in first-seen order). Pure + headless-testable. Both output
        /// lists are Cleared and refilled (no realloc on the caller's reused buffers). <paramref name="meshRunIndex"/>
        /// is scratch (mesh handle -&gt; run index): pass the caller's reused dictionary to keep the whole grouping
        /// pass allocation-free and O(instances) (a dictionary lookup instead of a linear scan of the runs seen so
        /// far). Omit it (the default) for a one-off/test call, which allocates a local scratch dictionary instead.
        /// <paramref name="castKinds"/> (optional) receives each SLOT's shadow-caster classification, index-aligned
        /// to <paramref name="instanceData"/>: this is the one place that still knows which queued instance a slot
        /// came from, so the opt-out flag is read here (see Scene3D.ShadowCasters.cs). Omit it and no classification
        /// is produced, which the depth pass reads as "every caster opaque" (the pre-policy shape).
        /// <paramref name="retained"/> optionally omits rejected queued slots from the packed stream. Mesh order
        /// still follows the complete queue, so removing a hidden first instance never reorders visible meshes.
        /// <paramref name="writeCursorScratch"/> reuses per-mesh cursors even when more than 64 meshes are queued.
        /// </summary>
        internal static void GroupInstances(IReadOnlyList<SceneInstances.Instance> items,
            List<ModelRenderer.InstanceData> instanceData, List<MeshRun> runs,
            Dictionary<(int Index, int Generation), int>? meshRunIndex = null,
            List<ShadowCastKind>? castKinds = null, ReadOnlySpan<bool> retained = default,
            List<uint>? writeCursorScratch = null)
        {
            if (!retained.IsEmpty && retained.Length != items.Count)
                throw new ArgumentException("Retention must cover every queued instance.", nameof(retained));
            instanceData.Clear();
            runs.Clear();
            castKinds?.Clear();
            if (items.Count == 0) return;

            meshRunIndex ??= new Dictionary<(int, int), int>();
            meshRunIndex.Clear();

            int[]? rentedSlots = null;
            Span<int> runSlots = items.Count <= 512 ? stackalloc int[items.Count]
                : (rentedSlots = ArrayPool<int>.Shared.Rent(items.Count));
            try
            {
                // First-seen mesh order. Instances are usually already mesh-coherent (one mesh per kind), so the run
                // list stays short. Pass 1: collect distinct mesh handles in first-seen order + count per mesh, O(1)
                // amortized per instance via meshRunIndex (a per-instance linear scan of the runs list so far would be
                // O(instances x uniqueMeshes), the hot path this dictionary replaces).
                for (int i = 0; i < items.Count; i++)
                {
                    MeshHandle mesh = items[i].Mesh;
                    var key = (mesh.Index, mesh.Generation);
                    uint count = retained.IsEmpty || retained[i] ? 1u : 0u;
                    if (meshRunIndex.TryGetValue(key, out int slot))
                    {
                        if (count != 0) runs[slot] = new MeshRun(mesh, 0, runs[slot].Count + count);
                    }
                    else
                    {
                        slot = runs.Count;
                        meshRunIndex[key] = slot;
                        runs.Add(new MeshRun(mesh, 0, count));
                    }
                    runSlots[i] = slot;
                }

                // Assign each run a start offset (prefix sum), and record per-mesh write cursors.
                // runs currently holds (meshIndex, 0, count) in first-seen order.
                uint cursor = 0;
                if (writeCursorScratch != null) CollectionsMarshal.SetCount(writeCursorScratch, runs.Count);
                Span<uint> writeCursor = writeCursorScratch != null ? CollectionsMarshal.AsSpan(writeCursorScratch)
                    : runs.Count <= 64 ? stackalloc uint[runs.Count] : new uint[runs.Count];
                for (int r = 0; r < runs.Count; r++)
                {
                    uint start = cursor;
                    writeCursor[r] = start;
                    cursor += runs[r].Count;
                    runs[r] = new MeshRun(runs[r].Mesh, start, runs[r].Count);
                }

                // Scatter into the saved mesh slots without hashing every instance's handle a second time.
                int total = (int)cursor;
                for (int i = 0; i < total; i++) instanceData.Add(default);
                if (castKinds != null) for (int i = 0; i < total; i++) castKinds.Add(ShadowCastKind.Opaque);
                for (int i = 0; i < items.Count; i++)
                {
                    if (!retained.IsEmpty && !retained[i]) continue;
                    var inst = items[i];
                    int slot = runSlots[i];
                    uint dst = writeCursor[slot]++;
                    bool dissolving = inst.Dissolving;
                    if (castKinds != null) castKinds[(int)dst] = ClassifyCaster(inst);
                    instanceData[(int)dst] = new ModelRenderer.InstanceData
                    {
                        Model = inst.World,
                        Tint = inst.Tint,
                        // During a dissolve the emissive channel carries the edge colour and Dissolve = (threshold, edge
                        // width) lights the gated ModelFrag term. A non-dissolving draw keeps the material emissive and a
                        // zero Dissolve, so it is byte-identical to the pre-dissolve packing (issue #253). SpecParams.z is
                        // left 0 for ApplyAlphaCutoffs to fill with the MASK cutoff, independent of dissolve.
                        Emissive = dissolving ? inst.DissolveEdge : inst.Material.Emissive,
                        SpecParams = new Vector4(inst.Material.Specular, inst.Material.Shininess, 0f, 0f),
                        Dissolve = dissolving ? new Vector2(inst.DissolveThreshold, inst.DissolveEdgeWidth) : Vector2.Zero,
                    };
                }
            }
            finally
            {
                if (rentedSlots is not null) ArrayPool<int>.Shared.Return(rentedSlots);
            }
        }

    }
}
