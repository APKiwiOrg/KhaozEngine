using System;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE SEAM EVERYTHING IN THIS BACKEND IS BUILT ON: one method per <see cref="IGpuCommandList"/> command,
    /// expressed in ENGINE-OWNED handle types and never in raw COM pointers. Always consumed through a generic
    /// constraint (<c>where TEmitter : struct, ID3D11Emitter</c>), never through the interface, so the JIT
    /// monomorphizes each implementation and the production path carries no indirection.
    /// <para>
    /// WHAT THE ENGINE-OWNED HANDLES BUY. Because no member names a Direct3D type, an emitter can be a plain
    /// struct with no device behind it. That is what makes <see cref="D3D11CountingEmitter"/> possible and what
    /// makes the op-encoding and replay-ordering tests device-free <c>[Fact]</c>s that run on every
    /// <c>dotnet test</c>, on macOS and Linux included.
    /// </para>
    /// <para>
    /// WHAT IT DOES NOT BUY, stated because the first version of this comment claimed it did. A tally taken at
    /// THIS seam counts SEAM calls, and decision T2's budget gates NATIVE calls, which are made inside the real
    /// emitter and fan out. One <see cref="SetGraphicsResourceSet(uint, IGpuResourceSet)"/> here becomes a
    /// <c>VSSetConstantBuffers1</c> plus a <c>PSSetShaderResources</c> plus a <c>PSSetSamplers</c> there (5.3),
    /// and section 9.4's assertions turn on a framebuffer CHANGE versus a redundant re-bind, a guard that lives
    /// inside the real emitter and is invisible from here. So a seam tally is an UPPER-BOUND INPUT and an
    /// ORDERING CHECK, not the native-call count. Where the countable native-call sink goes is row 9's decision,
    /// stated on <see cref="D3D11CountingEmitter"/> and in this package's README, and it is not built here.
    /// </para>
    /// <para>
    /// THE IMPLEMENTATION CONSTRAINT, ENFORCED BY TEST: an implementation is a READONLY struct, so all the
    /// mutable state it carries sits behind a class reference. <see cref="D3D11CommandRecorder{TEmitter}"/>
    /// stores its emitter BY VALUE, one copy per list, so under the immediate driver N lists hold N copies of
    /// the struct over ONE <c>ID3D11DeviceContext</c>. Inline mutable state would then be per-list on one driver
    /// and per-device on the other, and R6's redundancy caches describe what is bound on the CONTEXT rather than
    /// what a list recorded: two caches over one context means list B binds pipeline P, list A's cache still
    /// says P is bound, and A skips the rebind and draws with B's state. R8's precise unbind-and-scrub on
    /// disposal fails the same way, since it is reached from the device and would find only one of the copies.
    /// Both shipped implementations already have this shape, and row 6's real emitter keeps its caches and its
    /// context in a device-owned object the struct points at. The rejected alternative was a class holder the
    /// recorder mutates through, which permits a mutable struct emitter at the cost of threading a holder type
    /// through create, submit and replay, and which buys nothing here: the caches belong to the device either
    /// way, and reaching them through one dereference is what both shapes actually cost.
    /// </para>
    /// <para>
    /// THERE IS NO <c>Create*</c> MEMBER, and its absence is decision X1 rather than an omission. Every SRV, RTV,
    /// DSV, UAV and state object is created at resource, set or pipeline creation, so creating a view during
    /// replay is a COMPILE error here rather than an assertion that fires on a machine somewhere. All 25
    /// DEVICE_REMOVED stacks in the incumbent's field reports surfaced inside a view constructor reached from
    /// activation, which is the failure this shape rules out. Do not add one.
    /// </para>
    /// <para>
    /// THE OP STREAM IS ONE DRIVER OF THIS SEAM, NOT A LAYER UNDER IT (section 16). Two implementations ship
    /// today and they sit on opposite sides of that point: <see cref="D3D11StreamEmitter"/> records into a CPU
    /// command stream that is replayed at submit (decision R1), and a real emitter called straight from
    /// <see cref="D3D11CommandRecorder{TEmitter}"/> emits at record time (decision R2, the M1 fallback). Nothing
    /// here may assume a stream exists, because phase 3's Vulkan and Metal emitters have real deferred command
    /// buffers and would emit at record time straight into them, where a CPU op stream is pure overhead.
    /// </para>
    /// <para>
    /// <see cref="Begin"/> and <see cref="End"/> bracket ONE submitted list's worth of emission, and each
    /// implementation decides when that is. The stream emitter treats them as the recording scope, so
    /// <c>Begin</c> truncates the stream to zero and <c>End</c> seals it. A real emitter treats them as the
    /// native-call scope, which is where decision R3's single <c>ClearState</c> per replay belongs. Under the
    /// deferred driver the replay raises them around the stored ops, so a real emitter sees exactly one pair per
    /// submit either way.
    /// </para>
    /// </summary>
    internal interface ID3D11Emitter
    {
        /// <summary>Open the emission scope for one command list. See the type remarks for what each
        /// implementation does with it.</summary>
        void Begin();

        /// <summary>Close the emission scope opened by <see cref="Begin"/>.</summary>
        void End();

        /// <summary>Bind a framebuffer as the render target. Decision W6 makes this the ONLY place a viewport
        /// and scissor are emitted, on a framebuffer CHANGE, since the seam carries no <c>SetViewport</c>.</summary>
        void SetFramebuffer(IGpuFramebuffer framebuffer);

        /// <summary>Clear colour attachment <paramref name="index"/>.</summary>
        void ClearColorTarget(uint index, Color rgba);

        /// <summary>Clear the depth attachment.</summary>
        void ClearDepthStencil(float depth);

        /// <summary>Bind a graphics pipeline.</summary>
        void SetPipeline(IGpuPipeline pipeline);

        /// <summary>Bind a resource set with no dynamic offset.</summary>
        void SetGraphicsResourceSet(uint slot, IGpuResourceSet set);

        /// <summary>Bind a resource set whose one dynamic element is rebased by <paramref name="dynamicOffset"/>
        /// bytes. Distinct from the overload above rather than defaulted to zero, because a set bound with a
        /// dynamic offset of zero and a set bound without one are different binds.</summary>
        void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset);

        /// <summary>Bind a vertex buffer at a byte offset. The seam's no-offset overload arrives here with an
        /// offset of zero, because D3D11 takes an offset on every vertex-buffer bind anyway.</summary>
        void SetVertexBuffer(uint slot, IGpuBuffer buffer, uint offsetBytes);

        /// <summary>Bind the index buffer.</summary>
        void SetIndexBuffer(IGpuBuffer buffer, GpuIndexFormat format);

        /// <summary>Set the scissor rect for one output.</summary>
        void SetScissorRect(uint index, uint x, uint y, uint width, uint height);

        /// <summary>Reset the scissor to the full framebuffer for every output.</summary>
        void SetFullScissorRects();

        /// <summary>Non-indexed draw. The seam's single-instance overload arrives here as
        /// <c>(vertexCount, 1, 0, 0)</c>.</summary>
        void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart);

        /// <summary>Indexed, optionally instanced draw.</summary>
        void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart);

        /// <summary>Write <paramref name="data"/> into a buffer. The seam's two generic overloads both arrive
        /// here erased to bytes: a replayed write has no <c>T</c> left to be generic over, and D3D11 wants bytes
        /// either way. Under the deferred driver the span points into the recording's payload arena, so it is
        /// valid for the duration of this call and no longer.</summary>
        void UpdateBuffer(IGpuBuffer buffer, uint offsetBytes, ReadOnlySpan<byte> data);

        /// <summary>Copy bytes between two buffers.</summary>
        void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes, uint sizeInBytes);

        /// <summary>Copy a whole texture.</summary>
        void CopyTexture(IGpuTexture src, IGpuTexture dst);

        /// <summary>Copy one mip level and array layer into another. The seam's shorter overload arrives here
        /// with a destination mip and layer of zero.</summary>
        void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer,
            IGpuTexture dst, uint dstMipLevel, uint dstArrayLayer, uint width, uint height);

        /// <summary>Generate a texture's mip chain from its base level.</summary>
        void GenerateMipmaps(IGpuTexture texture);

        /// <summary>Resolve a multisampled render target into a single-sample texture.</summary>
        void ResolveTexture(IGpuTexture src, IGpuTexture dst);

        /// <summary>Bind a compute pipeline. Tracked separately from the graphics pipeline.</summary>
        void SetComputePipeline(IGpuComputePipeline pipeline);

        /// <summary>Bind a compute resource set with no dynamic offset.</summary>
        void SetComputeResourceSet(uint slot, IGpuResourceSet set);

        /// <summary>Bind a compute resource set with a dynamic offset.</summary>
        void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset);

        /// <summary>Dispatch workgroups of the bound compute pipeline.</summary>
        void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ);
    }
}
