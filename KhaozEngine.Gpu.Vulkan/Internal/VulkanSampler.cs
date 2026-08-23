using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// <see cref="IGpuSampler"/> on the native Vulkan backend: one <c>VkSampler</c>, created eagerly, retired
    /// behind the timeline.
    ///
    /// <para><b>THE MAPPING IS <see cref="VulkanSamplerPolicy"/>'s</b>, including the anisotropy degradation a
    /// device without <c>samplerAnisotropy</c> forces, so everything about a sampler that can be wrong is decided
    /// in a pure function and pinned by a <c>[Fact]</c>. What is left here is a handle and a lifetime.</para>
    ///
    /// <para><b>THE DEVICE'S SHARED PAIR DOES NOT OWN ITS SAMPLER.</b> A consumer that disposes the object it got
    /// from <c>IGpuDevice.PointSampler</c> destroys nothing, because the device hands out the same two for the
    /// process's life and destroys them itself at teardown. That is the rule the Direct3D 11 backend applies to
    /// its own shared pair too, and it is why <c>ownsSampler</c> exists at all.</para>
    ///
    /// <para><b>DISPOSAL IS ONE TERMINAL RETIRE (V-F9)</b>, with nothing to free underneath it: a sampler owns no
    /// memory, so the entry is a single <c>vkDestroySampler</c> that allocates nothing and retires nothing.</para>
    /// </summary>
    internal sealed class VulkanSampler : IGpuSampler
    {
        readonly VulkanResourceOwner _owner;
        readonly bool _owns;

        bool _disposed;

        /// <param name="owner">The device's resource seam, timeline and retire list.</param>
        /// <param name="description">The seam's description.</param>
        /// <param name="deviceSamplerAnisotropy">Whether the device enabled the <c>samplerAnisotropy</c> feature.
        /// False degrades an anisotropic request to trilinear, exactly as the engine's Veldrid path did, and
        /// asking for anisotropy without the feature is a validation error rather than a slow path.</param>
        /// <param name="ownsSampler">False for a device-owned shared sampler, so handing one to a consumer that
        /// disposes it does not destroy the device's own.</param>
        internal VulkanSampler(VulkanResourceOwner owner, in GpuSamplerDescription description,
            bool deviceSamplerAnisotropy, bool ownsSampler = true)
        {
            ArgumentNullException.ThrowIfNull(owner);

            _owner = owner;
            _owns = ownsSampler;

            Spec = VulkanSamplerPolicy.For(description, deviceSamplerAnisotropy);
            Handle = owner.Api.CreateSampler(Spec);
        }

        /// <summary>The <c>VkSampler</c> handle, which a descriptor write names.</summary>
        internal ulong Handle { get; }

        /// <summary>What it was really created with, AFTER the anisotropy degradation. Held so a diagnostic and a
        /// test can read the effective sampler rather than the requested one.</summary>
        internal VulkanSamplerSpec Spec { get; }

        /// <summary>Whether this wrapper destroys its sampler. False for the device's shared pair.</summary>
        internal bool OwnsSampler => _owns;

        /// <summary>True once disposed, whether or not anything native was retired.</summary>
        internal bool IsDisposed => _disposed;

        /// <summary>
        /// Retire the sampler behind the timeline (V-F9), or do nothing AT ALL for the device's shared pair.
        /// Idempotent.
        /// <para>
        /// A NON-OWNING WRAPPER RETURNS WITHOUT EVEN MARKING ITSELF DISPOSED, which looks like an oversight and is
        /// the point: the device destroys its shared pair through <see cref="DestroyShared"/> at teardown, and a
        /// flag set here would make that call a no-op. A consumer disposing what
        /// <c>IGpuDevice.PointSampler</c> handed back would then leak the device's own sampler until
        /// <c>vkDestroyDevice</c> collected it.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            if (!_owns || _disposed) return;
            _disposed = true;

            ulong handle = Handle;
            VulkanResourceOwner owner = _owner;
            owner.RetireTerminal(() => owner.Api.DestroySampler(handle));
        }

        /// <summary>Destroy a DEVICE-OWNED shared sampler, which <see cref="Dispose"/> deliberately will not. The
        /// device's teardown calls this in the window between <c>vkDeviceWaitIdle</c> and the liveness flip, which
        /// is the only window in which destroying a child object of the device is both safe and legal.</summary>
        internal void DestroyShared()
        {
            if (_disposed) return;
            _disposed = true;

            _owner.Api.DestroySampler(Handle);
        }
    }
}
