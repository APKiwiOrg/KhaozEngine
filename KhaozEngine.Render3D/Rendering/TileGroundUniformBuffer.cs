using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// A TILE-GROUND MATERIAL'S COMBINED UNIFORM BUFFER AND ITS CPU MIRROR: the frame block at offset 0, the
    /// material's params tail (per-layer tint + tiling, then the misc vector) appended at <c>frameBytes</c>, and one
    /// whole-buffer upload per frame. The sibling of <see cref="SplatUniformBuffer"/>, which carries the full
    /// rationale: the pipeline binds ONE uniform buffer, so the per-frame re-sync of a block smaller than the buffer
    /// was a PARTIAL write, and Veldrid sends a partial write to a non-Dynamic uniform buffer down a staging route
    /// that blocks the calling thread on D3D11
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/408">#408</see>). Retaining the load-time params
    /// on the CPU is what lets the whole buffer be rebuilt and uploaded in one command.
    /// <para>
    /// The tail is 64 layer vectors plus the misc vector, which is over a kilobyte of it, so this is the buffer in
    /// the engine that gains the most from not writing a partial head into it every frame.
    /// </para>
    /// </summary>
    internal sealed class TileGroundUniformBuffer : IDisposable
    {
        readonly byte[] _image;
        readonly int _frameBytes;

        internal TileGroundUniformBuffer(IGpuBuffer buffer, Vector4[] parameters, uint frameBytes)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentNullException.ThrowIfNull(parameters);
            Buffer = buffer;
            _frameBytes = checked((int)frameBytes);
            _image = new byte[_frameBytes + (int)TileGroundMaterialConfig.ParamsBytes];
            MemoryMarshal.AsBytes<Vector4>(parameters).CopyTo(_image.AsSpan(_frameBytes));
        }

        /// <summary>The GPU buffer itself, for the resource set the material binds.</summary>
        internal IGpuBuffer Buffer { get; }

        /// <summary>Copy this frame's packed frame block over the mirror's head and upload the WHOLE buffer.
        /// <paramref name="frameImage"/> is <c>ModelRenderer.FrameImage</c>, exactly the bytes a partial write
        /// would have carried.</summary>
        internal void Upload(IGpuCommandList cl, ReadOnlySpan<byte> frameImage)
        {
            frameImage.CopyTo(_image.AsSpan(0, _frameBytes));
            cl.UpdateBuffer(Buffer, 0, (ReadOnlySpan<byte>)_image);
        }

        public void Dispose() => Buffer.Dispose();
    }
}
