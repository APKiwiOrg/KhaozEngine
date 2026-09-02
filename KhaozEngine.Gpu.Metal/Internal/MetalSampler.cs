using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// The seam's <see cref="IGpuSampler"/> over one <c>MTLSamplerState</c>. Every decision the descriptor carries
    /// is <see cref="MetalSamplerPolicy"/>'s, so this type is the object lifetime and nothing else.
    ///
    /// <para><b>THE DEVICE'S SHARED PAIR DOES NOT OWN ITS SAMPLER</b>
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/506). A consumer that disposes the object it got from
    /// <c>IGpuDevice.PointSampler</c> destroys nothing, because the device hands out the same two for the
    /// process's life and destroys them itself at teardown through <see cref="DestroyShared"/>. That is the rule
    /// the Vulkan and Direct3D 11 backends apply to their own pairs, and it is why <c>ownsSampler</c> exists at
    /// all.</para>
    /// </summary>
    internal sealed class MetalSampler : IGpuSampler, IMetalOwnedResource, IMetalBindable
    {
        readonly IDeviceLiveness _liveness;
        readonly MTLSamplerState _sampler;
        readonly bool _owns;

        bool _disposed;

        MetalSampler(IDeviceLiveness liveness, MTLSamplerState sampler, bool ownsSampler)
        {
            _liveness = liveness;
            _sampler = sampler;
            _owns = ownsSampler;
        }

        /// <summary>The native sampler state, for the bind path. Nil after disposal. A NON-OWNING wrapper never
        /// marks itself disposed through <see cref="Dispose"/>, so the device's shared pair keeps answering after
        /// a consumer disposes what it was handed, which is the whole point of the pair being non-owning.
        /// </summary>
        internal MTLSamplerState Handle => _disposed ? default : _sampler;

        /// <summary>Whether this wrapper releases its sampler. False for the device's shared pair.</summary>
        internal bool OwnsSampler => _owns;

        /// <inheritdoc/>
        /// <remarks>The guarded <see cref="Handle"/>, read at the bind rather than copied into a resource set at
        /// its creation. See <see cref="IMetalBindable"/>.</remarks>
        IntPtr IMetalBindable.BindHandle => Handle.Handle;

        /// <inheritdoc/>
        /// <remarks>Null always: a sampler has no uniform ring and no offset of any kind.</remarks>
        MetalUniformRing? IMetalBindable.BindRing => null;

        /// <inheritdoc/>
        /// <remarks>Nothing on this row takes a sampler as a device entry point's parameter, so nothing checks it
        /// yet. It is recorded now because a resource that learns its owner LATER learns it in the row that has
        /// already written the bind path, and the resource-set row is the one that will ask.</remarks>
        public IDeviceLiveness Owner => _liveness;

        /// <summary>
        /// Create one from the seam's description, through the policy.
        /// <para>
        /// <paramref name="deviceSupportsBorderColor"/> is the device's own <c>MTLGPUFamilyMac2</c> answer, read
        /// once at device creation and carried here rather than asked per sampler. A false answer with a Border
        /// address mode is the ONE description this backend refuses outright, and the refusal is named: the
        /// alternative on such a device is a process abort under the debug layer.
        /// </para>
        /// <para>
        /// <paramref name="ownsSampler"/> is false for a device-owned shared sampler (the point and linear pair
        /// every device exposes), so handing one to a consumer that disposes it does not destroy the device's
        /// own. Only <see cref="DestroyShared"/> frees one of those.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MetalSampler Create(MTLDevice device, IDeviceLiveness liveness,
            in GpuSamplerDescription description, bool deviceSupportsBorderColor, bool ownsSampler = true)
        {
            // BEFORE THE DESCRIPTOR EXISTS, because the abort this avoids happens inside
            // -newSamplerStateWithDescriptor: and nothing after that call gets to run.
            if (MetalSamplerPolicy.MissingBorderColorSupport(description, deviceSupportsBorderColor) is { } missing)
                throw new NotSupportedException("The native Metal backend cannot create this sampler: " + missing);

            MetalSamplerSpec spec = MetalSamplerPolicy.For(description);

            MTLSamplerDescriptor descriptor = MTLSamplerDescriptor.New();
            if (descriptor.IsNull)
            {
                throw new InvalidOperationException(
                    "The Objective-C runtime has no MTLSamplerDescriptor class, which means the Metal framework "
                    + "did not load. Nothing about this sampler caused it.");
            }

            try
            {
                descriptor.Configure(spec.AddressS, spec.AddressT, spec.AddressR, spec.Filters.Min,
                    spec.Filters.Mag, spec.Filters.Mip, spec.BorderColor, spec.MaxAnisotropy, spec.LodMinClamp,
                    spec.LodMaxClamp);

                MTLSamplerState sampler = device.NewSamplerState(descriptor);
                if (sampler.IsNull)
                {
                    throw new InvalidOperationException(
                        "The native Metal device refused a sampler state for " + description.Filter
                        + " filtering with addressing (" + description.AddressModeU + ", "
                        + description.AddressModeV + ", " + description.AddressModeW
                        + "). -newSamplerStateWithDescriptor: answers nil for a descriptor it cannot satisfy, and "
                        + "the sampler descriptor carries no value this seam can set out of range.");
                }

                return new MetalSampler(liveness, sampler, ownsSampler);
            }
            finally
            {
                descriptor.Release();
            }
        }

        /// <summary>
        /// Release the sampler state, once, and never on a dead device (M-F6), or do nothing AT ALL for the
        /// device's shared pair.
        /// <para>
        /// A NON-OWNING WRAPPER RETURNS WITHOUT EVEN MARKING ITSELF DISPOSED, which looks like an oversight and
        /// is the point: <see cref="Handle"/> reads the flag, so marking it here would nil the handle the device
        /// still hands to every bind, and <see cref="DestroyShared"/> would become a no-op that leaked the
        /// sampler until the device itself went away.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            if (!_owns || _disposed) return;
            _disposed = true;

            if (_liveness.IsDead) return;
            if (!KhaozEngineMetal.IsPlatformSupported) return;

            ReleaseOnMacOs();
        }

        /// <summary>Release a DEVICE-OWNED shared sampler, which <see cref="Dispose"/> deliberately will not. The
        /// device's teardown calls this in the window before the liveness flip, which is the only window in which
        /// releasing a child object of the device is safe.</summary>
        internal void DestroyShared()
        {
            if (_disposed) return;
            _disposed = true;

            if (_liveness.IsDead) return;
            if (!KhaozEngineMetal.IsPlatformSupported) return;

            ReleaseOnMacOs();
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void ReleaseOnMacOs()
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();
            _sampler.Release();
        }
    }
}
