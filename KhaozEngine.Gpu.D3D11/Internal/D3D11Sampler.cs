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
    /// FOUR VALUES ARE HARDCODED, and they are hardcoded because the incumbent hardcodes them and the committed
    /// goldens were baked through them. No comparison function (the shadow path does manual PCF and never asks for
    /// a comparison sampler), a minimum LOD of 0, a maximum LOD of <c>uint.MaxValue</c> which Direct3D clamps to
    /// the real chain, and a transparent-black border colour. The seam exposes none of the four, so a caller
    /// cannot ask for anything else, and changing one would move pixels.
    /// </para>
    /// <para>
    /// THE INCUMBENT'S TWO DEGRADATIONS ARE NOT REPRODUCED. Its sampler path falls back from anisotropic filtering
    /// to trilinear when the device lacks anisotropy, and drops a non-zero LOD bias when the device lacks bias
    /// support, both because Metal has neither. Every Direct3D 11 device has both, so on this backend those
    /// branches are unreachable and carrying them would mean shipping a fallback nothing can enter. Decision G1
    /// says so, and the two capabilities the dropped branches read are now CONSTANTS rather than device answers:
    /// <see cref="D3D11CapabilityRead.SamplerAnisotropy"/> and
    /// <see cref="D3D11CapabilityRead.SamplerLodBias"/>, both true, both asserted equal to the incumbent's by
    /// <c>NativeVsVeldridCapabilityParityTests</c>. That is what turns "unreachable" from a claim here into
    /// something a test fails on.
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
        /// one to a consumer that disposes it does not destroy the device's own.
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

        /// <summary>True once disposed, whether or not anything native was released.</summary>
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

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            if (!_owns || _liveness.IsDead) return;
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
