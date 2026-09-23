using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// THE PER-PLANE UNIFORM SLOTS AS ONE CONTIGUOUS CPU IMAGE, packed during the frame's plane loop and uploaded
    /// in a SINGLE whole-buffer write. The same shape <c>ModelRenderer.FrameUbo.cs</c> gave the model frame block
    /// in 17.18.0, applied to the residual site
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/408">#408</see> names first.
    /// <para>
    /// WHY THE SHAPE MATTERS, AND ON ONE BACKEND IN PARTICULAR. A water pass used to record one
    /// <c>UpdateBuffer</c> per plane, each covering <see cref="PayloadBytes"/> at <c>i * SlotBytes</c>. Veldrid's
    /// <c>D3D11CommandList.UpdateBufferCore</c> sent a PARTIAL write to a non-Dynamic uniform buffer down the
    /// staging route: rent a staging buffer, hand it to <c>GraphicsDevice.UpdateBuffer</c>, which Mapped the
    /// IMMEDIATE context with <c>D3D11_MAP_WRITE</c> (not WRITE_DISCARD, no DO_NOT_WAIT) and therefore BLOCKED
    /// until the GPU had finished with the staging buffer being recycled. Only a write covering the whole buffer
    /// from offset 0 takes the cheap <c>UpdateSubresource</c> path. So a four-plane frame was four CPU/GPU sync
    /// points in the middle of the water encode, and it is one <c>UpdateSubresource</c> now. Metal and Vulkan have
    /// no such split and are simply issued fewer commands. The engine's own native D3D11 backend routes every
    /// uniform write through <c>D3D11UniformRing</c> and never had the stall to begin with.
    /// </para>
    /// <para>
    /// BYTE-IDENTICAL WHERE IT IS READ. A drawn plane's slot <c>i</c> holds exactly the bytes the per-plane write
    /// put there, at the same base, so every bound range renders the same picture. Three regions differ and none
    /// is ever read. The first is the <c>SlotBytes - PayloadBytes</c> pad at the tail of each slot (the shader's
    /// block ends at <see cref="PayloadBytes"/>, and the range is rounded up only because D3D11 rejects a
    /// non-multiple-of-16-constant count). The second is the slots past the frame's plane count (no draw binds an
    /// offset beyond <c>planes.Length - 1</c>). The third is a culled plane's slot: the draw loop skips it when
    /// packing and the whole-buffer write still uploads it, but <c>DrawRoutedPlanes</c> skips culled planes and
    /// never binds it. The first two used to hold whatever the GPU allocation happened to carry. All three now hold
    /// the mirror's zeros or an earlier frame's values.
    /// </para>
    /// </summary>
    internal sealed partial class WaterRenderer
    {
        // The whole UBO as CPU bytes: _capacity slots of SlotBytes each, mirroring the buffer exactly so the upload
        // below can cover it from offset 0. Grown with the buffer and never shrunk.
        byte[] _uboImage = Array.Empty<byte>();

        /// <summary>Resize the CPU mirror to match a UBO grown to <paramref name="slots"/> slots, keeping what the
        /// old one held (a drawn plane's slot is repacked before every upload, and a culled plane's slot is uploaded
        /// without a repack but never read, so the carry-over only keeps unread bytes stable rather than being
        /// load-bearing).</summary>
        void ResizeUboImage(int slots)
        {
            var image = new byte[checked(slots * (int)SlotBytes)];
            _uboImage.AsSpan().CopyTo(image);
            _uboImage = image;
        }

        /// <summary>Pack one plane's resolved uniforms into its slot of the mirror. The GPU sees nothing until
        /// <see cref="UploadSlots"/> runs, which the draw loop guarantees happens first.</summary>
        void PackSlot(int plane, in WaterUbo u) =>
            MemoryMarshal.Write(_uboImage.AsSpan(checked(plane * (int)SlotBytes), (int)SlotBytes), in u);

        /// <summary>Upload every packed slot in ONE whole-buffer write, before the first draw that binds one.</summary>
        void UploadSlots(IGpuCommandList cl) => cl.UpdateBuffer(_ubo!, 0, (ReadOnlySpan<byte>)_uboImage);
    }
}
