using System;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// ONE <c>MTLSamplerDescriptor</c>'s worth of decisions in plain values, so the whole sampler mapping is a
    /// pure function of the seam's description and is driven by <c>[Fact]</c>s with no device.
    /// </summary>
    /// <param name="AddressS">Addressing on S, which is the seam's U.</param>
    /// <param name="AddressT">Addressing on T, the seam's V.</param>
    /// <param name="AddressR">Addressing on R, the seam's W.</param>
    /// <param name="Filters">The min, mag and mip filters.</param>
    /// <param name="MaxAnisotropy">What reaches <c>-setMaxAnisotropy:</c>, already raised to at least 1.</param>
    /// <param name="BorderColor">The border colour, transparent black when an address mode is
    /// <see cref="MTLSamplerAddressMode.ClampToBorderColor"/> and NULL otherwise. Null means the descriptor's
    /// <c>-setBorderColor:</c> is not sent at all, which is the difference between a property the device never
    /// hears about and one it is asked to accept. See <see cref="MetalSamplerPolicy"/>.</param>
    /// <param name="LodMinClamp">Always 0.</param>
    /// <param name="LodMaxClamp">Always <c>uint.MaxValue</c> as a float.</param>
    internal readonly record struct MetalSamplerSpec(
        MTLSamplerAddressMode AddressS,
        MTLSamplerAddressMode AddressT,
        MTLSamplerAddressMode AddressR,
        MetalFilterSelection Filters,
        nuint MaxAnisotropy,
        MTLSamplerBorderColor? BorderColor,
        float LodMinClamp,
        float LodMaxClamp);

    /// <summary>
    /// THE SAMPLER MAPPING, reproduced from what the engine's Veldrid path plus <c>Veldrid.MTL.MTLSampler</c>
    /// really created between them.
    ///
    /// <para><b>FOUR VALUES THE SEAM DOES NOT EXPOSE ARE HARDCODED, because the incumbent hardcoded them and the
    /// committed goldens were baked through them.</b> No comparison function (the shadow path does manual PCF and
    /// never asks for a comparison sampler), a minimum LOD of 0, a maximum LOD of <c>uint.MaxValue</c>, and a
    /// transparent-black border colour. All four came from the engine's own Veldrid path, which built every
    /// sampler as <c>new SamplerDescription(u, v, w, filter, null, maxAniso, 0, uint.MaxValue, lodBias,
    /// SamplerBorderColor.TransparentBlack)</c>. Changing one would move pixels.</para>
    ///
    /// <para><b>THE INCUMBENT'S COMPARE-FUNCTION CONDITIONAL IS RESOLVED RATHER THAN REPRODUCED, and its
    /// border-colour one is DIVERGED FROM.</b> <c>MTLSampler</c> writes the compare function only when the seam
    /// supplied a comparison kind, and the engine's Veldrid path passed <c>null</c> at its ONE call site, so that
    /// arm is NEVER taken and no compare function is written here at all. Carrying a branch whose condition is
    /// constant would be reproducing a branch instead of a behaviour, and it would be a branch no test could
    /// reach either way. The border colour is the other half of row 6's "both reachable conditionals" and it no
    /// longer reads that way: see the next paragraph.</para>
    ///
    /// <para><b>THE BORDER COLOUR IS A DEVICE FACT ON macOS, WHICH THE INCUMBENT DOES NOT ASK ABOUT.</b>
    /// <c>MTLSampler</c> writes it whenever <c>gd.MetalFeatures.IsMacOS</c>, which is true on every machine this
    /// backend can run on, so the incumbent armed border colours on a device that may not have them. A
    /// virtualized GPU is exactly that device: the hosted <c>macos-26</c> runner's Apple Paravirtual device
    /// aborts under the armed debug layer with <c>MTLSamplerBorderColorTransparentBlack is not supported on this
    /// device</c>, and without the layer armed it would silently sample something other than a border. No shipped
    /// engine sampler asks for <see cref="GpuSamplerAddress.Border"/>, which is why the incumbent's arm had never
    /// been visible: no golden uses a border sampler, so the wrong sample has nowhere to show up. This backend
    /// diverges twice rather than reproducing that. It writes the property ONLY when an address mode is
    /// <see cref="MTLSamplerAddressMode.ClampToBorderColor"/>, so a Wrap, Mirror or Clamp sampler never sends
    /// <c>-setBorderColor:</c> at all and the shared WRAP pair is untouched by construction. And a Border-mode
    /// sampler on a device that does not support border colours is REFUSED BY NAME
    /// (<see cref="MissingBorderColorSupport"/>) rather than created wrong.</para>
    ///
    /// <para><b>THE SUPPORT ANSWER IS <c>MTLGPUFamilyMac2</c> AND A REAL ADAPTER, READ ONCE AT DEVICE
    /// CREATION.</b> Metal exposes no <c>supportsBorderColor</c> selector, so the family is the documented
    /// question: <c>MTLSamplerAddressModeClampToBorderColor</c> and <c>MTLSamplerBorderColor</c> are Mac-family
    /// features, and every Mac GPU on a macOS this engine supports answers <c>Mac2</c>. The family alone is NOT
    /// sufficient, and that is a measurement rather than a caution: the hosted CI runner's Apple Paravirtual
    /// device ANSWERS <c>Mac2</c> TRUE and still aborts border-colour sampler creation under the armed debug
    /// layer (run 31463608951, the metal-native leg's second run, identical abort after the family gate landed).
    /// So the reading also requires the adapter not to be a virtualised one, by the same name test
    /// <c>GpuFactAttribute.VirtualGpuSkipReason</c> already uses for the RequiresRealGpu skip. A FUNCTIONAL probe
    /// is not available as an alternative: the way to ask a device directly is to build a border sampler and see
    /// whether it comes back nil, and under <c>MTL_DEBUG_LAYER=1</c> that is the abort rather than the
    /// nil.</para>
    ///
    /// <para><b>THE ANISOTROPY DEGRADATION IS UNREACHABLE ON METAL, exactly as it was on Direct3D 11 and unlike
    /// on Vulkan.</b> The engine's Veldrid path fell back from anisotropic filtering to trilinear when the device
    /// reports no <c>SamplerAnisotropy</c>, and <c>MTLGraphicsDevice</c> constructs its
    /// <c>GraphicsDeviceFeatures</c> with <c>samplerAnisotropy: true</c> unconditionally, so no Metal device takes
    /// it. This backend's capability read answers true for the same reason and section 14 requires the two to
    /// agree. A branch nothing can reach is a branch nothing can test, so it is written down here instead of
    /// coded.</para>
    ///
    /// <para><b>THE LOD BIAS IS DROPPED, AND THAT IS THE ONE PLACE THE SEAM LOSES SOMETHING ON THIS BACKEND.</b>
    /// <see cref="GpuSamplerDescription.MipLodBias"/> has no <c>MTLSamplerDescriptor</c> field at all, which is
    /// why <c>GpuCapabilities.SamplerLodBias</c> is the one capability that differs from BOTH other native
    /// backends. The engine's Veldrid path already zeroed the bias on a device that reports no support, so a
    /// non-zero bias reaches neither implementation and the two agree by construction rather than by accident.
    /// The seam's own doc comment already says it is a no-op on Metal.</para>
    /// </summary>
    internal static class MetalSamplerPolicy
    {
        /// <summary>The minimum LOD every sampler on this backend is created with.</summary>
        internal const float MinLod = 0f;

        /// <summary>The maximum LOD every sampler on this backend is created with: <c>uint.MaxValue</c> converted
        /// to a float, which is the value the engine's Veldrid path passed and which arrives at
        /// <c>-setLodMaxClamp:</c> through the same widening there.</summary>
        internal const float MaxLod = uint.MaxValue;

        /// <summary>The border colour every sampler that has one is created with. The seam exposes no choice, so
        /// this names the one value rather than offering one.</summary>
        internal const MTLSamplerBorderColor Border = MTLSamplerBorderColor.TransparentBlack;

        /// <summary>The spec for <paramref name="description"/>.</summary>
        internal static MetalSamplerSpec For(in GpuSamplerDescription description)
        {
            MTLSamplerAddressMode s = MetalFormats.ToAddressMode(description.AddressModeU);
            MTLSamplerAddressMode t = MetalFormats.ToAddressMode(description.AddressModeV);
            MTLSamplerAddressMode r = MetalFormats.ToAddressMode(description.AddressModeW);

            return new MetalSamplerSpec(
                s, t, r,
                MetalFormats.ToFilterSelection(description.Filter),
                // RAISED TO AT LEAST 1, which is Math.Max(1, MaximumAnisotropy) in the incumbent. Metal rejects
                // zero, and the seam documents 0 as "keep the historical behaviour" rather than as a value.
                description.MaximumAnisotropy < 1 ? 1 : description.MaximumAnisotropy,
                // NULL UNLESS THE SAMPLER ACTUALLY BORDERS, which is the divergence from the incumbent the class
                // comment states. A null here is "do not send -setBorderColor: at all".
                UsesBorderColor(s, t, r) ? Border : null,
                MinLod,
                MaxLod);
        }

        /// <summary>Whether a sampler with these three address modes reads a border colour at all.</summary>
        internal static bool UsesBorderColor(MTLSamplerAddressMode s, MTLSamplerAddressMode t,
            MTLSamplerAddressMode r)
            => s == MTLSamplerAddressMode.ClampToBorderColor
                || t == MTLSamplerAddressMode.ClampToBorderColor
                || r == MTLSamplerAddressMode.ClampToBorderColor;

        /// <summary>Whether a device described by <paramref name="facts"/> supports border colours at all: the
        /// <c>MTLGPUFamilyMac2</c> answer the probe already reads AND a non-virtualised adapter name, given a
        /// name here so the reading is one place rather than a comparison spelled out at every caller. The
        /// adapter half exists because the paravirtual device reports the family and then aborts the creation,
        /// per the class comment's measurement.</summary>
        internal static bool DeviceSupportsBorderColor(in MetalDeviceFacts facts)
            => facts.SupportsMac2 && !IsVirtualisedAdapter(facts.DeviceName);

        /// <summary>The same name test <c>GpuFactAttribute.VirtualGpuSkipReason</c> applies for RequiresRealGpu,
        /// duplicated here deliberately: the test-support assembly references the engine and not the other way
        /// around, so the engine cannot reach that member, and the two sites cross-reference each other so a
        /// change visits both.</summary>
        internal static bool IsVirtualisedAdapter(string deviceName)
            => deviceName.Contains("Paravirtual", StringComparison.OrdinalIgnoreCase)
                || deviceName.Contains("Virtual", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Why <paramref name="description"/> cannot be created on a device whose border-colour support is
        /// <paramref name="deviceSupportsBorderColor"/>, or null when nothing stops it. Null is the only yes.
        /// <para>
        /// NAMED REFUSAL OVER SILENT WRONGNESS, which is this backend's standing posture and which has a sharper
        /// edge here than usual: the alternative is not a wrong pixel but a PROCESS ABORT. The debug layer asserts
        /// on a border sampler the device cannot honour, so a caller that reaches this on a virtualized GPU gets
        /// either an exception it can read or a test host that dies with no stack. Device-free by construction, so
        /// both answers are driven from fabricated support on every leg.
        /// </para>
        /// </summary>
        internal static string? MissingBorderColorSupport(in GpuSamplerDescription description,
            bool deviceSupportsBorderColor)
        {
            if (deviceSupportsBorderColor) return null;

            MetalSamplerSpec spec = For(description);
            if (!UsesBorderColor(spec.AddressS, spec.AddressT, spec.AddressR)) return null;

            return "this Metal device does not support sampler border colours (it answers no to supportsFamily: "
                + "for MTLGPUFamilyMac2, or it is a virtualised adapter that reports the family and then aborts "
                + "the creation, which the Apple Paravirtual device on a hosted macOS runner does), and the "
                + "sampler asks for GpuSamplerAddress.Border on at least one axis "
                + "(U: " + description.AddressModeU + ", V: " + description.AddressModeV
                + ", W: " + description.AddressModeW
                + "). Use Clamp for the same edge behaviour without a border colour, or Wrap or Mirror. Creating "
                + "it anyway is not the safe option: -newSamplerStateWithDescriptor: aborts the process under "
                + "MTL_DEBUG_LAYER=1 and samples something other than a border without it";
        }
    }
}
