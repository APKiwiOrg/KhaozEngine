using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Internal;
using Vortice.Direct3D11;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// <see cref="IGpuSampler"/> for the native Direct3D 11 backend: one <c>ID3D11SamplerState</c>, created
    /// eagerly, disposed under the liveness gate.
    /// <para>
    /// FOUR VALUES ARE HARDCODED, and they are hardcoded because the incumbent hardcoded them and the committed
    /// goldens were baked through them. No comparison function (the shadow path does manual PCF and never asks for
    /// a comparison sampler), a minimum LOD of 0, a maximum LOD of <c>uint.MaxValue</c> which Direct3D clamps to
    /// the real chain, and a transparent-black border colour. The seam exposes none of the four, so a caller
    /// cannot ask for anything else, and changing one would move pixels.
    /// </para>
    /// <para>
    /// THE INCUMBENT'S TWO DEGRADATIONS ARE NOT REPRODUCED. Its sampler path fell back from anisotropic
    /// filtering to trilinear when the device lacked anisotropy, and dropped a non-zero LOD bias when the device
    /// lacked bias support, both because Metal has neither. Every Direct3D 11 device has both, so on this backend
    /// those branches are unreachable and carrying them would mean shipping a fallback nothing can enter.
    /// Decision G1 says so, and the two capabilities the dropped branches read are now CONSTANTS rather than
    /// device answers: <see cref="D3D11CapabilityRead.SamplerAnisotropy"/> and
    /// <see cref="D3D11CapabilityRead.SamplerLodBias"/>, both true, and both were asserted equal to the
    /// incumbent's by <c>NativeVsVeldridCapabilityParityTests</c> until that test went away with the incumbent in
    /// 18.0.0. That assertion is what turned "unreachable" from a claim here into something a test failed on, and
    /// what carries it now is the feature-level guarantee each of those two constants records.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class D3D11Sampler : IGpuSampler, ID3D11BindableViews
    {
        readonly DeviceLiveness _liveness;
        readonly bool _owns;

        /// <summary>
        /// Wrap a new sampler state built from <paramref name="description"/>. <paramref name="ownsSampler"/> is
        /// false for a device-owned shared sampler (the point and linear pair every device exposes), so handing
        /// one to a consumer that disposes it does not destroy the device's own. The device destroys its own pair
        /// through <see cref="DestroyShared"/> at teardown, which is the only thing that frees a non-owning one.
        /// </summary>
        internal D3D11Sampler(ID3D11Device device, DeviceLiveness liveness,
            in GpuSamplerDescription description, bool ownsSampler = true)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(liveness);

            _liveness = liveness;
            _owns = ownsSampler;
            SamplerState = CreateWindows(device, description);
        }

        /// <summary>The native sampler state.</summary>
        internal ID3D11SamplerState SamplerState { get; }

        /// <summary>Whether this wrapper destroys its sampler. False for the device's shared pair.</summary>
        internal bool OwnsSampler => _owns;

        /// <summary>True once disposed, whether or not anything native was released. A NON-OWNING wrapper never
        /// reaches it through <see cref="Dispose"/>: see that method.</summary>
        internal bool IsDisposed { get; private set; }

        // ---- ID3D11BindableViews: a sampler fills the 's' file and no other ----

        /// <inheritdoc/>
        object? ID3D11BindableViews.SamplerStateObject => SamplerState;

        /// <inheritdoc/>
        object? ID3D11BindableViews.ShaderResourceViewObject => null;

        /// <inheritdoc/>
        object? ID3D11BindableViews.UnorderedAccessViewObject => null;

        /// <inheritdoc/>
        object? ID3D11BindableViews.BufferObject => null;

        /// <summary>
        /// Release the sampler state, once, and never on a dead device, or do nothing AT ALL for the device's
        /// shared pair (https://github.com/APKiwiOrg/KhaozEngine/issues/506).
        /// <para>
        /// A NON-OWNING WRAPPER RETURNS WITHOUT EVEN MARKING ITSELF DISPOSED, which looks like an oversight and
        /// is the point. The device destroys its shared pair through <see cref="DestroyShared"/> at teardown, and
        /// a flag set here would make that call a no-op, so a consumer that disposed what
        /// <c>IGpuDevice.PointSampler</c> handed it would leak the device's own sampler until the device itself
        /// was released. This is the same rule the Vulkan backend applies to its own pair.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            if (!_owns || IsDisposed) return;
            IsDisposed = true;

            if (_liveness.IsDead) return;
            SamplerState.Dispose();
        }

        /// <summary>Destroy a DEVICE-OWNED shared sampler, which <see cref="Dispose"/> deliberately will not. The
        /// device's teardown calls this while the liveness token still says alive, which is the window in which
        /// releasing a child of the device is safe at all, and it is also what a failed construction unwinds
        /// through.</summary>
        internal void DestroyShared()
        {
            if (IsDisposed) return;
            IsDisposed = true;

            if (_liveness.IsDead) return;
            SamplerState.Dispose();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static ID3D11SamplerState CreateWindows(ID3D11Device device, in GpuSamplerDescription description)
        {
            var d = new SamplerDescription
            {
                AddressU = D3D11Formats.ToAddressMode(description.AddressModeU),
                AddressV = D3D11Formats.ToAddressMode(description.AddressModeV),
                AddressW = D3D11Formats.ToAddressMode(description.AddressModeW),
                Filter = D3D11Formats.ToFilter(description.Filter),
                MaxAnisotropy = (int)description.MaximumAnisotropy,
                MipLODBias = description.MipLodBias,
                ComparisonFunction = ComparisonFunction.Never,
                MinLOD = 0f,
                MaxLOD = uint.MaxValue,
                BorderColor = new Vortice.Mathematics.Color4(0f, 0f, 0f, 0f),
            };
            return device.CreateSamplerState(d);
        }
    }
}
