using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// Every feature bit this backend READS off a physical device, as plain data with no Silk.NET type in it, so
    /// the selection below is decided from values a test writes by hand. The same split
    /// <see cref="VulkanDeviceFacts"/> takes for the probe.
    /// <para>
    /// The three groups are deliberately visible in the ordering: the three REQUIRED bits first, then the four
    /// this backend ENABLES when they are there, then the five it only REPORTS.
    /// </para>
    /// </summary>
    /// <param name="DynamicRendering">1.3. REQUIRED: the whole rendering path is <c>vkCmdBeginRendering</c>.</param>
    /// <param name="Synchronization2">1.3. REQUIRED: every barrier is <c>vkCmdPipelineBarrier2</c>.</param>
    /// <param name="TimelineSemaphore">1.2. REQUIRED: one device timeline replaces per-submit fences.</param>
    /// <param name="SamplerAnisotropy">Enabled when present, and reported through
    /// <c>GpuCapabilities.SamplerAnisotropy</c> either way.</param>
    /// <param name="FillModeNonSolid">Enabled when present. Wireframe rasterization.</param>
    /// <param name="DepthClamp">Enabled when present. The shadow pass depends on it.</param>
    /// <param name="IndependentBlend">Enabled when present. Per-attachment blend state.</param>
    /// <param name="GeometryShader">READ only, never enabled: nothing in this engine uses one.</param>
    /// <param name="TessellationShader">READ only, never enabled.</param>
    /// <param name="MultiViewport">READ only, never enabled: the viewport is one, set dynamically.</param>
    /// <param name="DrawIndirectFirstInstance">READ only, never enabled.</param>
    /// <param name="ShaderFloat64">READ only, never enabled: no shipped shader takes a double.</param>
    internal readonly record struct VulkanFeatureSupport(
        bool DynamicRendering,
        bool Synchronization2,
        bool TimelineSemaphore,
        bool SamplerAnisotropy,
        bool FillModeNonSolid,
        bool DepthClamp,
        bool IndependentBlend,
        bool GeometryShader,
        bool TessellationShader,
        bool MultiViewport,
        bool DrawIndirectFirstInstance,
        bool ShaderFloat64);

    /// <summary>
    /// What <see cref="VulkanFeatureChain.Select"/> decided: which bits <c>vkCreateDevice</c> is asked for, and
    /// the ones that were only read. The four optional bits carry the DEVICE's answer, so a device without
    /// <c>samplerAnisotropy</c> produces a selection that does not ask for it and a
    /// <c>GpuCapabilities.SamplerAnisotropy</c> of false, rather than a failed device creation.
    /// </summary>
    /// <param name="Support">What the device reported, carried through so the capability read has one source.</param>
    /// <param name="EnabledFeatureNames">Every feature this selection asks <c>vkCreateDevice</c> for, in the order
    /// the design lists them. The INFO line prints it, and the test that the five read-only features are never
    /// requested reads it.</param>
    internal readonly record struct VulkanFeatureSelection(
        VulkanFeatureSupport Support,
        IReadOnlyList<string> EnabledFeatureNames)
    {
        /// <summary>Whether <c>samplerAnisotropy</c> is enabled on the created device, which is also
        /// <c>GpuCapabilities.SamplerAnisotropy</c>.</summary>
        internal bool SamplerAnisotropy => Support.SamplerAnisotropy;

        /// <summary>Whether <c>fillModeNonSolid</c> is enabled on the created device.</summary>
        internal bool FillModeNonSolid => Support.FillModeNonSolid;

        /// <summary>Whether <c>depthClamp</c> is enabled on the created device.</summary>
        internal bool DepthClamp => Support.DepthClamp;

        /// <summary>Whether <c>independentBlend</c> is enabled on the created device.</summary>
        internal bool IndependentBlend => Support.IndependentBlend;
    }

    /// <summary>
    /// DECISION V-N4: the features <c>vkCreateDevice</c> is asked for, chosen BY NAME rather than by handing the
    /// driver back everything it reported.
    /// <para>
    /// THE INCUMBENT HANDS <c>vkCreateDevice</c> THE ENTIRE SUPPORTED FEATURE STRUCT, and that is the defect this
    /// type exists to not reproduce. Two things follow from it, both bad. The engine's real dependencies become
    /// unknowable from the code, because every feature is enabled and none of them is named anywhere. And a
    /// future device missing one fails at an unrelated call site, on frame one, in a driver, instead of failing at
    /// device creation with the feature's name in the message.
    /// </para>
    /// <para>
    /// THREE ARE REQUIRED AND FOUR ARE OPTIONAL, and the difference is what a missing bit costs. The three
    /// required ones are the backend's architecture (there is no <c>VkRenderPass</c> to fall back to, no
    /// <c>vkCmdPipelineBarrier</c> path beside the barrier-2 one, and no per-submit fence beside the timeline), so
    /// a device without one cannot run this backend at all and says so by name. The four optional ones each
    /// degrade something a renderer can live without, so a device without one gets a device that does not ask for
    /// it and a capability that reads false. All three required bits are MANDATORY on a conformant 1.3 device, so
    /// on real hardware this check is a formality that fires on a 1.2 machine rather than a gate anything trips.
    /// </para>
    /// <para>
    /// FIVE MORE ARE READ AND NEVER ENABLED (<c>geometryShader</c>, <c>tessellationShader</c>,
    /// <c>multiViewport</c>, <c>drawIndirectFirstInstance</c>, <c>shaderFloat64</c>). Nothing in this engine uses
    /// them, and enabling a feature nobody uses costs a real driver something on some hardware while making the
    /// dependency list a lie. They are carried on <see cref="VulkanFeatureSupport"/> because reporting a
    /// capability and depending on one are different things.
    /// </para>
    /// <para>
    /// Everything here is pure, so the whole selection runs under <c>dotnet test</c> on a machine with no Vulkan
    /// loader: the required-missing throw, the optional-missing degrade, and the promise that the read-only five
    /// never reach <c>vkCreateDevice</c>.
    /// </para>
    /// </summary>
    internal static class VulkanFeatureChain
    {
        /// <summary><c>VkPhysicalDeviceVulkan13Features.dynamicRendering</c>, as it is spelled in the message a
        /// rejected device produces and in <see cref="VulkanFeatureSelection.EnabledFeatureNames"/>.</summary>
        internal const string DynamicRendering = "dynamicRendering";

        /// <summary><c>VkPhysicalDeviceVulkan13Features.synchronization2</c>.</summary>
        internal const string Synchronization2 = "synchronization2";

        /// <summary><c>VkPhysicalDeviceVulkan12Features.timelineSemaphore</c>.</summary>
        internal const string TimelineSemaphore = "timelineSemaphore";

        /// <summary><c>VkPhysicalDeviceFeatures.samplerAnisotropy</c>.</summary>
        internal const string SamplerAnisotropy = "samplerAnisotropy";

        /// <summary><c>VkPhysicalDeviceFeatures.fillModeNonSolid</c>.</summary>
        internal const string FillModeNonSolid = "fillModeNonSolid";

        /// <summary><c>VkPhysicalDeviceFeatures.depthClamp</c>.</summary>
        internal const string DepthClamp = "depthClamp";

        /// <summary><c>VkPhysicalDeviceFeatures.independentBlend</c>.</summary>
        internal const string IndependentBlend = "independentBlend";

        /// <summary>
        /// The features to ask <c>vkCreateDevice</c> for, given what <paramref name="support"/> says the device
        /// reported.
        /// </summary>
        /// <param name="support">The bits read off the physical device through the chained feature query.</param>
        /// <param name="deviceName">The device's own name, for the message a rejection produces. A rejection that
        /// does not say WHICH device it is about is unreadable on a two-device machine.</param>
        /// <exception cref="NotSupportedException">A REQUIRED feature is missing. The message names the feature
        /// and says what depends on it, which is the whole reason the enable is by name.</exception>
        internal static VulkanFeatureSelection Select(in VulkanFeatureSupport support, string deviceName)
        {
            string device = string.IsNullOrWhiteSpace(deviceName) ? "this device" : $"'{deviceName}'";

            if (!support.DynamicRendering) throw Missing(device, DynamicRendering,
                "the whole rendering path is vkCmdBeginRendering, and there is no VkRenderPass and no "
                + "VkFramebuffer anywhere in this backend to fall back to");

            if (!support.Synchronization2) throw Missing(device, Synchronization2,
                "every barrier the layout tracker emits is vkCmdPipelineBarrier2 with explicit stage and access "
                + "masks, and there is no barrier-1 path beside it");

            if (!support.TimelineSemaphore) throw Missing(device, TimelineSemaphore,
                "the device's one monotonic timeline is a timeline semaphore: every submit signals it, every "
                + "fence is a target on it, and WaitForIdle is a host wait on it");

            // The order here is the order the design lists them, and it is the order the INFO line prints, so a
            // session log and the design read the same way. Required first, because a reader scanning the line
            // for what a device is missing is looking for one of those three.
            var enabled = new List<string>(7) { DynamicRendering, Synchronization2, TimelineSemaphore };
            if (support.SamplerAnisotropy) enabled.Add(SamplerAnisotropy);
            if (support.FillModeNonSolid) enabled.Add(FillModeNonSolid);
            if (support.DepthClamp) enabled.Add(DepthClamp);
            if (support.IndependentBlend) enabled.Add(IndependentBlend);

            return new VulkanFeatureSelection(support, enabled);
        }

        /// <summary>The INFO line naming exactly what the device was created with, and what was asked for and not
        /// there. It exists because "enabled selectively by name" is only checkable from outside if the names are
        /// somewhere a session log can be read for them.</summary>
        internal static string Describe(in VulkanFeatureSelection selection)
        {
            var absent = new List<string>(4);
            if (!selection.Support.SamplerAnisotropy) absent.Add(SamplerAnisotropy);
            if (!selection.Support.FillModeNonSolid) absent.Add(FillModeNonSolid);
            if (!selection.Support.DepthClamp) absent.Add(DepthClamp);
            if (!selection.Support.IndependentBlend) absent.Add(IndependentBlend);

            string line = "Vulkan device features enabled by name: "
                + string.Join(", ", selection.EnabledFeatureNames) + ".";

            return absent.Count == 0
                ? line
                : line + " Not offered by this device and therefore not enabled: " + string.Join(", ", absent)
                    + ". Every capability that depends on one of those reads false for this session.";
        }

        static NotSupportedException Missing(string device, string feature, string why)
            => new($"The native Vulkan backend cannot create a device on {device}: it reports no {feature}, and "
                + $"{why}. This feature is MANDATORY on a conformant Vulkan 1.3 device, so a device reporting the "
                + "1.3 version floor and not this bit is below spec. There is no second Vulkan path to fall back "
                + "to on this machine: GpuBackendKind.Vulkan named the Veldrid backend and was retired in "
                + "18.0.0.");
    }
}
