using System;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Decision V-N4: the features <c>vkCreateDevice</c> is asked for, chosen BY NAME rather than by handing the
    /// driver back everything it reported.
    /// <para>
    /// The incumbent handed <c>vkCreateDevice</c> the entire supported feature struct, and these rows are what
    /// stops that shape coming back. Two things follow from it, both bad: the engine's real dependencies become
    /// unknowable from the code, and a device missing one fails at an unrelated call site on frame one instead of
    /// at creation with the feature's name in the message.
    /// </para>
    /// <para>
    /// All device-free, over fabricated feature data, which is what lets "a device without dynamic rendering is
    /// refused by name" be asserted on a machine with no Vulkan loader at all.
    /// </para>
    /// </summary>
    public sealed class VulkanFeatureChainTests
    {
        static VulkanFeatureSupport Everything => new(
            DynamicRendering: true, Synchronization2: true, TimelineSemaphore: true,
            SamplerAnisotropy: true, FillModeNonSolid: true, DepthClamp: true, IndependentBlend: true,
            GeometryShader: true, TessellationShader: true, MultiViewport: true,
            DrawIndirectFirstInstance: true, ShaderFloat64: true);

        /// <summary>
        /// A conformant 1.3 device gets exactly the SEVEN named features and nothing else. The count is asserted
        /// as well as the membership, because the failure this row exists to prevent is an eighth feature quietly
        /// joining the list.
        /// </summary>
        [Fact]
        public void ACapableDevice_EnablesExactlySevenFeaturesByName()
        {
            VulkanFeatureSelection selection = VulkanFeatureChain.Select(Everything, "llvmpipe");

            Assert.Equal(
                new[]
                {
                    "dynamicRendering", "synchronization2", "timelineSemaphore",
                    "samplerAnisotropy", "fillModeNonSolid", "depthClamp", "independentBlend",
                },
                selection.EnabledFeatureNames);
        }

        /// <summary>
        /// THE FIVE READ-ONLY FEATURES NEVER REACH <c>vkCreateDevice</c>, even on a device that offers all of
        /// them. Nothing in this engine uses a geometry shader, a tessellation shader, multiple viewports,
        /// <c>drawIndirectFirstInstance</c> or a double in a shader, and enabling a feature nobody uses costs a
        /// real driver something on some hardware while making the dependency list a lie.
        /// </summary>
        [Theory]
        [InlineData("geometryShader")]
        [InlineData("tessellationShader")]
        [InlineData("multiViewport")]
        [InlineData("drawIndirectFirstInstance")]
        [InlineData("shaderFloat64")]
        public void TheReadOnlyFeatures_AreNeverEnabled(string feature)
        {
            VulkanFeatureSelection selection = VulkanFeatureChain.Select(Everything, "llvmpipe");

            Assert.DoesNotContain(feature, selection.EnabledFeatureNames);
            // Still READ, because reporting a capability and depending on one are different things.
            Assert.True(selection.Support.GeometryShader);
        }

        /// <summary>
        /// A MISSING REQUIRED FEATURE IS REFUSED BY NAME, with the device named too, and the message says what
        /// depends on it. That sentence is the whole payoff of enabling by name: without it the failure lands on
        /// frame one at an unrelated call site inside a driver.
        /// </summary>
        [Theory]
        [InlineData("dynamicRendering")]
        [InlineData("synchronization2")]
        [InlineData("timelineSemaphore")]
        public void AMissingRequiredFeature_IsRefusedByName(string missing)
        {
            VulkanFeatureSupport support = Everything with
            {
                DynamicRendering = missing != "dynamicRendering",
                Synchronization2 = missing != "synchronization2",
                TimelineSemaphore = missing != "timelineSemaphore",
            };

            NotSupportedException ex = Assert.Throws<NotSupportedException>(
                () => VulkanFeatureChain.Select(support, "Old Radeon"));

            Assert.Contains(missing, ex.Message, StringComparison.Ordinal);
            Assert.Contains("Old Radeon", ex.Message, StringComparison.Ordinal);
            // And it points at the working path rather than leaving the reader stuck.
            Assert.Contains("GpuBackendKind.Vulkan", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The refusal names the FIRST missing requirement rather than three at once, in the order the design
        /// lists them. A message naming three missing features on a 1.2 device is the same failure phrased three
        /// times, and the reader has to pick which one to act on.
        /// </summary>
        [Fact]
        public void ADeviceMissingAllThree_IsRefusedForTheFirst()
        {
            var support = new VulkanFeatureSupport(
                DynamicRendering: false, Synchronization2: false, TimelineSemaphore: false,
                SamplerAnisotropy: true, FillModeNonSolid: true, DepthClamp: true, IndependentBlend: true,
                GeometryShader: false, TessellationShader: false, MultiViewport: false,
                DrawIndirectFirstInstance: false, ShaderFloat64: false);

            NotSupportedException ex = Assert.Throws<NotSupportedException>(
                () => VulkanFeatureChain.Select(support, "Old Radeon"));

            Assert.Contains("dynamicRendering", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("synchronization2", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// A MISSING OPTIONAL FEATURE DEGRADES RATHER THAN REFUSING: it is not asked for, and the capability that
        /// depends on it reads false. That is the difference the required-versus-optional split is about, and
        /// <c>samplerAnisotropy</c> is the one that reaches <c>GpuCapabilities</c> directly.
        /// </summary>
        [Fact]
        public void AMissingOptionalFeature_IsNotAskedForAndReadsAsACapabilityOff()
        {
            VulkanFeatureSupport support = Everything with { SamplerAnisotropy = false };

            VulkanFeatureSelection selection = VulkanFeatureChain.Select(support, "some driver");

            Assert.DoesNotContain("samplerAnisotropy", selection.EnabledFeatureNames);
            Assert.False(selection.SamplerAnisotropy);
            // The other three are unaffected: one missing optional does not turn the rest off.
            Assert.True(selection.FillModeNonSolid);
            Assert.True(selection.DepthClamp);
            Assert.True(selection.IndependentBlend);
        }

        /// <summary>
        /// A device with none of the four optional features still creates, with exactly the three required ones.
        /// The lower bound of the whole policy, and the shape a minimal conformant 1.3 implementation presents.
        /// </summary>
        [Fact]
        public void ADeviceWithNoOptionalFeatures_StillCreates()
        {
            var support = new VulkanFeatureSupport(
                DynamicRendering: true, Synchronization2: true, TimelineSemaphore: true,
                SamplerAnisotropy: false, FillModeNonSolid: false, DepthClamp: false, IndependentBlend: false,
                GeometryShader: false, TessellationShader: false, MultiViewport: false,
                DrawIndirectFirstInstance: false, ShaderFloat64: false);

            VulkanFeatureSelection selection = VulkanFeatureChain.Select(support, "minimal 1.3 device");

            Assert.Equal(
                new[] { "dynamicRendering", "synchronization2", "timelineSemaphore" },
                selection.EnabledFeatureNames);
        }

        /// <summary>
        /// The INFO line names what was enabled AND what was asked for and not there. It exists because "enabled
        /// selectively by name" is only checkable from outside if the names are somewhere a session log can be
        /// read for them.
        /// </summary>
        [Fact]
        public void TheDescription_NamesWhatWasEnabledAndWhatWasAbsent()
        {
            VulkanFeatureSupport support = Everything with { DepthClamp = false };

            string described = VulkanFeatureChain.Describe(VulkanFeatureChain.Select(support, "a device"));

            Assert.Contains("dynamicRendering", described, StringComparison.Ordinal);
            Assert.Contains("Not offered by this device", described, StringComparison.Ordinal);
            Assert.Contains("depthClamp", described, StringComparison.Ordinal);
        }

        /// <summary>A device that offers everything produces a description with no absent clause, so the ordinary
        /// line stays short enough to read.</summary>
        [Fact]
        public void TheDescription_SaysNothingAboutAbsenceWhenNothingIsAbsent()
        {
            string described = VulkanFeatureChain.Describe(VulkanFeatureChain.Select(Everything, "a device"));

            Assert.DoesNotContain("Not offered", described, StringComparison.Ordinal);
        }

        /// <summary>An unnamed device still produces a readable refusal, because a driver that reports nothing
        /// usable is exactly the driver most likely to be missing a feature.</summary>
        [Fact]
        public void AnUnnamedDevice_StillProducesAReadableRefusal()
        {
            VulkanFeatureSupport support = Everything with { TimelineSemaphore = false };

            NotSupportedException ex = Assert.Throws<NotSupportedException>(
                () => VulkanFeatureChain.Select(support, "   "));

            Assert.Contains("this device", ex.Message, StringComparison.Ordinal);
        }
    }
}
