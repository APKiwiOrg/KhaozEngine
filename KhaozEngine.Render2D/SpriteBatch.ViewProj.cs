using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// THE PER-BEGIN VIEW-PROJECTION UBO: its slots, and the disjoint matrix write each Begin records. Split out of <c>SpriteBatch.cs</c> when the upload shape changed for
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/408">#408</see>, because the slot bookkeeping is
    /// one coherent thing rather than an arbitrary slice of the batch.
    /// </summary>
    public sealed partial class SpriteBatch
    {
        // The clip-corrected view-projection is no longer baked into every vertex on the CPU. It rides in this UBO
        // and the vertex shader multiplies it. Each Begin claims its OWN 256-byte slot (VpSlotBytes), so no slot is
        // overwritten within a frame's command list - the same distinct-slot + dynamic-offset pattern the 3D
        // dynamic-offset renderers use (OverlayMeshRenderer / GroundDecalRenderer), which is safe regardless of how
        // a backend orders mid-command-list buffer copies (overwriting one shared slot mid-list mis-bound on
        // the Veldrid Metal leg). cl.UpdateBuffer records the write into the command stream, so cross-frame reuse of the
        // same slots is safe too (each frame's list runs to completion before the next) - no ring is needed here,
        // unlike the vertex buffers (gd.UpdateBuffer, an off-timeline write). _beginIndex resets each NewFrame,
        // _vpUbo grows on demand.
        const uint VpPayloadBytes = 64;   // one Matrix4x4
        const int VpSlotBytes = 256;      // Metal/D3D11/Vulkan-safe dynamic-offset alignment (one matrix per slot)
        IGpuBuffer _vpUbo;
        IGpuResourceSet _vpSet;           // binds the VpPayloadBytes window of _vpUbo at offset 0, per-Begin offset supplied at draw time
        int _vpCapacity;                  // slots in _vpUbo
        int _beginIndex;                  // Begins claimed this frame (reset by NewFrame). The current one's slot is _beginIndex-1
        uint _vpDynamicOffset;            // byte offset of the current Begin's slot, bound with set 1 on every draw

        // Each Begin writes only its own slot. Whole-mirror rewrites worked around a retired Veldrid D3D11
        // staging stall (#408). Every shipped native backend now supports partial uniform writes without that
        // stall, and rewriting an earlier slot would race an early-committed Metal prefix even if bytes match.

        /// <summary>The byte offset into the view-projection UBO bound (via set 1's dynamic offset) for the CURRENT
        /// Begin. Advances by <see cref="ViewProjSlotBytes"/> per Begin within a frame and resets to 0 each NewFrame.
        /// For tests of the per-Begin slot bookkeeping.</summary>
        internal uint CurrentViewProjOffset => _vpDynamicOffset;

        /// <summary>The per-Begin slot stride of the view-projection UBO (the dynamic-offset alignment). For tests.</summary>
        internal int ViewProjSlotBytes => VpSlotBytes;

        /// <summary>The number of 256-byte slots the view-projection UBO currently holds, growing when a frame runs more
        /// Begins than it had capacity for. For tests of the grow-with-retire path.</summary>
        internal int ViewProjSlotCapacity => _vpCapacity;

        // Claim a fresh slot before recording any draw that reads it. _vp is already clip-corrected.
        void UploadViewProj()
        {
            int slot = _beginIndex++;
            EnsureVpCapacity(slot + 1);
            _vpDynamicOffset = (uint)(slot * VpSlotBytes);
            _cl.UpdateBuffer(_vpUbo, _vpDynamicOffset, in _vp);
        }

        // Grow _vpUbo to hold at least this many 256-byte slots. A grow RETIRES the old buffer and set: earlier
        // Begins this frame already recorded draws and slot writes against them, and a prior frame's list may still
        // read them. Their slots go unused in the new buffer, and this Begin and later ones write into it. The
        // new buffer needs only this Begin's slot, since earlier draws retain their original buffer and set.
        void EnsureVpCapacity(int slots)
        {
            if (_vpCapacity >= slots) return;
            _retire.Retire(_vpUbo, _vpSet, null);
            _vpCapacity = Math.Max(slots, _vpCapacity * 2);
            _vpUbo = _gd.Factory.CreateBuffer(new GpuBufferDescription((uint)(_vpCapacity * VpSlotBytes), GpuBufferUsage.UniformBuffer));
            _vpSet = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_vpLayout, new GpuBufferRange(_vpUbo, 0, VpPayloadBytes)));
        }
    }
}
