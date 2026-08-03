using System;
using System.Runtime.Versioning;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE REAL EMITTER'S RESOURCE COMMANDS: the write path of decisions U1 and U4, the copies, the mip
    /// generation, the resolve and the compute pipeline bind. Split from the state and draw half so neither file
    /// grows into the 1751-line command list the fork carries.
    /// <para>
    /// THE COMPUTE ORDERING RULES ARE NOT HERE. Decision C1's SRV-versus-UAV auto-unbind in both directions and
    /// the compute shader's own redundancy cache are work-breakdown row 12, together with the staging map path
    /// and the readback. What this half owes row 12 is the shape it will hang off, which is the same
    /// <see cref="D3D11BindFlush"/> hook the graphics side uses.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal readonly partial struct D3D11NativeEmitter
    {
        /// <summary>
        /// THE ONE PLACE THE TWO UPLOAD PATHS PART (decisions U1 and U4), and the only seam command whose
        /// destination decides the mechanism.
        /// <para>
        /// A WRITE TO A RING-BACKED UNIFORM BUFFER GOES STRAIGHT INTO THE MAPPED SEGMENT and issues nothing at
        /// all: the ring is mapped <c>NO_OVERWRITE</c> for the record phase, so the memcpy the caller already
        /// asked for IS the memcpy into GPU-visible memory. That is the 22-blocking-staging-maps-a-frame
        /// pathology gone, and it is why this branch is here as well as in
        /// <see cref="D3D11StreamEmitter.UpdateBuffer"/>: under <c>KE_D3D11_RECORD=immediate</c> this type IS the
        /// record-time emitter, so the routing has to be the same on both drivers or the two would disagree
        /// about where a uniform lands.
        /// </para>
        /// <para>
        /// EVERYTHING ELSE IS <c>UpdateSubresource</c> WITH A BOX, which is the partial-write form Direct3D 11
        /// permits on a non-constant buffer. Under the deferred driver the span points into the recording's
        /// payload arena and is valid for the duration of this call and no longer, which is exactly the lifetime
        /// this copy needs.
        /// </para>
        /// </summary>
        public void UpdateBuffer(IGpuBuffer buffer, uint offsetBytes, ReadOnlySpan<byte> data)
        {
            if (buffer is null) throw new ArgumentNullException(nameof(buffer));
            if (buffer is ID3D11RingBacked { Ring: { } ring })
            {
                ring.Write(offsetBytes, data);
                return;
            }

            // An empty write is a no-op rather than a zero-width box, which the runtime rejects.
            if (data.IsEmpty) return;

            var box = new Box((int)offsetBytes, 0, 0, (int)(offsetBytes + (uint)data.Length), 1, 1);
            Native.UpdateSubresource(data, NativeBuffer(buffer), 0, 0, 0, box);
        }

        /// <summary>Copy bytes between two buffers, as the partial-region copy both offsets make it.</summary>
        public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes,
            uint sizeInBytes)
        {
            if (sizeInBytes == 0) return;

            var box = new Box((int)srcOffsetBytes, 0, 0, (int)(srcOffsetBytes + sizeInBytes), 1, 1);
            Native.CopySubresourceRegion(NativeBuffer(dst), 0, (int)dstOffsetBytes, 0, 0, NativeBuffer(src), 0,
                box);
        }

        /// <summary>Copy a whole texture, every mip and every layer.</summary>
        public void CopyTexture(IGpuTexture src, IGpuTexture dst)
            => Native.CopyResource(NativeTexture(dst).DeviceTexture, NativeTexture(src).DeviceTexture);

        /// <summary>
        /// Copy one mip level and array layer into another, at the origin of both. The seam carries no source or
        /// destination offset, so the box is the whole requested extent from (0, 0) and the destination lands at
        /// (0, 0).
        /// </summary>
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer,
            IGpuTexture dst, uint dstMipLevel, uint dstArrayLayer, uint width, uint height)
        {
            if (width == 0 || height == 0) return;

            D3D11Texture source = NativeTexture(src);
            D3D11Texture destination = NativeTexture(dst);
            var box = new Box(0, 0, 0, (int)width, (int)height, 1);

            Native.CopySubresourceRegion(
                destination.DeviceTexture, Subresource(destination, dstMipLevel, dstArrayLayer), 0, 0, 0,
                source.DeviceTexture, Subresource(source, srcMipLevel, srcArrayLayer), box);
        }

        /// <summary>Generate a texture's mip chain from its base level, through the full-chain shader resource
        /// view its declared usage earned it.</summary>
        public void GenerateMipmaps(IGpuTexture texture)
        {
            D3D11Texture source = NativeTexture(texture);
            ID3D11ShaderResourceView view = source.ShaderResourceView ?? throw new ArgumentException(
                "GenerateMipmaps needs a shader resource view over the full mip chain, and this texture was not "
                + "created with GpuTextureUsage.Sampled or GpuTextureUsage.GenerateMipmaps, so it never got one. "
                + "Views follow from the declared usage at creation (decision X1).", nameof(texture));

            Native.GenerateMips(view);
        }

        /// <summary>Resolve a multisampled render target into a single-sample texture, at subresource 0 on both
        /// sides, which is the whole of what the seam can express (decision C4).</summary>
        public void ResolveTexture(IGpuTexture src, IGpuTexture dst)
        {
            D3D11Texture source = NativeTexture(src);
            D3D11Texture destination = NativeTexture(dst);

            Native.ResolveSubresource(destination.DeviceTexture, 0, source.DeviceTexture, 0,
                destination.DxgiFormat);
        }

        /// <summary>
        /// Bind a compute pipeline. The shader is bound UNGUARDED, which is deliberate rather than an omission: a
        /// compute pipeline is one shader, and its redundancy cache belongs with the rest of decision C1's
        /// compute schedule in work-breakdown row 12, so caching it here alone would be half a rule.
        /// <para>
        /// The pipeline-switch DRAIN is not half a rule and happens here, on the compute dirty array, for the
        /// same reason as the graphics one: a compute set's registers are numbered under its pipeline's layouts.
        /// </para>
        /// </summary>
        public void SetComputePipeline(IGpuComputePipeline pipeline)
        {
            if (pipeline is null) throw new ArgumentNullException(nameof(pipeline));
            if (pipeline is not ID3D11ComputePipelineState state)
                throw new ArgumentException(
                    $"A {pipeline.GetType().Name} reached the native Direct3D 11 emitter as a compute pipeline. A "
                    + "pipeline this backend created answers ID3D11ComputePipelineState, which is where the "
                    + "compiled compute shader comes from.", nameof(pipeline));

            D3D11NativeEmitter sink = this;
            _state.Binds.SetComputePipeline(ref sink, pipeline);

            ID3D11ComputeShader shader = state.ComputeShader as ID3D11ComputeShader ?? throw new ArgumentException(
                "A compute pipeline reached the native Direct3D 11 emitter with no compiled compute shader.",
                nameof(pipeline));
            Native.CSSetShader(shader);
        }

        /// <summary>RECORDED, NOT EMITTED (decision R5, rule 1), on the compute side. The next dispatch pays
        /// it.</summary>
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set)
            => _state.Binds.RecordGraphics(slot, set, 0u, hasDynamicOffset: false);

        /// <inheritdoc cref="SetGraphicsResourceSet(uint, IGpuResourceSet)"/>
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
            => _state.Binds.RecordGraphics(slot, set, dynamicOffset, hasDynamicOffset: true);

        /// <inheritdoc cref="SetGraphicsResourceSet(uint, IGpuResourceSet)"/>
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set)
            => _state.Binds.RecordCompute(slot, set, 0u, hasDynamicOffset: false);

        /// <inheritdoc cref="SetGraphicsResourceSet(uint, IGpuResourceSet)"/>
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
            => _state.Binds.RecordCompute(slot, set, dynamicOffset, hasDynamicOffset: true);

        // D3D11CalcSubresource: the mip index plus the layer's stride through the chain. A cubemap counts six
        // slices per cube, which ArrayLayers already reports as cubes, so the caller passes the SLICE it means.
        static int Subresource(D3D11Texture texture, uint mipLevel, uint arrayLayer)
            => (int)(mipLevel + (arrayLayer * texture.MipLevels));

        // The native buffer behind an engine handle: the shared resolve refuses anything else by name, and this is
        // the cast it deliberately does not make. Shared by the input assembler, the copies and the bulk write
        // path, so "a buffer from another backend" is one message and one device-free test.
        static ID3D11Buffer NativeBuffer(IGpuBuffer buffer) => (ID3D11Buffer)D3D11BindResolve.NativeBuffer(buffer);

        static D3D11Texture NativeTexture(IGpuTexture texture)
            => texture as D3D11Texture
                ?? throw new ArgumentException(
                    $"A {(texture is null ? "null" : texture.GetType().Name)} was handed to the native Direct3D 11 "
                    + "emitter as a texture. A texture this backend created carries the ID3D11Texture2D and the "
                    + "eager views a copy, a resolve and a mip generation name.", nameof(texture));
    }
}
