using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// THE PER-BEGIN VIEW-PROJECTION UBO: its slots, its CPU mirror, and the single whole-buffer write each Begin
    /// records. Split out of <c>SpriteBatch.cs</c> when the upload shape changed for
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/408">#408</see>, because the slot bookkeeping is
    /// one coherent thing rather than an arbitrary slice of the batch.
    /// </summary>
    public sealed partial class SpriteBatch
    {
        // The clip-corrected view-projection is no longer baked into every vertex on the CPU. It rides in this UBO
        // and the vertex shader multiplies it. Each Begin claims its OWN 256-byte slot (VpSlotBytes), so no slot is
        // overwritten within a frame's command list - the same distinct-slot + dynamic-offset pattern the 3D
        // dynamic-offset renderers use (OverlayMeshRenderer / GroundDecalRenderer), which is safe regardless of how
        // a backend orders mid-command-list buffer copies (overwriting one shared slot mid-list mis-binds on
        // Metal/Veldrid). cl.UpdateBuffer records the write into the command stream, so cross-frame reuse of the
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

        // THE CPU MIRROR OF THE WHOLE UBO, which is what makes each Begin's write a WHOLE-buffer write.
        //
        // A Begin used to write its own 64 bytes at its slot offset, and a PARTIAL write to a non-Dynamic uniform
        // buffer is the shape Veldrid's D3D11CommandList.UpdateBufferCore sends down its staging route: rent a
        // staging buffer, hand it to GraphicsDevice.UpdateBuffer, which Maps the IMMEDIATE context with
        // D3D11_MAP_WRITE (not WRITE_DISCARD, no DO_NOT_WAIT) and blocks until the GPU has released the buffer being
        // recycled. Only a write covering the whole buffer from offset 0 takes the cheap UpdateSubresource path, so
        // every Begin was a CPU/GPU sync point in the middle of the 2D encode (#408). Metal and Vulkan have no such
        // split, and the engine's own native D3D11 backend routes uniform writes through D3D11UniformRing and never
        // had the stall at all.
        //
        // WHY THIS STAYS ONE WRITE PER BEGIN rather than one per frame, as the model and shadow blocks became in
        // 17.18.0/17.20.0: those passes know every slot before the first draw that reads one. A batch does not. A
        // Begin's draws are recorded before the next Begin exists, so an upload deferred to the end of the frame
        // would land behind the draws that read it. Each Begin therefore re-uploads the mirror whole. That rewrites
        // the earlier slots with the bytes they already hold (a slot's mirror value never changes once its Begin has
        // claimed it, because _beginIndex only advances within a frame), so it is a no-op for anything already
        // recorded, and it costs _vpCapacity * 256 bytes of memcpy per Begin instead of a blocking Map.
        byte[] _vpImage;

        /// <summary>The byte offset into the view-projection UBO bound (via set 1's dynamic offset) for the CURRENT
        /// Begin. Advances by <see cref="ViewProjSlotBytes"/> per Begin within a frame and resets to 0 each NewFrame.
        /// For tests of the per-Begin slot bookkeeping.</summary>
        internal uint CurrentViewProjOffset => _vpDynamicOffset;

        /// <summary>The per-Begin slot stride of the view-projection UBO (the dynamic-offset alignment). For tests.</summary>
        internal int ViewProjSlotBytes => VpSlotBytes;

        /// <summary>The number of 256-byte slots the view-projection UBO currently holds, growing when a frame runs more
        /// Begins than it had capacity for. For tests of the grow-with-retire path.</summary>
        internal int ViewProjSlotCapacity => _vpCapacity;

        // Claim this Begin's own view-projection UBO slot, pack its matrix into the mirror, and upload the mirror
        // WHOLE. The slot's byte offset is bound with set 1 on every draw of this batch. _vp is already
        // clip-corrected by Begin's Clip().
        void UploadViewProj()
        {
            int slot = _beginIndex++;
            EnsureVpCapacity(slot + 1);
            _vpDynamicOffset = (uint)(slot * VpSlotBytes);
            MemoryMarshal.Write(_vpImage.AsSpan(slot * VpSlotBytes, VpSlotBytes), in _vp);
            _cl.UpdateBuffer(_vpUbo, 0, (ReadOnlySpan<byte>)_vpImage);
        }

        // Grow _vpUbo to hold at least this many 256-byte slots. A grow RETIRES the old buffer and set: earlier
        // Begins this frame already recorded draws and slot writes against them, and a prior frame's list may still
        // read them. Their slots go unused in the new buffer, and this Begin and later ones write into it. The
        // mirror keeps what it held, so the whole-buffer write that follows still carries every claimed slot.
        void EnsureVpCapacity(int slots)
        {
            if (_vpCapacity >= slots) return;
            _retire.Retire(_vpUbo, _vpSet, null);
            _vpCapacity = Math.Max(slots, _vpCapacity * 2);
            var image = new byte[checked(_vpCapacity * VpSlotBytes)];
            _vpImage.AsSpan().CopyTo(image);
            _vpImage = image;
            _vpUbo = _gd.Factory.CreateBuffer(new GpuBufferDescription((uint)(_vpCapacity * VpSlotBytes), GpuBufferUsage.UniformBuffer));
            _vpSet = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_vpLayout, new GpuBufferRange(_vpUbo, 0, VpPayloadBytes)));
        }
    }
}
