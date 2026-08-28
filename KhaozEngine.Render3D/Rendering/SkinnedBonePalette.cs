using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// THE GPU-SKINNED BONE PALETTES OF ONE FRAME, in ONE buffer, uploaded ONCE and read by every pass that draws
    /// those casters (issue <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/407">#407</see>). One
    /// 256-byte-aligned slot per CASTER, selected by a per-draw dynamic offset, exactly the slot-array shape the
    /// skinned main and skinned depth buffers already use for their own per-draw data.
    /// <para>
    /// WHY IT IS ITS OWN BUFFER. A caster's palette is the same bytes in the main pass and in every shadow cascade:
    /// only the matrix in front of it differs (<c>ViewProj</c> is per frame, <c>LightMvp</c> is per caster-cascade).
    /// While the palette rode inside those slots, a caster's 48 bones were re-packed once for the main slot and once
    /// for EACH cascade's depth slot, so a four-cascade frame paid five copies of one palette per caster. It pays one
    /// now, and the two slot buffers shrink to the matrices that really are per draw.
    /// </para>
    /// <para>
    /// ONE LAYOUT, ONE SET, TWO PIPELINES. The description is identical on both sides (a single vertex-only dynamic
    /// uniform buffer), so the same <see cref="Layout"/> object goes into both pipelines' layout arrays and the same
    /// <see cref="Set"/> is bound in both passes, at set 2 in the main pipeline and set 1 in the depth one. A set
    /// layout carries no set NUMBER: the pipeline's layout array decides that, and each backend's numbering is a
    /// per-pipeline walk of that array, so one object at two different slots is exactly as well defined as two.
    /// </para>
    /// <para>
    /// The slot is <see cref="SlotBytes"/> = 8192, which is already a multiple of 256, so no alignment round-up is
    /// spent. It is a WHOLE-buffer upload from offset 0 for the same reason its two siblings are: Direct3D 11 takes
    /// its cheap <c>UpdateSubresource</c> route only for a write covering the entire constant buffer, and a partial
    /// one falls back to a staging map that blocks (see <c>ModelRenderer.FrameUbo.cs</c>).
    /// </para>
    /// </summary>
    internal sealed class SkinnedBonePalette : IDisposable
    {
        /// <summary>One caster's palette slot: the full <see cref="SkinningMath.MaxBonesPerDraw"/> mat4 window the
        /// shader declares. 128 * 64 = 8192, a multiple of 256 already.</summary>
        internal static readonly uint SlotBytes = (uint)SkinningMath.MaxBonesPerDraw * 64;

        readonly IGpuDevice _gd;
        readonly IGpuResourceLayout _layout;      // one dynamic-offset uniform buffer, VERTEX only
        IGpuBuffer? _ubo;
        uint _slots;
        IGpuResourceSet? _set;                    // single-slot window over _ubo, rebased per draw
        // Persistent CPU image of the whole buffer, so every caster's palette is packed before ONE upload records
        // the entire destination. Carried across a grow so untouched slots keep their bytes.
        byte[] _image = Array.Empty<byte>();
        readonly List<IDisposable> _retired = new();   // grown-out buffers/sets (a prior frame may still read them)

        internal SkinnedBonePalette(IGpuDevice gd)
        {
            _gd = gd;
            _layout = gd.Factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Palette", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex, dynamic: true)));
        }

        /// <summary>The shared layout both skinned pipelines put in their layout arrays.</summary>
        internal IGpuResourceLayout Layout => _layout;

        /// <summary>The single-slot window set both passes bind, with the caster's own dynamic offset.
        /// Null until <see cref="EnsureCapacity"/> has run at least once.</summary>
        internal IGpuResourceSet Set => _set!;

        /// <summary>The dynamic offset that selects caster <paramref name="slot"/>'s palette.</summary>
        internal static uint OffsetFor(uint slot) => slot * SlotBytes;

        /// <summary>Ensure the buffer holds at least <paramref name="slotCount"/> caster slots, growing
        /// geometrically and retiring the old buffer + its set. ONE slot per caster, never per cascade.</summary>
        internal void EnsureCapacity(uint slotCount)
        {
            if (_ubo != null && _slots >= slotCount) return;
            if (_ubo != null) _retired.Add(_ubo);
            if (_set != null) _retired.Add(_set);
            _slots = Math.Max(slotCount, _slots == 0 ? 8u : _slots * 2);
            var image = new byte[checked((int)(_slots * SlotBytes))];
            _image.AsSpan().CopyTo(image);
            _image = image;
            _ubo = _gd.Factory.CreateBuffer(new GpuBufferDescription(_slots * SlotBytes, GpuBufferUsage.UniformBuffer));
            _set = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(
                _layout, new GpuBufferRange(_ubo, 0, SlotBytes)));
        }

        /// <summary>Pack one caster's composed palette (inverseBind * jointWorld) into its slot, uploaded raw so the
        /// shader reads each matrix column-major as its transpose, which is what makes the GPU blend equal
        /// <see cref="SkinningMath.SkinVertex"/>. Only the mesh's own bones are written (indices are load-validated
        /// below the mesh's bone count), so the rest of the slot keeps whatever it held.</summary>
        internal void Pack(uint slot, ReadOnlySpan<Matrix4x4> bones)
        {
            if (bones.Length == 0) return;
            MemoryMarshal.AsBytes(bones).CopyTo(
                _image.AsSpan(checked((int)(slot * SlotBytes)), checked((int)SlotBytes)));
        }

        /// <summary>Upload every packed palette in one whole-buffer write. Called once per frame, before either
        /// pass draws, so the main pass and all cascades read the same bytes.</summary>
        internal void Upload(IGpuCommandList cl) => cl.UpdateBuffer(_ubo!, 0, (ReadOnlySpan<byte>)_image);

        public void Dispose()
        {
            _ubo?.Dispose();
            _set?.Dispose();
            _layout.Dispose();
            foreach (IDisposable r in _retired) r.Dispose();
            _retired.Clear();
        }
    }
}
