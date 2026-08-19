using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// A SPLAT MATERIAL'S COMBINED UNIFORM BUFFER AND ITS CPU MIRROR: the frame block at offset 0, the material's
    /// own <see cref="SplatParamsData"/> appended at <c>frameBytes</c>, and one whole-buffer upload per frame.
    /// <para>
    /// WHY THE MATERIAL RETAINS ITS PARAMS. The splat pipeline binds ONE uniform buffer (a second UBO in a set
    /// mis-binds on Metal, see <c>ModelRenderer</c>), so the frame block and the per-material params share a
    /// buffer that is LARGER than the frame block. Re-syncing the frame block into it each frame was therefore a
    /// PARTIAL write, and Veldrid's <c>D3D11CommandList.UpdateBufferCore</c> sends a partial write to a non-Dynamic
    /// uniform buffer down its staging route: rent a staging buffer, hand it to
    /// <c>GraphicsDevice.UpdateBuffer</c>, which Maps the IMMEDIATE context with <c>D3D11_MAP_WRITE</c> (not
    /// WRITE_DISCARD, no DO_NOT_WAIT) and blocks until the GPU has released the buffer being recycled. Only a write
    /// covering the whole buffer from offset 0 takes the cheap <c>UpdateSubresource</c> path. Packing the frame
    /// block into a mirror whose tail already holds the params is what makes the whole write possible at all: the
    /// params are load-time data nothing else keeps, so without them the rest of the buffer could not be rebuilt.
    /// That was the prerequisite <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/408">#408</see> named.
    /// </para>
    /// <para>
    /// BYTE-IDENTICAL. The head holds the same frame bytes the partial write carried and the tail the same params
    /// the load-time write put there, so the buffer's contents at draw time are unchanged and only the number of
    /// commands that produced them moved.
    /// </para>
    /// </summary>
    internal sealed class SplatUniformBuffer : IDisposable
    {
        readonly byte[] _image;
        readonly int _frameBytes;

        internal SplatUniformBuffer(IGpuBuffer buffer, in SplatParamsData parameters, uint frameBytes)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            Buffer = buffer;
            _frameBytes = checked((int)frameBytes);
            _image = new byte[_frameBytes + (int)SplatParamsData.SizeInBytes];
            MemoryMarshal.Write(_image.AsSpan(_frameBytes), in parameters);
        }

        /// <summary>The GPU buffer itself, for the resource set the material binds.</summary>
        internal IGpuBuffer Buffer { get; }

        /// <summary>Copy this frame's packed frame block over the mirror's head and upload the WHOLE buffer.
        /// <paramref name="frameImage"/> is <c>ModelRenderer.FrameImage</c>, exactly the bytes the partial write
        /// used to carry.</summary>
        internal void Upload(IGpuCommandList cl, ReadOnlySpan<byte> frameImage)
        {
            frameImage.CopyTo(_image.AsSpan(0, _frameBytes));
            cl.UpdateBuffer(Buffer, 0, (ReadOnlySpan<byte>)_image);
        }

        public void Dispose() => Buffer.Dispose();
    }
}
