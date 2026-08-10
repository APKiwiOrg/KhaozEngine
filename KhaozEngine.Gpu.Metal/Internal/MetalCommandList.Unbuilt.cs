using System;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE LEDGER OF SEAM MEMBERS ROW 7 DOES NOT OWN, each one naming the row that builds it.
    ///
    /// <para><b>IT IS A LEDGER, AND A STALE ONE IS WORSE THAN NONE</b>, which is the discipline the device is
    /// already under: a row that fills a member deletes its entry here and the file goes away when the last one
    /// does. The Vulkan sibling's row 7 landed exactly this shape and its row 15 removed the last of it.</para>
    ///
    /// <para><b>WHY A SEPARATE FILE.</b> The lifecycle in <c>MetalCommandList.cs</c> is the part that can be
    /// WRONG, and it is the part every later row reads before adding to it. Twenty refusing members in front of
    /// it would bury it, and the KESIZE ratchet is the mechanical half of the same point: the design's own
    /// warning for this phase is that the incumbent's <c>MTLCommandList.cs</c> is 1163 lines against an 800-line
    /// cap, so the split is made at the START rather than reached for later, which is the failure the ratchet
    /// exists to prevent.</para>
    ///
    /// <para><b>THE MESSAGE NAMES WHAT IS LIVE AS WELL AS WHAT IS NOT</b>, in the shape all three backends
    /// settled on: a reader who hits one needs to know whether the backend is unfinished or their machine is
    /// wrong, and those have different answers.</para>
    /// </summary>
    internal sealed partial class MetalCommandList
    {
        // ---- Row 12: passes, clears, viewport and scissor ---------------------------------------------------

        /// <inheritdoc/>
        public void SetFramebuffer(IGpuFramebuffer fb) => throw NotBuiltYet("Binding a framebuffer", PassesRow);

        /// <inheritdoc/>
        public void ClearColorTarget(uint index, Color rgba)
            => throw NotBuiltYet("Clearing a colour target", PassesRow);

        /// <inheritdoc/>
        public void ClearDepthStencil(float depth)
            => throw NotBuiltYet("Clearing the depth attachment", PassesRow);

        /// <inheritdoc/>
        public void SetScissorRect(uint index, uint x, uint y, uint w, uint h)
            => throw NotBuiltYet("Setting a scissor rect", PassesRow);

        /// <inheritdoc/>
        public void SetFullScissorRects() => throw NotBuiltYet("Resetting the scissor rects", PassesRow);

        // ---- Row 13: the bind flush --------------------------------------------------------------------------

        /// <inheritdoc/>
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set)
            => throw NotBuiltYet("Binding a graphics resource set", BindsRow);

        /// <inheritdoc/>
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
            => throw NotBuiltYet("Binding a graphics resource set", BindsRow);

        /// <inheritdoc/>
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set)
            => throw NotBuiltYet("Binding a compute resource set", BindsRow);

        /// <inheritdoc/>
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
            => throw NotBuiltYet("Binding a compute resource set", BindsRow);

        // ---- Row 14: draws, dispatches and transfers ---------------------------------------------------------

        /// <inheritdoc/>
        public void SetVertexBuffer(uint slot, IGpuBuffer b)
            => throw NotBuiltYet("Binding a vertex buffer", DrawsRow);

        /// <inheritdoc/>
        public void SetVertexBuffer(uint slot, IGpuBuffer b, uint offsetBytes)
            => throw NotBuiltYet("Binding a vertex buffer", DrawsRow);

        /// <inheritdoc/>
        public void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt)
            => throw NotBuiltYet("Binding an index buffer", DrawsRow);

        /// <inheritdoc/>
        public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
            => throw NotBuiltYet("Drawing", DrawsRow);

        /// <inheritdoc/>
        public void Draw(uint vertexCount) => throw NotBuiltYet("Drawing", DrawsRow);

        /// <inheritdoc/>
        public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset,
            uint instanceStart)
            => throw NotBuiltYet("Drawing indexed", DrawsRow);

        /// <inheritdoc/>
        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
            => throw NotBuiltYet("Dispatching compute work", DrawsRow);

        /// <inheritdoc/>
        public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes,
            uint sizeInBytes)
            => throw NotBuiltYet("Copying between buffers", DrawsRow);

        /// <inheritdoc/>
        public void CopyTexture(IGpuTexture src, IGpuTexture dst)
            => throw NotBuiltYet("Copying a texture", DrawsRow);

        /// <inheritdoc/>
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst,
            uint width, uint height)
            => throw NotBuiltYet("Copying a texture subresource", DrawsRow);

        /// <inheritdoc/>
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst,
            uint dstMipLevel, uint dstArrayLayer, uint width, uint height)
            => throw NotBuiltYet("Copying a texture subresource", DrawsRow);

        /// <inheritdoc/>
        public void GenerateMipmaps(IGpuTexture texture)
            => throw NotBuiltYet("Generating a mip chain", DrawsRow);

        /// <inheritdoc/>
        public void ResolveTexture(IGpuTexture src, IGpuTexture dst)
            => throw NotBuiltYet("Resolving a multisampled texture", DrawsRow);

        const string PassesRow = "the render-passes row (https://github.com/APKiwiOrg/KhaozEngine/issues/578)";
        const string BindsRow = "the bind-flush row (https://github.com/APKiwiOrg/KhaozEngine/issues/579)";
        const string DrawsRow = "the draw-and-dispatch row (https://github.com/APKiwiOrg/KhaozEngine/issues/580)";

        static NotSupportedException NotBuiltYet(string what, string row)
            => new($"{what} is not built yet on the native Metal command list: it lands in {row}. The command "
                + "buffer per Begin, the encoder lifecycle with its one-encoder-at-a-time rule and its M-R4 "
                + "invalidation, End, disposal and the submit path ARE live (work-breakdown row 7, "
                + "https://github.com/APKiwiOrg/KhaozEngine/issues/573), and so is UpdateBuffer with the uniform "
                + "ring behind it and the staging arena behind everything else (row 8, "
                + "https://github.com/APKiwiOrg/KhaozEngine/issues/574), and binding a graphics or a compute "
                + "pipeline (row 11, https://github.com/APKiwiOrg/KhaozEngine/issues/577). This is a statement "
                + "about the package and not about this machine. Select GpuBackendKind.Metal, which goes through "
                + "Veldrid, for a fully working Metal device.");
    }
}
