using System;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gpu
{
    /// <summary>A CPU-mapped view of a staging resource for readback. Engine mirror of Veldrid
    /// <c>MappedResource</c>: the base pointer, the row pitch (bytes per row, may exceed Width*bpp), and the
    /// total mapped size.</summary>
    public readonly struct MappedData
    {
        /// <summary>Base pointer of the mapped region.</summary>
        public IntPtr Data { get; }
        /// <summary>Bytes between consecutive rows (may be padded beyond the logical row width).</summary>
        public uint RowPitch { get; }
        /// <summary>Total mapped size in bytes.</summary>
        public uint SizeInBytes { get; }

        public MappedData(IntPtr data, uint rowPitch, uint sizeInBytes)
        {
            Data = data; RowPitch = rowPitch; SizeInBytes = sizeInBytes;
        }
    }

    /// <summary>Marker for anything bindable into a <see cref="GpuResourceSetDescription"/>:
    /// <see cref="IGpuBuffer"/>, <see cref="IGpuTexture"/>, or <see cref="IGpuSampler"/>.</summary>
    public interface IGpuBindableResource { }

    /// <summary>A GPU buffer handle (vertex / index / uniform). Engine wrapper over Veldrid <c>DeviceBuffer</c>.</summary>
    public interface IGpuBuffer : IGpuBindableResource, IDisposable
    {
        /// <summary>Buffer size in bytes.</summary>
        uint SizeInBytes { get; }
    }

    /// <summary>A GPU texture handle. Engine wrapper over Veldrid <c>Texture</c>; exposes its dimensions and
    /// format for pipeline / framebuffer reasoning.</summary>
    public interface IGpuTexture : IGpuBindableResource, IDisposable
    {
        /// <summary>Texel width.</summary>
        uint Width { get; }
        /// <summary>Texel height.</summary>
        uint Height { get; }
        /// <summary>Mip-level count (1 == level 0 only, no mip chain).</summary>
        uint MipLevels { get; }
        /// <summary>MSAA sample count (1 == single-sample). &gt; 1 is a multisampled render target that must be
        /// resolved (<see cref="IGpuCommandList.ResolveTexture"/>) into a single-sample texture before sampling.</summary>
        uint SampleCount { get; }
        /// <summary>Pixel format.</summary>
        GpuPixelFormat Format { get; }
    }

    /// <summary>A GPU sampler handle. Engine wrapper over Veldrid <c>Sampler</c>.</summary>
    public interface IGpuSampler : IGpuBindableResource, IDisposable { }

    /// <summary>A GPU completion fence: the signal a submission raises once the GPU has finished executing it.
    /// Engine wrapper over Veldrid <c>Fence</c>. Created through <see cref="IGpuResourceFactory.CreateFence"/>,
    /// handed to <see cref="IGpuDevice.Submit(IGpuCommandList,IGpuFence)"/>, then POLLED through
    /// <see cref="Signaled"/>. There is deliberately no blocking wait on this seam: a caller that wants to block
    /// already has <see cref="IGpuDevice.WaitForIdle"/>, and the whole reason the fence exists is to replace a
    /// block with a poll.
    /// <para>Only meaningful on a device whose <see cref="GpuCapabilities.SupportsCompletionFences"/> is true.
    /// Read that flag before creating one: two of the four backends cannot signal on GPU completion, and the
    /// factory throws rather than hand back a fence that would lie.</para></summary>
    public interface IGpuFence : IDisposable
    {
        /// <summary>True once every command in the submission this fence was handed to has completed on the GPU.
        /// Non-blocking: it polls and returns, it never waits. Reads true once the owning device has been
        /// destroyed (a dead device has no outstanding work left to finish), mirroring the same no-op
        /// <see cref="IGpuDevice.WaitForIdle"/> becomes after device disposal.</summary>
        bool Signaled { get; }

        /// <summary>Return this fence to the unsignaled state so it can be submitted again. A fence must be
        /// unsignaled when it is submitted, so recycling one instead of creating a new one per submission goes
        /// through here.</summary>
        void Reset();
    }

    /// <summary>A render-target framebuffer handle. Engine wrapper over Veldrid <c>Framebuffer</c>; exposes its
    /// <see cref="GpuOutputDescription"/> so a matching pipeline can be created.</summary>
    public interface IGpuFramebuffer : IDisposable
    {
        /// <summary>The attachment formats of this framebuffer (for pipeline <c>Outputs</c>).</summary>
        GpuOutputDescription Outputs { get; }
        /// <summary>Framebuffer width in pixels.</summary>
        uint Width { get; }
        /// <summary>Framebuffer height in pixels.</summary>
        uint Height { get; }
    }

    /// <summary>A graphics pipeline handle. Engine wrapper over Veldrid <c>Pipeline</c>.</summary>
    public interface IGpuPipeline : IDisposable { }

    /// <summary>A compute pipeline handle. Engine wrapper over the Veldrid <c>Pipeline</c> a
    /// <c>ComputePipelineDescription</c> produces. A distinct type from <see cref="IGpuPipeline"/> on purpose:
    /// Veldrid has one <c>Pipeline</c> type and one <c>SetPipeline</c> for both kinds, so binding a compute
    /// pipeline for a draw is a runtime error there and a compile error here.</summary>
    public interface IGpuComputePipeline : IDisposable { }

    /// <summary>A resource-layout handle (binding-slot shape). Engine wrapper over Veldrid <c>ResourceLayout</c>.</summary>
    public interface IGpuResourceLayout : IDisposable { }

    /// <summary>A bound resource set handle. Engine wrapper over Veldrid <c>ResourceSet</c>.</summary>
    public interface IGpuResourceSet : IDisposable { }

    /// <summary>A compiled shader set (vertex + fragment) handle. Engine wrapper over the Veldrid
    /// <c>Shader[]</c> a SPIR-V cross-compile produces.</summary>
    public interface IGpuShaderSet : IDisposable { }

    /// <summary>A compiled compute shader handle (the single-stage sibling of <see cref="IGpuShaderSet"/>).
    /// Engine wrapper over the Veldrid <c>Shader</c> a single-stage SPIR-V cross-compile produces, plus the
    /// workgroup size read out of the module itself.</summary>
    public interface IGpuComputeShader : IDisposable
    {
        /// <summary>Workgroup size on X, read from the shader's own <c>layout(local_size_x = ...)</c>. Cover N
        /// threads with <c>(N + ThreadGroupSizeX - 1) / ThreadGroupSizeX</c> groups in
        /// <see cref="IGpuCommandList.Dispatch"/>.</summary>
        uint ThreadGroupSizeX { get; }
        /// <summary>Workgroup size on Y (1 unless the shader declares <c>local_size_y</c>).</summary>
        uint ThreadGroupSizeY { get; }
        /// <summary>Workgroup size on Z (1 unless the shader declares <c>local_size_z</c>).</summary>
        uint ThreadGroupSizeZ { get; }
    }

    /// <summary>Creates GPU resources. Engine mirror of Veldrid <c>ResourceFactory</c> (the subset used).</summary>
    public interface IGpuResourceFactory
    {
        /// <summary>Create a buffer.</summary>
        IGpuBuffer CreateBuffer(in GpuBufferDescription d);
        /// <summary>Create a 2D texture.</summary>
        IGpuTexture CreateTexture(in GpuTextureDescription d);
        /// <summary>Create a framebuffer over an optional depth + colour textures.</summary>
        IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour);
        /// <summary>Create a sampler.</summary>
        IGpuSampler CreateSampler(in GpuSamplerDescription d);
        /// <summary>Create a resource layout.</summary>
        IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d);
        /// <summary>Create a resource set.</summary>
        IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d);
        /// <summary>Cross-compile GLSL 450 SPIR-V vertex + fragment sources (entry point <c>main</c>) into a
        /// backend shader set. Every backend compiles through the shared <c>SpirvFrontEnd</c>, and through
        /// <c>SpirvCrossCompile</c> where its API wants HLSL or MSL (until 18.0.0 the incumbent wrapped
        /// <c>Veldrid.SPIRV.CreateFromSpirv</c> instead).</summary>
        IGpuShaderSet CreateShadersFromSpirv(string vertGlsl, string fragGlsl);
        /// <summary>Cross-compile a GLSL 450 SPIR-V COMPUTE source (entry point <c>main</c>) into a backend compute
        /// shader, by the same front end and cross-compile path as the graphics pair. The workgroup size is read
        /// back off the compiled module and surfaced on <see cref="IGpuComputeShader.ThreadGroupSizeX"/>, which is
        /// also what the compute pipeline is built with, so there is no second copy to keep in sync.
        /// <para>DECLARE <c>layout(local_size_x = N) in;</c> in the source. Omitting it is not an error: GLSL's
        /// default workgroup size is 1x1x1 and that is what gets compiled in, so a dispatch runs ONE invocation per
        /// group and the shader is silently a few hundred times slower than intended rather than broken. Nothing
        /// can catch that for you, because a deliberate 1x1x1 is legal.</para>
        /// Validate the source device-free first with <see cref="ShaderValidation.ValidateCompute"/>. Throws
        /// <see cref="NotSupportedException"/> on a device whose <see cref="GpuCapabilities.SupportsCompute"/> is
        /// false, so a caller that forgot to gate fails loudly instead of at dispatch, and
        /// <see cref="ShaderValidationException"/> when the source does not compile.</summary>
        IGpuComputeShader CreateComputeShaderFromSpirv(string computeGlsl);
        /// <summary>Create a graphics pipeline.</summary>
        IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d);
        /// <summary>Create a compute pipeline. Throws <see cref="NotSupportedException"/> when the device does not
        /// support compute (see <see cref="GpuCapabilities.SupportsCompute"/>).</summary>
        IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d);
        /// <summary>Create a command list.</summary>
        IGpuCommandList CreateCommandList();
        /// <summary>Create an UNSIGNALED completion fence for
        /// <see cref="IGpuDevice.Submit(IGpuCommandList,IGpuFence)"/>. Throws <see cref="NotSupportedException"/> on
        /// a device whose <see cref="GpuCapabilities.SupportsCompletionFences"/> is false, the same way
        /// <see cref="CreateComputePipeline"/> throws without compute: a fence that signals on something other than
        /// GPU completion is worse than no fence, because the caller freeing resources behind it cannot tell.</summary>
        IGpuFence CreateFence();
    }

    /// <summary>Records GPU commands for one submission. Engine mirror of Veldrid <c>CommandList</c>.</summary>
    public interface IGpuCommandList : IDisposable
    {
        /// <summary>
        /// Begin recording. Resets the list, so everything recorded into it before is discarded and a list is
        /// reusable frame after frame.
        /// <para>
        /// THE PORTABLE CONTRACT IS ONE OPEN RECORDING PER DEVICE. Between a <see cref="Begin"/> and its
        /// <see cref="End"/>, do not open a second list on the same device, and do not call any engine API that
        /// opens one of its own. Backends do not agree on what a second recording means: the Veldrid Direct3D11
        /// leg rejects it outright, and with Direct3D11 in immediate-context mode a command list IS the device's
        /// immediate context, so opening one resets the state the first list already recorded. Work that needs a
        /// list of its own belongs in the frame's pre-record phase, before the frame's list is opened.
        /// </para>
        /// <para>
        /// THAT CONTRACT IS ENFORCED, not merely stated, for every recording the engine opens.
        /// <see cref="GpuRecording.Open"/> is the seam's open-recording register and the way the engine's own
        /// hosts, renderers and helpers begin a list. A second one on the same device is refused there with a
        /// <see cref="GpuNestedRecordingException"/> naming both the open recording and the refused one, on
        /// every backend and with no GPU involved, which is what turns a backend-dependent silent corruption
        /// into one readable sentence (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/424">#424</see>).
        /// Calling this method directly is still legal and still bound by the contract above. It is simply not
        /// watched, so a consumer opening a list of its own gets whatever the backend below does.
        /// </para>
        /// <para>
        /// A BACKEND MAY BE MORE PERMISSIVE, AND THAT IS NOT PART OF THIS CONTRACT. The engine's own native
        /// Direct3D11 backend is more permissive on its DEFAULT driver, which records into an engine-owned
        /// command stream and touches no device state here, so N lists may record concurrently there and submit
        /// order is the observable order. That holds for one of its two drivers rather than for the backend:
        /// under <c>KE_D3D11_RECORD=immediate</c> the same backend emits as it records, so this method clears
        /// the device state when it is called, record order is the observable order, and a second concurrent
        /// recording wipes what the first already emitted. Concurrent recording is therefore not tolerated
        /// there either. Neither shape is a promise of this interface, code written against either does not
        /// port, and the one-open-recording rule above is the only thing to rely on.
        /// </para>
        /// <para>
        /// THE ENGINE'S OWN NATIVE VULKAN BACKEND IS PERMISSIVE FOR A REASON WORTH KNOWING, because it is the
        /// reason it holds rather than a second happy accident. N lists there record concurrently and genuinely:
        /// each list owns its own <c>VkCommandPool</c>s, which is the externally-synchronised object Vulkan's own
        /// threading model asks a caller to keep per thread, and image-layout tracking is LIST-LOCAL against a
        /// canonical resting layout, so nothing shared is read or written during recording at all. That is the
        /// same property the Direct3D 11 stream buys by touching no device state, obtained here from the API plus
        /// the barrier design instead. It is still a backend property and still not a promise of this interface:
        /// the same code on either Veldrid backend, on the Veldrid Metal backend, or on the immediate Direct3D 11
        /// driver is a half-recorded frame or a corrupted one, and a machine that falls back after a failed device
        /// creation swaps the backend under the code without telling it.
        /// </para>
        /// <para>
        /// THE ENGINE'S OWN NATIVE METAL BACKEND IS PERMISSIVE TOO, AND IT COSTS THAT BACKEND NOTHING TO BE.
        /// Each list holds its own <c>MTLCommandBuffer</c> and its own encoders, which is the allocation the queue
        /// hands out per recording rather than anything shared, and that backend keeps no record-time state
        /// outside the list at all: no layout tracker, no barrier batch, no device state cache. So N lists there
        /// record concurrently and submit order is the observable order, because the commits are serialised. That
        /// is the same shape the native Vulkan backend has, reached from Metal's object model instead of from a
        /// barrier design, and it is a BACKEND PROPERTY with all the same caveats: <c>GpuBackendKind.Metal</c>,
        /// which is the Veldrid Metal backend and the one a Mac falls back to, does not have it.
        /// </para>
        /// </summary>
        void Begin();
        /// <summary>Finish recording, sealing the list for submission. A list submitted without this is a
        /// half-recorded frame, and a backend is free to refuse it. See <see cref="Begin"/> for how many
        /// recordings may be open at once.</summary>
        void End();
        /// <summary>Bind a framebuffer as the render target.</summary>
        void SetFramebuffer(IGpuFramebuffer fb);
        /// <summary>Clear colour attachment <paramref name="index"/> to <paramref name="rgba"/>.</summary>
        void ClearColorTarget(uint index, Color rgba);
        /// <summary>Clear the depth attachment.</summary>
        void ClearDepthStencil(float depth);
        /// <summary>Bind a graphics pipeline.</summary>
        void SetPipeline(IGpuPipeline p);
        /// <summary>Bind a resource set to graphics slot <paramref name="slot"/>.</summary>
        void SetGraphicsResourceSet(uint slot, IGpuResourceSet set);
        /// <summary>Bind a resource set whose dynamic-offset buffer binding is rebased by <paramref name="dynamicOffset"/>
        /// bytes for this draw. The set must have exactly one element declared dynamic (see
        /// <see cref="GpuResourceLayoutElement.Dynamic"/>); the offset must satisfy the backend's uniform-buffer
        /// offset alignment (256 bytes is safe across Metal/D3D11/Vulkan).</summary>
        void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset);
        /// <summary>Bind a vertex buffer to slot <paramref name="slot"/>.</summary>
        void SetVertexBuffer(uint slot, IGpuBuffer b);
        /// <summary>Bind a vertex buffer to slot <paramref name="slot"/> starting at <paramref name="offsetBytes"/>
        /// into the buffer, so a draw reads its slice of a shared buffer as if from the buffer's start.</summary>
        void SetVertexBuffer(uint slot, IGpuBuffer b, uint offsetBytes);
        /// <summary>Bind the index buffer with element format <paramref name="fmt"/>.</summary>
        void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt);
        /// <summary>Set scissor rect for output <paramref name="index"/>.</summary>
        void SetScissorRect(uint index, uint x, uint y, uint w, uint h);
        /// <summary>Reset scissor to the full framebuffer for all outputs.</summary>
        void SetFullScissorRects();
        /// <summary>Non-indexed draw. The fullscreen passes call <c>Draw(3, 1, 0, 0)</c>.</summary>
        void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart);
        /// <summary>Non-indexed draw of a single instance (convenience; the fullscreen-triangle passes use this).</summary>
        void Draw(uint vertexCount);
        /// <summary>Indexed (optionally instanced) draw.</summary>
        void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart);
        /// <summary>Upload a single unmanaged struct into a buffer at <paramref name="offsetBytes"/>.</summary>
        void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged;
        /// <summary>Upload a span of unmanaged elements into a buffer at <paramref name="offsetBytes"/>.</summary>
        void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged;
        /// <summary>Copy <paramref name="sizeInBytes"/> bytes between two buffers (e.g. a compute-written storage
        /// buffer -> a <see cref="GpuBufferUsage.Staging"/> buffer for readback). The counterpart of
        /// <see cref="CopyTexture"/> for buffers, and <see cref="GpuReadback.ReadBuffer{T}"/> wraps the whole
        /// staging-copy-map-unmap dance.
        /// <para>
        /// <b>BOTH OFFSETS MUST BE MULTIPLES OF FOUR, ON EVERY BACKEND, AND ONE THAT IS NOT IS REFUSED WITH AN
        /// <see cref="ArgumentOutOfRangeException"/> NAMING THE SIDE IT CAME FROM.</b> macOS requires it of
        /// <c>copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size:</c>, so until 17.40.0 the same call
        /// succeeded on three backends and threw on Metal
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/602">#602</see>). The strictest backend's
        /// requirement is the seam's contract rather than one implementation's quirk, because the alternative is
        /// a portability trap a consumer only finds on a user's Mac. The SIZE is not constrained: only Metal
        /// needs it aligned and it pads the size up, which moves no data the caller asked for.
        /// </para>
        /// </summary>
        void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes, uint sizeInBytes);

        /// <summary>Copy a whole texture (e.g. render target -> staging) for readback.</summary>
        void CopyTexture(IGpuTexture src, IGpuTexture dst);

        /// <summary>Copy one mip level + array layer of <paramref name="src"/> (its top-left <paramref name="width"/> x
        /// <paramref name="height"/> region) into <paramref name="dst"/>'s mip 0 / layer 0 - for reading a specific
        /// mip of a texture array back to the CPU (e.g. verifying a generated mip chain). <paramref name="dst"/> must
        /// be at least that size.</summary>
        void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst, uint width, uint height);

        /// <summary>Copy one mip level + array layer of <paramref name="src"/> into a specific mip level + array
        /// layer of <paramref name="dst"/>. The general form of the overload above (which is this with a
        /// destination of mip 0 / layer 0). Its use here is seeding the base level of a MIPPED texture from a
        /// single-mip one written by compute: a storage-image binding must cover exactly one mip level, so a
        /// compute-written map that also needs a mip chain has to be two textures with a copy between them, not one
        /// texture bound both ways.</summary>
        void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer,
            IGpuTexture dst, uint dstMipLevel, uint dstArrayLayer, uint width, uint height);

        /// <summary>Generate the full mip chain of <paramref name="texture"/> from its base level. The texture must
        /// be created with <see cref="GpuTextureUsage.GenerateMipmaps"/> and a mip count &gt; 1.</summary>
        void GenerateMipmaps(IGpuTexture texture);

        /// <summary>Resolve a multisampled (<paramref name="src"/>, <see cref="IGpuTexture.SampleCount"/> &gt; 1) render
        /// target into the single-sample <paramref name="dst"/> (same width/height/format, sample count 1), averaging
        /// the samples - the MSAA resolve. Do this before the post chain / any pass that SAMPLES the target, since a
        /// multisampled texture cannot be bound as a normal sampled texture.</summary>
        void ResolveTexture(IGpuTexture src, IGpuTexture dst);

        // ---- Compute ----
        //
        // ORDERING CONTRACT. There is no explicit barrier call on this seam, because the backend layer has none:
        // what ordering exists comes from each backend's implicit handling, and no two backends agree. Two rules
        // fall out of that, and both are proved by the compute [GpuFact] suite on every backend.
        //
        // THE MECHANISMS BELOW ARE NAMED PER IMPLEMENTATION RATHER THAN PER API, and that is not pedantry. Every
        // API the engine renders on now has two backends behind it (Veldrid's and the engine's own
        // KhaozEngine.Gpu.Direct3D11, .Vulkan and .Metal), and the pairs do NOT handle either rule the same way.
        // A sentence that says "on Vulkan" was true of the one implementation that existed when it was written and
        // is false of the other. The Metal pair is the one place a single sentence still covers both, and that is a
        // measured coincidence rather than a licence to write to the API: both end the compute encoder when the
        // render encoder begins, because that is Metal's own rule and neither backend gets to choose. What does
        // NOT vary is the rule: write to the rule, never to the mechanism, because the mechanism is the part that
        // differs between two backends a consumer can swap with one environment variable.
        //
        //   1. Compute writes a storage texture, then a GRAPHICS pass samples it: record BOTH in the SAME command
        //      list, and create the texture with Storage | Sampled (see GpuTextureUsage.Storage). Every backend
        //      then handles the handoff. The native Vulkan backend tracks image layouts list-locally and
        //      transitions at the DRAW, from what the bound sets name, so the restore is not something it can
        //      skip (the Veldrid Vulkan backend, deleted in 18.0.0, queued a layout restore at dispatch time
        //      instead and drained it before the next draw, per command list and armed by the Sampled flag). The
        //      Metal backend ends the compute encoder when the render encoder begins, and the Direct3D 11 backend
        //      unbinds the UAV as the SRV is bound.
        //      Split across two command lists, the Veldrid Vulkan restore was silently skipped, and that split
        //      stays NOT safe on this seam.
        //
        //   2. A dispatch that READS what an earlier dispatch WROTE (the classic ping-pong: an FFT stage, a
        //      multi-pass reduction) must be separated by End + IGpuDevice.Submit + IGpuDevice.WaitForIdle.
        //      Chaining dependent dispatches inside one command list is NOT safe on this seam: on the Veldrid
        //      Vulkan backend (deleted in 18.0.0) no memory barrier was emitted between them at all (storage
        //      buffers were not tracked, and a storage image stayed in the same layout so the transition was a
        //      no-op), and dispatches inside a command buffer may overlap. A submit boundary plus a device drain
        //      is the only ordering this seam guarantees.
        //
        //      THE NATIVE VULKAN BACKEND IS MORE PERMISSIVE, AND THAT CHANGES NOTHING ABOVE. It emits a global
        //      memory barrier before a dispatch that binds a resource an earlier dispatch in the same recording
        //      wrote, so the chain is ordered there without the drain. That is a BACKEND PROPERTY, exactly like
        //      the nested-Begin permissiveness on IGpuCommandList.Begin, and dropping the End plus Submit plus
        //      WaitForIdle because it works on that backend is writing to the backend rather than to the seam
        //      (until 18.0.0 it broke outright on the Veldrid backend the same machine fell back to). It is
        //      evidence for an automatic-hazard seam capability
        //      (https://github.com/APKiwiOrg/KhaozEngine/issues/461), which is where a consumer-visible version
        //      of this would have to live.
        //
        //      SO IS THE NATIVE METAL BACKEND, BY A THIRD MECHANISM, AND IT IS THE ONE THAT COMPLETES A QUORUM.
        //      Its compute encoder is created with the default SERIAL dispatch type, where consecutive dispatches
        //      in one encoder are ordered and their hazards tracked by the driver, so a dependent chain is ordered
        //      there without a barrier and without the drain. That is three of three engine-owned backends
        //      honouring rule 2 natively by three different mechanisms (hazard tracking on Direct3D 11, a real
        //      barrier on Vulkan, serial ordering on Metal), and the two Veldrid legs that still needed the drain
        //      went away in 18.0.0. It is still a backend property and it still changes nothing above: the drain
        //      is what this SEAM guarantees, the quorum is evidence for #461 rather than a contract change, and
        //      consumer code that drops it because the machine it was written on tolerated it is relying on a
        //      property the seam never promised.
        //
        // Rule 2 costs a GPU stall per dependent stage, which is real: it is the current ceiling on any multi-pass
        // compute chain built against this seam.

        /// <summary>Bind a compute pipeline. Compute and graphics pipeline bindings are tracked separately, so
        /// this does not disturb a bound graphics pipeline (and vice versa).</summary>
        void SetComputePipeline(IGpuComputePipeline p);

        /// <summary>Bind a resource set to COMPUTE slot <paramref name="slot"/>. Compute and graphics resource-set
        /// bindings are separate: <see cref="SetGraphicsResourceSet(uint,IGpuResourceSet)"/> does not feed a
        /// dispatch and this does not feed a draw.</summary>
        void SetComputeResourceSet(uint slot, IGpuResourceSet set);

        /// <summary>Bind a compute resource set whose dynamic-offset buffer binding is rebased by
        /// <paramref name="dynamicOffset"/> bytes for this dispatch. The set must have exactly one element declared
        /// dynamic (see <see cref="GpuResourceLayoutElement.Dynamic"/>); the offset must satisfy the backend's
        /// uniform-buffer offset alignment (256 bytes is safe across Metal/Direct3D11/Vulkan). Lets a run of
        /// dispatches read their own per-stage parameter block out of one shared uniform buffer.</summary>
        void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset);

        /// <summary>Dispatch <paramref name="groupCountX"/> x <paramref name="groupCountY"/> x
        /// <paramref name="groupCountZ"/> WORKGROUPS of the bound compute pipeline. These are group counts, not
        /// thread counts: the total invocation count is the group count multiplied by the shader's
        /// <see cref="IGpuComputeShader.ThreadGroupSizeX"/>/<c>Y</c>/<c>Z</c>, so cover N elements with
        /// <c>(N + groupSize - 1) / groupSize</c> groups and bounds-check in the shader (the tail group runs on
        /// out-of-range indices). See the ordering contract above before chaining dependent dispatches.</summary>
        void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ);
    }

    /// <summary>The GPU device: backend info, capabilities, the resource factory, the swapchain framebuffer,
    /// buffer/texture updates, submission, and staging map/unmap. Engine mirror of Veldrid <c>GraphicsDevice</c>
    /// (the subset the 5.x renderers use). Veldrid is hidden inside the impl. Disposing any resource created
    /// by this device AFTER the device itself is disposed is a safe no-op, since device destruction already
    /// freed all child objects (teardown-order stragglers cannot destroy against a dead device).</summary>
    public interface IGpuDevice : IDisposable
    {
        /// <summary>The active backend.</summary>
        GpuBackendKind Backend { get; }
        /// <summary>Clip-space / depth conventions of the live device.</summary>
        GpuCapabilities Capabilities { get; }
        /// <summary>The resource factory.</summary>
        IGpuResourceFactory Factory { get; }
        /// <summary>The main swapchain framebuffer (null on a headless no-swapchain device).</summary>
        IGpuFramebuffer? SwapchainFramebuffer { get; }
        /// <summary>A shared point (nearest) sampler owned by the device, WRAP-addressed on all three axes.
        /// <para>
        /// THE ADDRESS MODE IS PART OF THE CONTRACT, and it is the opposite of what the identically named
        /// <see cref="GpuSamplerDescription.Point"/> static gives you: that static defaults every axis to
        /// <see cref="GpuSamplerAddress.Clamp"/>, this shared sampler wraps. Renderers sample through this pair
        /// assuming wrap (<c>ModelRenderer</c> says so in writing), so a backend that builds its shared pair from
        /// the engine statics renders every out-of-range tap clamped, which throws nothing and only a golden
        /// sees. Build the pair from wrap-addressed descriptions, not from the statics.
        /// </para></summary>
        IGpuSampler PointSampler { get; }
        /// <summary>A shared linear (bilinear) sampler owned by the device, WRAP-addressed on all three axes. The
        /// address-mode contract, and the clamp/wrap name collision behind it, are on
        /// <see cref="PointSampler"/>.</summary>
        IGpuSampler LinearSampler { get; }

        /// <summary>
        /// The two facts a device can only report about ITSELF and only LIVE: whether it is on a software
        /// rasterizer, and why it was lost if it has been. Read every time a telemetry session header is written,
        /// never cached, because a device loss happens long after creation and a captured value would always say
        /// the device was fine.
        /// <para>
        /// DEFAULT-IMPLEMENTED, so this was appended without breaking any implementer, and the default is the
        /// honest one: no answers. A backend that cannot report either fact leaves both null, and null means
        /// "nobody answered" rather than "no" (see <see cref="GpuDeviceDiagnostics"/>). The Veldrid path takes the
        /// default today, which is correct rather than a gap: Veldrid exposes neither the DXGI adapter flag nor a
        /// device-removal reason, so a value from it would have to be invented.
        /// </para>
        /// </summary>
        GpuDeviceDiagnostics Diagnostics => default;

        /// <summary>
        /// The soak counters this device keeps about itself, cumulative since creation and read LIVE: how much it
        /// waited for the GPU to go idle, how often a frame boundary blocked on a busy uniform ring segment, and
        /// how many device-level writes were queued against a segment still in flight. Sampled into a telemetry
        /// session's rows through <see cref="GpuTelemetryChannels"/>.
        /// <para>
        /// DEFAULT-IMPLEMENTED, and the default is the honest one: a device that counts none of this leaves
        /// <see cref="GpuDeviceCounters.HasValue"/> false, which is a DIFFERENT fact from counting and finding
        /// zero. Metal and the incumbent Veldrid paths take the default. The two native backends report what
        /// their subsystems count, D3D11 all seven fields, Vulkan the drain pair until its remaining subsystems
        /// land.
        /// </para>
        /// <para>
        /// SAMPLE IT FROM ANY THREAD. The values are monotone cumulative and every one of them is read whole, so a
        /// sampler does not have to be the frame thread and does not have to synchronise with it. The one thing a
        /// concurrent sample can do is straddle a wait that is in progress, reporting a count and a duration one
        /// entry apart, which is noise over a capture window and never a torn number.
        /// </para>
        /// </summary>
        GpuDeviceCounters Counters => default;

        /// <summary>Submit a finished command list for execution.</summary>
        void Submit(IGpuCommandList cl);

        /// <summary>Submit a finished command list and signal <paramref name="fence"/> once the GPU has finished
        /// executing it. The fence must be unsignaled (fresh from <see cref="IGpuResourceFactory.CreateFence"/>, or
        /// <see cref="IGpuFence.Reset"/> since its last signal).
        /// <para>THE ORDERING THIS BUYS, and it is the whole point: a fence handed to a submission made after some
        /// earlier work signals only once the queue has drained through it, so polling it to
        /// <see cref="IGpuFence.Signaled"/> is exactly the guarantee <see cref="WaitForIdle"/> gives at the moment
        /// of the submit, without the block. Vulkan says so in as many words (<c>vkQueueWaitIdle</c> is specified as
        /// equivalent to submitting a fence to the queue and waiting on it), and Metal executes a queue's command
        /// buffers in commit order. Requires <see cref="GpuCapabilities.SupportsCompletionFences"/>.</para></summary>
        void Submit(IGpuCommandList cl, IGpuFence fence);

        /// <summary>Block until the GPU is idle. After the device is disposed this is a safe no-op (a dead
        /// device has nothing to wait for), so a resource wrapper draining before its own disposal stays safe
        /// when it outlives the device at teardown. Calling it concurrently WITH device disposal remains a
        /// consumer ordering error.</summary>
        void WaitForIdle();

        /// <summary>
        /// Upload a span of unmanaged elements into a buffer at <paramref name="offsetBytes"/>.
        /// <para>
        /// <b>This write lands when you call it, OFF the command timeline.</b> Anything a submitted-later command
        /// list reads is therefore whatever the CPU wrote most recently, not what was current when the list was
        /// recorded, and the CPU can be several frames ahead of the GPU. For per-frame values a list reads
        /// (uniforms, instance data) use <see cref="IGpuCommandList.UpdateBuffer{T}(IGpuBuffer, uint, in T)"/>
        /// instead, which copies at RECORD time and applies in list order. This one is for uploads that happen
        /// once (mesh vertices, an index buffer, a baked field) or that a drain already orders. The FFT ocean lost
        /// a frame of surface phase to exactly this when the drain that had been hiding it went away (#398).
        /// </para>
        /// </summary>
        void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged;
        /// <summary>Upload an array (convenience). Off-timeline, per the span overload.</summary>
        void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged;
        /// <summary>Upload a single unmanaged struct (convenience). Off-timeline, per the span overload.</summary>
        void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged;

        /// <summary>Upload CPU RGBA (or format-matching) bytes into a texture sub-region (mip 0, layer 0).</summary>
        void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height);

        /// <summary>Upload CPU bytes into a texture sub-region at an explicit <paramref name="mipLevel"/> and
        /// <paramref name="arrayLayer"/> (the splat-terrain layer stacks upload each layer's base mip).</summary>
        void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height, uint mipLevel, uint arrayLayer);

        /// <summary>Map a staging resource for CPU access.</summary>
        MappedData Map(IGpuTexture staging, GpuMapMode mode);
        /// <summary>Unmap a previously mapped staging resource.</summary>
        void Unmap(IGpuTexture staging);

        /// <summary>Map a staging BUFFER (created with <see cref="GpuBufferUsage.Staging"/>) for CPU access - the
        /// buffer half of the texture map/unmap pair, and how a compute-written storage buffer is read back after
        /// <see cref="IGpuCommandList.CopyBuffer"/>. <see cref="MappedData.RowPitch"/> is meaningless for a buffer
        /// (it equals the size); the data is a flat byte range. Prefer <see cref="GpuReadback.ReadBuffer{T}"/>,
        /// which wraps the whole staging-copy-map-unmap sequence.</summary>
        MappedData Map(IGpuBuffer staging, GpuMapMode mode);
        /// <summary>Unmap a previously mapped staging buffer.</summary>
        void Unmap(IGpuBuffer staging);

        /// <summary>Resize the main swapchain.</summary>
        void ResizeSwapchain(uint w, uint h);
        /// <summary>Present the main swapchain.</summary>
        void Present();

        /// <summary>
        /// Whether presentation syncs to the display's vertical blank. Settable at runtime: on a windowed device this
        /// reconfigures the live swapchain in place (no recreate, no leaked swapchain, size + depth preserved), so a
        /// game can flip vsync mid-session. A no-op backing value on a headless (no-swapchain) device. On Metal it
        /// sets the layer's <c>displaySyncEnabled</c>, but the Veldrid Metal present still does not throttle the CPU
        /// from this alone - pair with a software frame cap for a deterministic rate (see <c>PresentMode</c>).
        /// <para>
        /// THAT SECOND CLAUSE IS MEASURED ON THE VELDRID METAL BACKEND, AND ONLY THERE. The engine's own
        /// <see cref="GpuBackendKind.MetalNative"/> writes <c>displaySyncEnabled</c> unconditionally where the
        /// incumbent writes it inside three values of a deprecated enum, and bounds <c>maximumDrawableCount</c>
        /// with a blocking acquire at the present boundary. Rollout gate 5 of
        /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c> measured it on 2026-08-11 and those two
        /// DO throttle the CPU: the acquire blocks once per frame for 15.175 ms of a 16.669 ms frame, a display
        /// pinned to 120 Hz paces the loop at 120 fps, and turning this property off mid-session free-runs past
        /// 700 fps with visible tearing. So the software cap is the incumbent's alone now
        /// (<c>FrameCap.Resolve</c> carries the full reasoning), and vsync with no cap is healthy on the native
        /// backend.
        /// </para>
        /// </summary>
        bool SyncToVerticalBlank { get; set; }
    }
}
