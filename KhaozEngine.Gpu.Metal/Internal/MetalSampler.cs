using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// The seam's <see cref="IGpuSampler"/> over one <c>MTLSamplerState</c>. Every decision the descriptor carries
    /// is <see cref="MetalSamplerPolicy"/>'s, so this type is the object lifetime and nothing else.
    /// </summary>
    internal sealed class MetalSampler : IGpuSampler, IMetalOwnedResource
    {
        readonly IMetalDeviceLiveness _liveness;
        readonly MTLSamplerState _sampler;

        bool _disposed;

        MetalSampler(IMetalDeviceLiveness liveness, MTLSamplerState sampler)
        {
            _liveness = liveness;
            _sampler = sampler;
        }

        /// <summary>The native sampler state, for the bind path. Nil after disposal.</summary>
        internal MTLSamplerState Handle => _disposed ? default : _sampler;

        /// <inheritdoc/>
        /// <remarks>Nothing on this row takes a sampler as a device entry point's parameter, so nothing checks it
        /// yet. It is recorded now because a resource that learns its owner LATER learns it in the row that has
        /// already written the bind path, and the resource-set row is the one that will ask.</remarks>
        public IMetalDeviceLiveness Owner => _liveness;

        /// <summary>Create one from the seam's description, through the policy.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MetalSampler Create(MTLDevice device, IMetalDeviceLiveness liveness,
            in GpuSamplerDescription description)
        {
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

                return new MetalSampler(liveness, sampler);
            }
            finally
            {
                descriptor.Release();
            }
        }

        /// <summary>Release the sampler state, once, and never on a dead device (M-F6).</summary>
        public void Dispose()
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
