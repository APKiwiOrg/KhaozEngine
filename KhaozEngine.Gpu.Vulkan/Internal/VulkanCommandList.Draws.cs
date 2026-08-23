using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE DRAW AND DISPATCH FAMILY OF THE COMMAND LIST: the vertex and index binds, both <c>Draw</c> overloads,
    /// <c>DrawIndexed</c> and <c>Dispatch</c>. Split into its own partial per
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/556, alongside
    /// <see cref="VulkanCommandList"/>'s transfer partial and for the same reason: the main file sits against the
    /// KESIZE cap and these are a different subsystem from the recording lifecycle.
    /// <para>
    /// EVERY MEMBER HERE IS A RESOLVE PLUS ONE CALL INTO <see cref="VulkanDrawRecorder"/>, which owns the ORDER.
    /// That is deliberate rather than thin: five draw members repeating a four-step pre-command sequence is five
    /// places for a step to go missing, and a missing step renders plausibly wrong instead of throwing.
    /// </para>
    /// </summary>
    internal sealed partial class VulkanCommandList
    {
        /// <summary>
        /// ROW 15's PRE-COMMAND ORDER, its vertex and index bind records and its dependent-dispatch hazard set.
        /// Exposed because the device-free tests drive the run cutting, the identity guards and the hazard set
        /// through it, exactly as <see cref="GraphicsBinds"/> and <see cref="Rendering"/> are exposed for theirs.
        /// </summary>
        /// <exception cref="NotSupportedException">This list was built with no draw seam, which is only a list a
        /// test constructed.</exception>
        internal VulkanDrawRecorder Draws => RequireDraws("Reading the draw recorder");

        /// <inheritdoc/>
        /// <remarks>The no-offset overload, which is the offset overload at zero. There is no distinction to
        /// preserve here: a vertex binding's offset is one number the driver receives either way.</remarks>
        public void SetVertexBuffer(uint slot, IGpuBuffer b) => SetVertexBuffer(slot, b, 0);

        /// <inheritdoc/>
        /// <remarks>
        /// RECORDS ONLY. No native call until a draw, which is the same shape a resource-set bind takes and for
        /// the same reasons: a rebind of what is already recorded costs nothing, several rebinds between two draws
        /// collapse to one emission, and the flush cuts ONE <c>vkCmdBindVertexBuffers</c> per contiguous run of
        /// dirty slots. The incumbent emitted at the call with no guard, so a renderer that rebinds one mesh's
        /// buffer before each of its draws pays a native call per draw for a state change that did not happen.
        /// <para>
        /// A RECORD MADE OUTSIDE A RECORDING IS DISCARDED RATHER THAN REFUSED, exactly as a resource-set bind is,
        /// and for the identical reason: it touches nothing but this list's own array, and <see cref="Begin"/>
        /// resets that array, so a bind made before a recording cannot leak into the one that follows.
        /// </para>
        /// <para>
        /// THE USAGE IS CHECKED. A buffer created without <see cref="GpuBufferUsage.VertexBuffer"/> carries no
        /// <c>VK_BUFFER_USAGE_VERTEX_BUFFER_BIT</c>, so binding it is a validation error on a machine with the
        /// layer and undefined behaviour on one without.
        /// </para>
        /// </remarks>
        public void SetVertexBuffer(uint slot, IGpuBuffer b, uint offsetBytes)
        {
            VulkanBuffer buffer = VulkanBuffer.RequireUsage(
                b, GpuBufferUsage.VertexBuffer, "a native Vulkan vertex buffer bind");

            RequireDraws("Binding a vertex buffer").Geometry.RecordVertex(slot, buffer.Handle, offsetBytes);
        }

        /// <inheritdoc/>
        /// <remarks>The index arm of the same record, carrying the element width, which is the third thing a
        /// rebind compares: the same buffer at the same offset read as 16-bit rather than 32-bit is a different
        /// bind.</remarks>
        public void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt)
        {
            VulkanBuffer buffer = VulkanBuffer.RequireUsage(
                b, GpuBufferUsage.IndexBuffer, "a native Vulkan index buffer bind");

            RequireDraws("Binding an index buffer").Geometry.RecordIndex(
                buffer.Handle, 0, fmt == GpuIndexFormat.UInt16);
        }

        /// <inheritdoc/>
        /// <remarks>The single-instance convenience the fullscreen passes call, which is the full overload at
        /// <c>instanceCount = 1</c> and both starts at zero.</remarks>
        public void Draw(uint vertexCount) => Draw(vertexCount, 1, 0, 0);

        /// <inheritdoc/>
        /// <remarks>
        /// <c>vkCmdDraw</c>, behind <see cref="VulkanDrawRecorder"/>'s four-step pre-command order: the bound
        /// sets' images into the layouts their bindings need (the compute rule 1 barrier, V-C1), then
        /// <see cref="PrepareDraw"/>'s deferred begin and dynamic state, then the vertex and index binds, then the
        /// descriptor flush and the command as one monomorphized pair.
        /// <para>
        /// EIGHT INSTANCES IS STILL ONE DRAW AND ONE BIND TRACE, which is the identity MV4 freezes: the instance
        /// count is an argument to this call and influences nothing above it.
        /// </para>
        /// </remarks>
        public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
            => RequireDraws("Drawing").Draw(CurrentBuffer, RequireRendering("Drawing"), _graphicsBinds,
                new VulkanDrawCall(vertexCount, instanceCount, vertexStart, instanceStart));

        /// <inheritdoc/>
        /// <remarks>The indexed arm, identical in every step but the command. <paramref name="vertexOffset"/> is
        /// signed and travels signed: it is added to every index before the vertex buffer is read, and a mesh
        /// packed behind another one in a shared buffer passes a negative one.</remarks>
        public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset,
            uint instanceStart)
            => RequireDraws("Drawing indexed").DrawIndexed(CurrentBuffer, RequireRendering("Drawing indexed"),
                _graphicsBinds,
                new VulkanIndexedDrawCall(indexCount, instanceCount, indexStart, vertexOffset, instanceStart));

        /// <inheritdoc/>
        /// <remarks>
        /// <c>vkCmdDispatch</c>, with the pending render pass instance ENDED first (V-A4), the compute set's
        /// storage images in <c>GENERAL</c> and its sampled ones in <c>SHADER_READ_ONLY_OPTIMAL</c>, and the
        /// read-after-write barrier emitted when an earlier dispatch in this recording wrote something this one
        /// binds (V-C2).
        /// <para>
        /// THAT BARRIER IS NOT A CONTRACT CHANGE AND MUST NOT BE READ AS ONE. The seam's compute rule 2 is
        /// honoured as written: chaining dependent dispatches inside one list still needs <c>End</c>,
        /// <c>Submit</c> and <c>WaitForIdle</c> on the PORTABLE contract, because the Veldrid legs needed the drain
        /// and a consumer that drops it because this backend tolerates the chain breaks on Metal. See
        /// <see cref="VulkanComputeHazards"/>.
        /// </para>
        /// </remarks>
        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
            => RequireDraws("Dispatching compute").Dispatch(CurrentBuffer,
                RequireRendering("Dispatching compute"), _computeBinds, groupCountX, groupCountY, groupCountZ);

        // THE ONE THING EVERY MEMBER HERE NEEDS TRUE. A list built with no draw seam is only a list a test
        // constructed: every list the device hands out has one.
        //
        // RECORDING IS NOT REQUIRED HERE, and the asymmetry with RequireRendering is deliberate. The two binds
        // above RECORD, so a call outside a recording is discarded exactly as a resource-set bind is. The three
        // commands below go through RequireRendering as well, which is what refuses them outside a recording, and
        // it refuses for the reason it always did: a vkCmd* against a buffer vkBeginCommandBuffer has not seen is
        // undefined behaviour rather than a no-op.
        VulkanDrawRecorder RequireDraws(string what)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            return _draws ?? throw new NotSupportedException(
                what + " on a native Vulkan command list needs its draw recorder, and this list was built with "
                + "none, so it can bind no geometry and issue no command. Every list the device hands out has "
                + "one: this is a list constructed directly by a test.");
        }
    }
}
