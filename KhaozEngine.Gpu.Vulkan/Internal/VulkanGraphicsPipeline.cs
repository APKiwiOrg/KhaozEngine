using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// <see cref="IGpuPipeline"/> on the native Vulkan backend: a <c>VkPipeline</c>, the SHARED
    /// <c>VkPipelineLayout</c> it was created with, and that layout's set-layout sequence. Work-breakdown row 13
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/523).
    ///
    /// <para><b>THE THREE FIELDS ARE THE THREE THINGS A BIND NEEDS, AND NOTHING ELSE IS KEPT.</b>
    /// <c>SetPipeline</c> emits <c>vkCmdBindPipeline</c> with <see cref="Handle"/> and then hands
    /// <see cref="PipelineLayout"/> and <see cref="SetLayouts"/> to
    /// <see cref="VulkanBindRecords.SetPipelineLayout"/>, which invalidates recorded descriptor slots from the
    /// first INCOMPATIBLE set onward (V-R6). The blend, depth, raster and vertex state a
    /// <c>D3D11GraphicsPipeline</c> has to keep as four live state objects is baked into the driver's own object
    /// here, which is the whole difference between the two APIs at this seat.</para>
    ///
    /// <para><b>THE SET-LAYOUT ARRAY IS OWNED FOR THIS OBJECT'S LIFE AND NEVER MUTATED.</b> The bind records hold
    /// it by reference across draws and compare against it on the next switch, so a caller that reused the array
    /// would silently change what an already-bound pipeline claims to be compatible with.</para>
    ///
    /// <para><b>DISPOSAL IS A REAL, DEFERRED DESTROY (V-F9), unlike a shader set's or a resource layout's.</b>
    /// Those two hold SHARED handles that one wrapper may not end. A <c>VkPipeline</c> is not shared: it is
    /// created for this object and destroyed with it. It is destroyed BEHIND THE TIMELINE because a submission
    /// that bound it can still be executing, and destroying a pipeline a command buffer in flight names is
    /// undefined behaviour of the quiet kind.</para>
    /// </summary>
    internal sealed class VulkanGraphicsPipeline : IGpuPipeline
    {
        readonly VulkanPipelineOwner _owner;
        readonly ulong[] _setLayouts;

        ulong _handle;

        /// <param name="owner">The device's pipeline seam, timeline and retire list.</param>
        /// <param name="handle">The <c>VkPipeline</c>, non-zero.</param>
        /// <param name="pipelineLayout">The shared <c>VkPipelineLayout</c> it was created with.</param>
        /// <param name="setLayouts">That layout's set-layout handles in slot order. Taken over rather than
        /// copied.</param>
        internal VulkanGraphicsPipeline(VulkanPipelineOwner owner, ulong handle, ulong pipelineLayout,
            ulong[] setLayouts)
        {
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(setLayouts);

            _owner = owner;
            _handle = handle;
            _setLayouts = setLayouts;
            PipelineLayout = pipelineLayout;
        }

        /// <summary>The <c>VkPipeline</c>, which <c>vkCmdBindPipeline</c> names.</summary>
        internal ulong Handle => _handle;

        /// <summary>The SHARED <c>VkPipelineLayout</c>, identity-equal to every other pipeline built from the
        /// same set layouts (V-D5), which is what makes the compatibility test a pointer compare.</summary>
        internal ulong PipelineLayout { get; }

        /// <summary>The set-layout handles in slot order, as the bind records compare them.</summary>
        internal ulong[] SetLayouts => _setLayouts;

        /// <summary>True once disposed. The destroy is deferred, so this flips before the handle goes.</summary>
        internal bool IsDisposed { get; private set; }

        /// <summary>Retire the <c>VkPipeline</c> behind the timeline. Idempotent, because a consumer disposing a
        /// pipeline twice is a teardown-order accident rather than a defect, and retiring the same handle twice
        /// would double-destroy it.</summary>
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;

            ulong handle = _handle;
            _handle = 0;
            if (handle == 0) return;

            _owner.RetireTerminal(() => _owner.Api.DestroyPipeline(handle));
        }

        /// <summary>A pipeline this backend created, refused by name for anything else.</summary>
        internal static VulkanGraphicsPipeline Require(IGpuPipeline? pipeline, string what)
            => pipeline as VulkanGraphicsPipeline
                ?? throw new ArgumentException(
                    $"The graphics pipeline handed to {what} was not created by the native Vulkan backend, so it "
                    + "carries no VkPipeline and no VkPipelineLayout. Create pipelines through the same "
                    + "IGpuDevice.Factory the command list records against.",
                    nameof(pipeline));
    }
}
