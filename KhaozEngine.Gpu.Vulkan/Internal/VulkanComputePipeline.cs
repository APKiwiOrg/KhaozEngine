using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// <see cref="IGpuComputePipeline"/> on the native Vulkan backend, and the compute twin of
    /// <see cref="VulkanGraphicsPipeline"/> in every respect that matters: a <c>VkPipeline</c>, the shared
    /// <c>VkPipelineLayout</c> it was created with, and that layout's set-layout sequence. Work-breakdown row 13
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/523).
    ///
    /// <para><b>IT IS A DISTINCT TYPE BECAUSE THE SEAM MAKES IT ONE, AND THE SEAM IS RIGHT.</b>
    /// <see cref="IGpuComputePipeline"/> and <see cref="IGpuPipeline"/> are separate interfaces precisely so a
    /// compute pipeline cannot be bound for a draw, and Vulkan agrees underneath: a pipeline is created for one
    /// bind point and binding it at the other is invalid. Sharing one wrapper would put the bind point in a field
    /// and turn a compile error into a runtime one.</para>
    ///
    /// <para><b>THE WORKGROUP SIZE IS NOT HERE, and its absence is decision-shaped.</b> Vulkan takes the local
    /// size from the module and ignores anything a create-info says, so the dispatch reads
    /// <see cref="IGpuComputeShader.ThreadGroupSizeX"/> off the shader that made this pipeline rather than a copy
    /// held here that could disagree with the SPIR-V. See <see cref="VulkanComputeShader"/>.</para>
    /// </summary>
    internal sealed class VulkanComputePipeline : IGpuComputePipeline
    {
        readonly VulkanPipelineOwner _owner;
        readonly ulong[] _setLayouts;

        ulong _handle;

        /// <param name="owner">The device's pipeline seam, timeline and retire list.</param>
        /// <param name="handle">The <c>VkPipeline</c>, non-zero.</param>
        /// <param name="pipelineLayout">The shared <c>VkPipelineLayout</c> it was created with.</param>
        /// <param name="setLayouts">That layout's set-layout handles in slot order. Taken over rather than
        /// copied.</param>
        internal VulkanComputePipeline(VulkanPipelineOwner owner, ulong handle, ulong pipelineLayout,
            ulong[] setLayouts)
        {
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(setLayouts);

            _owner = owner;
            _handle = handle;
            _setLayouts = setLayouts;
            PipelineLayout = pipelineLayout;
        }

        /// <summary>The <c>VkPipeline</c>, which <c>vkCmdBindPipeline</c> names at the COMPUTE bind point.
        /// </summary>
        internal ulong Handle => _handle;

        /// <summary>The SHARED <c>VkPipelineLayout</c> (V-D5).</summary>
        internal ulong PipelineLayout { get; }

        /// <summary>The set-layout handles in slot order, as the COMPUTE bind records compare them. Graphics and
        /// compute bindings are tracked separately (V-C1), so a compute switch never invalidates a graphics
        /// slot.</summary>
        internal ulong[] SetLayouts => _setLayouts;

        /// <summary>True once disposed.</summary>
        internal bool IsDisposed { get; private set; }

        /// <summary>Retire the <c>VkPipeline</c> behind the timeline, exactly as the graphics arm does, and
        /// idempotent for the same reason.</summary>
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;

            ulong handle = _handle;
            _handle = 0;
            if (handle == 0) return;

            _owner.RetireTerminal(() => _owner.Api.DestroyPipeline(handle));
        }

        /// <summary>A compute pipeline this backend created, refused by name for anything else.</summary>
        internal static VulkanComputePipeline Require(IGpuComputePipeline? pipeline, string what)
            => pipeline as VulkanComputePipeline
                ?? throw new ArgumentException(
                    $"The compute pipeline handed to {what} was not created by the native Vulkan backend, so it "
                    + "carries no VkPipeline and no VkPipelineLayout. Create pipelines through the same "
                    + "IGpuDevice.Factory the command list records against.",
                    nameof(pipeline));
    }
}
