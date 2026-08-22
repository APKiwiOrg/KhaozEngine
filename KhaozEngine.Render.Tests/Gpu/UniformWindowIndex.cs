using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>One uniform-buffer window a resource set binds: <see cref="Bytes"/> bytes of
    /// <see cref="Buffer"/> starting at <see cref="Offset"/>. A <see cref="Dynamic"/> window is rebased per draw
    /// by the offset handed to <c>SetGraphicsResourceSet(slot, set, dynamicOffset)</c>, which is how one buffer
    /// serves many passes from separate slots.</summary>
    internal readonly record struct UniformWindow(IGpuBuffer Buffer, uint Offset, uint Bytes, bool Dynamic)
    {
        /// <summary>The window as a draw that supplied <paramref name="dynamicOffset"/> actually reads it.</summary>
        internal UniformWindow Rebased(uint dynamicOffset) =>
            Dynamic ? this with { Offset = Offset + dynamicOffset } : this;
    }

    /// <summary>
    /// WHICH BYTES OF WHICH UNIFORM BUFFER EACH RESOURCE SET READS. <see cref="UniformRewriteAudit"/> needs this
    /// to tell the engine's sanctioned whole-mirror-per-slot pattern from a real ring collapse: a pass packs its
    /// own slot and uploads the WHOLE buffer, so a second pass's upload legitimately differs from the first in the
    /// slot no earlier draw ever bound. Comparing the whole overlapping range flags that pattern, which is a false
    /// positive on shipped, correct code.
    ///
    /// <para><b>THREE FACTS, NONE OF THEM ON THE HANDLES.</b> <see cref="IGpuResourceSet"/> exposes nothing at all,
    /// and neither the bound resources nor the layout's <see cref="GpuResourceLayoutElement.Dynamic"/> flags can be
    /// read back from one. So they are remembered at the one place that sees them, the factory, and this type is
    /// where <see cref="UniformBufferTrackingGpuDevice"/> puts them.</para>
    ///
    /// <para><b>AN UNKNOWN LAYOUT IS TREATED CONSERVATIVELY, AND COUNTED.</b> A set built against a layout this
    /// index never saw (created through an undecorated factory) has no per-slot kinds and no dynamic flags, so
    /// every bound resource that IS a tracked uniform buffer contributes its whole extent as a static window. That
    /// over-reports what a draw reads, which can only turn a safe rewrite into a reported hazard and never the
    /// other way, and <see cref="SetsWithUnknownLayout"/> is non-zero so a caller can refuse to believe the
    /// answer.</para>
    /// </summary>
    internal sealed class UniformWindowIndex
    {
        static readonly UniformWindow[] None = Array.Empty<UniformWindow>();
        static readonly GpuResourceLayoutElement[] NoElements = Array.Empty<GpuResourceLayoutElement>();

        // Reference identity, and neither table keeps its key alive: a renderer that retires and replaces a set on
        // regrowth would otherwise pile up here for the life of the scene.
        readonly ConditionalWeakTable<IGpuResourceLayout, GpuResourceLayoutElement[]> _layouts = new();
        readonly ConditionalWeakTable<IGpuResourceSet, UniformWindow[]> _sets = new();
        int _unknownLayout;

        /// <summary>How many sets were built against a layout this index never saw. A caller that wants to trust an
        /// empty hazard list checks this is zero first.</summary>
        internal int SetsWithUnknownLayout => _unknownLayout;

        /// <summary>Remember a layout's elements, which is where the per-slot kind and the dynamic flag live.</summary>
        internal void NoteLayout(IGpuResourceLayout layout, in GpuResourceLayoutDescription d)
        {
            if (layout is null) return;
            _layouts.AddOrUpdate(layout, d.Elements ?? NoElements);
        }

        /// <summary>Resolve a set's uniform bindings into windows and remember them.
        /// <paramref name="isUniform"/> answers whether a buffer was created with
        /// <see cref="GpuBufferUsage.UniformBuffer"/>, the only usage the native backends ring-back.</summary>
        internal void NoteSet(IGpuResourceSet set, in GpuResourceSetDescription d, Func<IGpuBuffer, bool> isUniform)
        {
            ArgumentNullException.ThrowIfNull(isUniform);
            if (set is null) return;

            GpuResourceLayoutElement[]? elements = null;
            if (d.Layout is null || !_layouts.TryGetValue(d.Layout, out elements)) _unknownLayout++;

            IGpuBindableResource[] bound = d.Resources ?? Array.Empty<IGpuBindableResource>();
            List<UniformWindow>? windows = null;
            for (int i = 0; i < bound.Length; i++)
            {
                bool dynamic = false;
                if (elements is not null)
                {
                    // Resources are in binding order, so element i describes resource i.
                    if (i >= elements.Length || elements[i].Kind != GpuResourceKind.UniformBuffer) continue;
                    dynamic = elements[i].Dynamic;
                }

                UniformWindow window;
                switch (bound[i])
                {
                    case GpuBufferRange r when r.Buffer is not null && isUniform(r.Buffer):
                        window = new UniformWindow(r.Buffer, r.Offset, r.Size, dynamic);
                        break;
                    case IGpuBuffer b when isUniform(b):
                        window = new UniformWindow(b, 0, b.SizeInBytes, dynamic);
                        break;
                    default:
                        continue;
                }

                (windows ??= new List<UniformWindow>()).Add(window);
            }

            _sets.AddOrUpdate(set, windows is null ? None : windows.ToArray());
        }

        /// <summary>The windows <paramref name="set"/> binds, unrebased. Empty for a set this index never saw and
        /// for one that binds no uniform buffer at all.</summary>
        internal IReadOnlyList<UniformWindow> WindowsOf(IGpuResourceSet set) =>
            set is not null && _sets.TryGetValue(set, out UniformWindow[]? w) ? w : None;
    }
}
