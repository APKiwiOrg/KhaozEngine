using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE DERIVATIONS ROW 9 MAKES BEFORE IT TOUCHES A DRIVER: which usage bits a resource gets, which eager views
    /// it is created with and over what range (V-M11), what its canonical resting layout is (V-F7), whether it is
    /// cleared at creation (V-M10), and what a sampler is really built with (section 14). All of it is a pure
    /// function of the seam's own description, so all of it runs on a machine with no Vulkan loader.
    /// </summary>
    public sealed class VulkanViewPolicyTests
    {
        /// <summary>
        /// THE EAGER VIEW SET IS DECIDED BY THE USAGE BITS AND BY NOTHING ELSE (V-M11), and the counts here are
        /// the whole of decision V-M11's bound: a full-chain sampled view if the texture is sampled or generates
        /// mips, an attachment view if it is a render target or a depth target, a storage view if it is a storage
        /// image, and nothing at all for a staging texture, which has no image.
        /// </summary>
        [Theory]
        [InlineData(GpuTextureUsage.Sampled, true, false, false)]
        [InlineData(GpuTextureUsage.RenderTarget, false, true, false)]
        [InlineData(GpuTextureUsage.DepthStencil, false, true, false)]
        [InlineData(GpuTextureUsage.Storage, false, false, true)]
        [InlineData(GpuTextureUsage.Sampled | GpuTextureUsage.RenderTarget, true, true, false)]
        [InlineData(GpuTextureUsage.Sampled | GpuTextureUsage.Storage, true, false, true)]
        [InlineData(GpuTextureUsage.Staging, false, false, false)]
        public void TheEagerViewSet_FollowsTheDeclaredUsageBits(GpuTextureUsage usage, bool sampled,
            bool attachment, bool storage)
        {
            VulkanTextureViewPlan plan = VulkanViewPolicy.ForTexture(usage);

            Assert.Equal(sampled, plan.SampledView);
            Assert.Equal(attachment, plan.AttachmentView);
            Assert.Equal(storage, plan.StorageView);
        }

        /// <summary>
        /// A MIP-GENERATING TEXTURE EARNS THE SAMPLED VIEW WITHOUT ASKING TO BE SAMPLED, because a generated chain
        /// nothing can sample is a chain nobody asked for, AND IT EARNS THE SAMPLED USAGE BIT WITH IT.
        /// <c>vkCreateImageView</c> refuses a view over an image whose usage names no view-compatible use at all
        /// (<c>VUID-VkImageViewCreateInfo-image-04441</c>), and the two transfer bits are not among them, so a
        /// plan that created the view without the bit described an image whose one view cannot be made.
        /// <para>
        /// It earns no ATTACHMENT bit, which is where this backend and the Direct3D 11 one differ: there
        /// <c>GenerateMips</c> is defined through a shader resource view and forces the render-target bind flag
        /// onto the resource.
        /// </para>
        /// </summary>
        [Fact]
        public void AMipGeneratingTexture_EarnsTheSampledViewAndTheSampledBit()
        {
            VulkanTextureViewPlan plan = VulkanViewPolicy.ForTexture(GpuTextureUsage.GenerateMipmaps);

            Assert.True(plan.SampledView);
            Assert.False(plan.AttachmentView);
            Assert.Equal(
                VulkanImageUsage.TransferSrc | VulkanImageUsage.TransferDst | VulkanImageUsage.Sampled,
                plan.Usage);

            // THE INVARIANT BEHIND IT, over every usage shape: a plan that creates the sampled view always carries
            // the bit that makes the view creatable.
            foreach (GpuTextureUsage usage in new[]
                     {
                         GpuTextureUsage.GenerateMipmaps,
                         GpuTextureUsage.Sampled,
                         GpuTextureUsage.GenerateMipmaps | GpuTextureUsage.RenderTarget,
                         GpuTextureUsage.GenerateMipmaps | GpuTextureUsage.Storage,
                     })
            {
                VulkanTextureViewPlan each = VulkanViewPolicy.ForTexture(usage);
                Assert.True(!each.SampledView || (each.Usage & VulkanImageUsage.Sampled) != 0);
            }
        }

        /// <summary>
        /// THE CANONICAL RESTING LAYOUT (V-F7), in the one order the design states it: sampled wins outright, then
        /// storage, then the attachment reading. A texture that is BOTH a render target and sampled rests in
        /// <c>SHADER_READ_ONLY_OPTIMAL</c>, which is every intermediate in the post chain, and a list that renders
        /// into it transitions and restores. Row 14 consumes this.
        /// <para>
        /// A <c>[Fact]</c> with the table in the body rather than a <c>[Theory]</c>, because a test method has to
        /// be public and <c>VulkanRestingLayout</c> is internal, so it cannot appear in a signature. Same shape
        /// <c>VulkanMemoryTypeSelectionTests</c> settled on for the same reason.
        /// </para>
        /// </summary>
        [Fact]
        public void TheRestingLayout_IsSampledThenStorageThenTheAttachmentReading()
        {
            (GpuTextureUsage Usage, VulkanRestingLayout Resting)[] expected =
            [
                (GpuTextureUsage.Sampled, VulkanRestingLayout.ShaderReadOnlyOptimal),
                (GpuTextureUsage.Sampled | GpuTextureUsage.RenderTarget,
                    VulkanRestingLayout.ShaderReadOnlyOptimal),
                (GpuTextureUsage.Sampled | GpuTextureUsage.Storage, VulkanRestingLayout.ShaderReadOnlyOptimal),
                (GpuTextureUsage.Storage, VulkanRestingLayout.General),
                (GpuTextureUsage.RenderTarget, VulkanRestingLayout.ColorAttachmentOptimal),
                (GpuTextureUsage.DepthStencil, VulkanRestingLayout.DepthStencilAttachmentOptimal),
                (GpuTextureUsage.Staging, VulkanRestingLayout.None),
            ];

            foreach ((GpuTextureUsage usage, VulkanRestingLayout resting) in expected)
            {
                Assert.Equal(resting, VulkanViewPolicy.ForTexture(usage).Resting);
            }
        }

        /// <summary>
        /// EVERY IMAGE CARRIES BOTH TRANSFER BITS, reproduced from the incumbent's
        /// <c>VkFormats.VdToVkTextureUsage</c>, which opens with exactly those two. It is what makes a readback, an
        /// upload, a creation-time clear and a mip blit legal on any texture without the seam having to declare an
        /// intention it has no word for.
        /// </summary>
        [Theory]
        [InlineData(GpuTextureUsage.Sampled)]
        [InlineData(GpuTextureUsage.RenderTarget)]
        [InlineData(GpuTextureUsage.DepthStencil)]
        [InlineData(GpuTextureUsage.Storage)]
        public void EveryImage_CarriesBothTransferBits(GpuTextureUsage usage)
        {
            VulkanImageUsage image = VulkanViewPolicy.ForTexture(usage).Usage;

            Assert.True((image & VulkanImageUsage.TransferSrc) != 0);
            Assert.True((image & VulkanImageUsage.TransferDst) != 0);
        }

        /// <summary>
        /// THE CREATION-TIME CLEAR IS PRESERVED AND ITS TWO ARMS ARE EXCLUSIVE (V-M10), reproducing
        /// <c>VkTexture.ClearIfRenderTarget</c>'s <c>if</c> and <c>else if</c>: a colour target is cleared to
        /// transparent black, a depth target that is not also a colour one is cleared to depth 0, and everything
        /// else is cleared not at all. Dropping the clear would change what a render target reads before anything
        /// writes it, and undefined contents are not stable across runs while the goldens require stability.
        /// </summary>
        [Theory]
        [InlineData(GpuTextureUsage.RenderTarget, true, false)]
        [InlineData(GpuTextureUsage.DepthStencil, false, true)]
        [InlineData(GpuTextureUsage.RenderTarget | GpuTextureUsage.DepthStencil, true, false)]
        [InlineData(GpuTextureUsage.Sampled, false, false)]
        [InlineData(GpuTextureUsage.Storage, false, false)]
        public void TheCreationTimeClear_HasTwoExclusiveArms(GpuTextureUsage usage, bool colour, bool depth)
        {
            VulkanTextureViewPlan plan = VulkanViewPolicy.ForTexture(usage);

            Assert.Equal(colour, plan.ClearColorAtCreation);
            Assert.Equal(depth, plan.ClearDepthAtCreation);
            Assert.Equal(colour || depth, plan.ClearsAtCreation);
        }

        /// <summary>
        /// A STAGING TEXTURE IS REFUSED IN COMBINATION WITH ANYTHING ELSE, rather than silently dropping the other
        /// bits. On this backend a staging texture is a <c>VkBuffer</c> with a software subresource layout and no
        /// image at all (V-C7), so there is no bindable resource for the other usage to describe.
        /// </summary>
        [Fact]
        public void AStagingTextureCombinedWithAnythingElse_IsRefused()
        {
            Assert.Throws<ArgumentException>(() =>
                VulkanViewPolicy.ForTexture(GpuTextureUsage.Staging | GpuTextureUsage.Sampled));

            // The bit alone is what every staging texture the engine creates passes, and it is fine.
            Assert.True(VulkanViewPolicy.ForTexture(GpuTextureUsage.Staging).Staging);
        }

        /// <summary>
        /// A CUBEMAP'S REAL LAYER COUNT IS SIX PER LOGICAL LAYER, which is the incumbent's
        /// <c>_actualImageArrayLayers</c> and the number the image and its views are created with. The LOGICAL
        /// count is what the seam speaks in and what the staging arithmetic uses.
        /// </summary>
        [Fact]
        public void ACubemapsRealLayerCount_IsSixPerLogicalLayer()
        {
            VulkanTextureViewPlan cube = VulkanViewPolicy.ForTexture(
                GpuTextureUsage.Sampled | GpuTextureUsage.Cubemap);
            VulkanTextureViewPlan flat = VulkanViewPolicy.ForTexture(GpuTextureUsage.Sampled);

            Assert.True(cube.Cubemap);
            Assert.Equal(12u, cube.ActualArrayLayers(2));
            Assert.False(flat.Cubemap);
            Assert.Equal(2u, flat.ActualArrayLayers(2));
        }

        /// <summary>
        /// EVERY BUFFER CARRIES BOTH TRANSFER BITS TOO, and a STAGING buffer carries those and nothing else: it is
        /// CPU-mapped and never bound, so a binding bit on it would describe a use it cannot have.
        /// </summary>
        [Fact]
        public void EveryBuffer_CarriesBothTransferBits_AndAStagingBufferCarriesNothingElse()
        {
            Assert.Equal(VulkanBufferBinding.TransferSrc | VulkanBufferBinding.TransferDst,
                VulkanViewPolicy.ForBuffer(GpuBufferUsage.Staging));

            VulkanBufferBinding vertex = VulkanViewPolicy.ForBuffer(GpuBufferUsage.VertexBuffer);
            Assert.True((vertex & VulkanBufferBinding.TransferSrc) != 0);
            Assert.True((vertex & VulkanBufferBinding.TransferDst) != 0);
            Assert.True((vertex & VulkanBufferBinding.Vertex) != 0);
        }

        /// <summary>
        /// BOTH STRUCTURED KINDS TAKE THE ONE STORAGE-BUFFER BIT (V-C4). Vulkan has no read-only storage-buffer
        /// bit, and there is no RAW byte-address forcing either: that is an HLSL artefact of what SPIRV-Cross emits
        /// for a GLSL storage block and has no analogue here.
        /// </summary>
        [Theory]
        [InlineData(GpuBufferUsage.StructuredBufferReadOnly)]
        [InlineData(GpuBufferUsage.StructuredBufferReadWrite)]
        [InlineData(GpuBufferUsage.StructuredBufferReadOnly | GpuBufferUsage.StructuredBufferReadWrite)]
        public void BothStructuredKinds_TakeTheOneStorageBufferBit(GpuBufferUsage usage)
            => Assert.True((VulkanViewPolicy.ForBuffer(usage) & VulkanBufferBinding.Storage) != 0);

        /// <summary>
        /// THE MEMORY LADDER FOLLOWS THE USAGE, and the two interesting rows are the ones that differ from the
        /// obvious reading. A STAGING buffer takes the READBACK ladder, which is the one rung that prefers a cached
        /// type and therefore the one place row 6's invalidate is real code. A DYNAMIC buffer does NOT become
        /// host-visible, unlike on the Veldrid leg: the only dynamic buffers this engine creates are uniform
        /// buffers, and those are ring-backed and host-visible for a better reason.
        /// </summary>
        [Fact]
        public void TheMemoryLadder_FollowsTheUsage()
        {
            (GpuBufferUsage Usage, VulkanMemoryUsage Ladder)[] expected =
            [
                (GpuBufferUsage.Staging, VulkanMemoryUsage.Readback),
                (GpuBufferUsage.UniformBuffer, VulkanMemoryUsage.Ring),
                (GpuBufferUsage.UniformBuffer | GpuBufferUsage.Dynamic, VulkanMemoryUsage.Ring),
                (GpuBufferUsage.VertexBuffer, VulkanMemoryUsage.DeviceLocal),
                (GpuBufferUsage.VertexBuffer | GpuBufferUsage.Dynamic, VulkanMemoryUsage.DeviceLocal),
                (GpuBufferUsage.StructuredBufferReadWrite, VulkanMemoryUsage.DeviceLocal),
            ];

            foreach ((GpuBufferUsage usage, VulkanMemoryUsage ladder) in expected)
            {
                Assert.Equal(ladder, VulkanViewPolicy.MemoryFor(usage));
            }
        }

        /// <summary>
        /// THE FORMAT MAP IS THE INCUMBENT'S, INCLUDING ITS DEPTH READING. <c>R32Float</c> is the one format whose
        /// answer depends on the usage: it becomes a real depth format when the texture carries
        /// <see cref="GpuTextureUsage.DepthStencil"/>, exactly as <c>VkFormats.VdToVkPixelFormat</c>'s
        /// <c>toDepthFormat</c> flag makes it. The two combined depth formats carry their depth spelling whatever
        /// the flag says, because neither has a colour one to fall back to.
        /// </summary>
        [Theory]
        [InlineData(GpuPixelFormat.R8UNorm, false, Format.R8Unorm)]
        [InlineData(GpuPixelFormat.R8G8B8A8UNorm, false, Format.R8G8B8A8Unorm)]
        [InlineData(GpuPixelFormat.B8G8R8A8UNorm, false, Format.B8G8R8A8Unorm)]
        [InlineData(GpuPixelFormat.R16G16B16A16Float, false, Format.R16G16B16A16Sfloat)]
        [InlineData(GpuPixelFormat.R32Float, false, Format.R32Sfloat)]
        [InlineData(GpuPixelFormat.R32Float, true, Format.D32Sfloat)]
        [InlineData(GpuPixelFormat.D24UNormS8UInt, true, Format.D24UnormS8Uint)]
        [InlineData(GpuPixelFormat.D24UNormS8UInt, false, Format.D24UnormS8Uint)]
        [InlineData(GpuPixelFormat.D32FloatS8UInt, true, Format.D32SfloatS8Uint)]
        public void TheFormatMap_IsTheIncumbents(GpuPixelFormat format, bool depthStencil, Format expected)
            => Assert.Equal(expected, VulkanFormats.ToVkFormat(format, depthStencil));

        /// <summary>
        /// THE ONE DELIBERATE DIVERGENCE, AND IT IS THE INCUMBENT'S DEFECT RATHER THAN ITS CONTRACT.
        /// <c>VkFormats.VdToVkPixelFormat</c> answers <c>VK_FORMAT_R16G16B16A16_SFLOAT</c> for
        /// <c>PixelFormat.R16_G16_Float</c>, a FOUR-channel format for a two-channel request. It is invisible on
        /// the shipped engine because the only texture using that format is the distortion offset target, which is
        /// written and sampled through red and green alone and never read back. It would NOT be invisible here:
        /// <see cref="VulkanStagingLayout"/> reproduces the incumbent's software layout, which sizes that format at
        /// four bytes per texel, so an image at eight would make every copy and every readback of one read the
        /// wrong bytes. This backend answers the two-channel format the seam asked for.
        /// </summary>
        [Fact]
        public void TwoChannelHalfFloat_MapsToTwoChannels_UnlikeTheIncumbent()
        {
            Assert.Equal(Format.R16G16Sfloat, VulkanFormats.ToVkFormat(GpuPixelFormat.R16G16Float, false));

            // And the arithmetic agrees with the image, which is what the divergence buys: four bytes per texel on
            // both sides rather than four on one and eight on the other.
            Assert.Equal(4u, VulkanStagingLayout.BytesPerTexel(GpuPixelFormat.R16G16Float));
        }

        /// <summary>
        /// THE VIEW ASPECT IS DEPTH ALONE FOR A DEPTH-STENCIL TEXTURE, reproduced from <c>VkTextureView</c>, which
        /// does the same and does NOT add the stencil aspect: nothing in this engine samples a stencil plane, a
        /// view carrying both aspects cannot be bound as a sampled image at all, and a copy region must name
        /// exactly one aspect bit anyway.
        /// </summary>
        [Fact]
        public void TheViewAspect_IsDepthAloneForADepthTexture()
        {
            Assert.Equal(ImageAspectFlags.DepthBit, VulkanFormats.ToAspect(depthStencil: true));
            Assert.Equal(ImageAspectFlags.ColorBit, VulkanFormats.ToAspect(depthStencil: false));
        }

        /// <summary>
        /// THE BARRIER AND CLEAR ASPECT IS THE OTHER ANSWER, AND IT IS NOT THE SAME RULE. A layout transition
        /// applies to the whole image, and without <c>separateDepthStencilLayouts</c> a barrier over a COMBINED
        /// format must name both planes or it is
        /// <c>VUID-VkImageMemoryBarrier2-image-03319</c>. The creation-time clear inherits the same range, which is
        /// what keeps the stencil plane out of the undefined contents V-M10's preserved clear exists to remove:
        /// both of the seam's depth formats are combined, so a depth-only clear left half of every depth target
        /// varying run to run.
        /// </summary>
        [Fact]
        public void TheBarrierAspect_AddsTheStencilPlaneOnACombinedFormat()
        {
            const ImageAspectFlags both = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit;

            Assert.Equal(both, VulkanFormats.ToBarrierAspect(true, GpuPixelFormat.D32FloatS8UInt));
            Assert.Equal(both, VulkanFormats.ToBarrierAspect(true, GpuPixelFormat.D24UNormS8UInt));
            Assert.True(VulkanFormats.IsStencilFormat(GpuPixelFormat.D32FloatS8UInt));
            Assert.True(VulkanFormats.IsStencilFormat(GpuPixelFormat.D24UNormS8UInt));

            // The one depth reading that has NO stencil plane: a single-channel float on a depth-stencil texture
            // becomes VK_FORMAT_D32_SFLOAT, where naming the stencil aspect would be the invalid answer.
            Assert.Equal(ImageAspectFlags.DepthBit, VulkanFormats.ToBarrierAspect(true, GpuPixelFormat.R32Float));
            Assert.False(VulkanFormats.IsStencilFormat(GpuPixelFormat.R32Float));

            // And a colour texture is colour whatever its format is.
            Assert.Equal(ImageAspectFlags.ColorBit,
                VulkanFormats.ToBarrierAspect(false, GpuPixelFormat.R8G8B8A8UNorm));
        }

        /// <summary>
        /// AN OUT-OF-RANGE SAMPLE COUNT THROWS RATHER THAN FALLING SILENTLY TO 1 (V-C6, C4's departure inherited).
        /// The engine clamps upstream in <c>AntiAliasing.ResolveFor</c>, so a count arriving here came from a
        /// caller that skipped it, and a silent downgrade presents as a golden mismatch that reads like a rendering
        /// bug.
        /// </summary>
        [Fact]
        public void AnOutOfRangeSampleCount_Throws()
        {
            Assert.Equal(SampleCountFlags.Count4Bit, VulkanFormats.ToSampleCount(4));
            Assert.Throws<ArgumentOutOfRangeException>(() => VulkanFormats.ToSampleCount(3));
            Assert.Throws<ArgumentOutOfRangeException>(() => VulkanFormats.ToSampleCount(64));
        }

        /// <summary>
        /// THE VIEW TYPE FOLLOWS THE CUBEMAP FLAG AND THE LOGICAL LAYER COUNT, reproduced from
        /// <c>VkTextureView</c>: one logical layer is a plain view and more than one is an array view, on both
        /// sides of the cubemap branch.
        /// </summary>
        [Theory]
        [InlineData(false, 1u, ImageViewType.Type2D)]
        [InlineData(false, 4u, ImageViewType.Type2DArray)]
        [InlineData(true, 1u, ImageViewType.TypeCube)]
        [InlineData(true, 2u, ImageViewType.TypeCubeArray)]
        public void TheViewType_FollowsTheCubemapFlagAndTheLayerCount(bool cubemap, uint layers,
            ImageViewType expected)
            => Assert.Equal(expected, VulkanFormats.ToViewType(cubemap, layers));

        /// <summary>
        /// AND AN EXPLICIT ARRAY WIDENS THE 2D ARM AT ONE LAYER (#666), which is the only way a texture whose
        /// fragment declares <c>texture2DArray</c> can carry a single layer. A 2D-array view over an image with
        /// one layer is legal, the range just covers that one. The cube arm keeps the count rule.
        /// </summary>
        [Theory]
        [InlineData(false, 1u, true, ImageViewType.Type2DArray)]
        [InlineData(false, 1u, false, ImageViewType.Type2D)]
        [InlineData(false, 4u, false, ImageViewType.Type2DArray)]
        [InlineData(true, 1u, true, ImageViewType.TypeCube)]
        public void AnExplicitArray_TakesTheArrayViewTypeAtOneLayer(bool cubemap, uint layers, bool arrayView,
            ImageViewType expected)
            => Assert.Equal(expected, VulkanFormats.ToViewType(cubemap, layers, arrayView));

        /// <summary>
        /// A STAGING TEXTURE HAS NO LAYOUT AT ALL, so asking for one is a caller that lost track of which kind of
        /// resource it is holding. It is a <c>VkBuffer</c> here (V-C7), and there is no image to be in a layout.
        /// </summary>
        [Fact]
        public void AStagingTexturesLayout_IsRefusedRatherThanInvented()
            => Assert.Throws<ArgumentOutOfRangeException>(
                () => VulkanFormats.ToImageLayout(VulkanRestingLayout.None));

        /// <summary>
        /// THE SHARED SAMPLER PAIR WRAPS ON ALL THREE AXES, and the assertion that matters most is the one against
        /// the engine's own identically named statics. <see cref="GpuSamplerDescription.Point"/> and
        /// <see cref="GpuSamplerDescription.Linear"/> default every axis to CLAMP, and reading the address mode off
        /// them because the names matched cost two goldens on the Direct3D 11 leg. The same mistake is available
        /// here, which is why this test compares the two rather than merely asserting wrap.
        /// </summary>
        [Fact]
        public void TheSharedSamplerPair_Wraps_AndTheEngineStaticsDoNot()
        {
            foreach (GpuSamplerDescription shared in new[]
                     { VulkanSharedSamplers.Point, VulkanSharedSamplers.Linear })
            {
                Assert.Equal(GpuSamplerAddress.Wrap, shared.AddressModeU);
                Assert.Equal(GpuSamplerAddress.Wrap, shared.AddressModeV);
                Assert.Equal(GpuSamplerAddress.Wrap, shared.AddressModeW);
            }

            Assert.Equal(GpuSamplerAddress.Clamp, GpuSamplerDescription.Point.AddressModeU);
            Assert.Equal(GpuSamplerAddress.Clamp, GpuSamplerDescription.Linear.AddressModeU);

            // Same filters, opposite addressing: the collision this pair exists to survive.
            Assert.Equal(GpuSamplerDescription.Point.Filter, VulkanSharedSamplers.Point.Filter);
            Assert.Equal(GpuSamplerDescription.Linear.Filter, VulkanSharedSamplers.Linear.Filter);
        }

        /// <summary>
        /// THE FOUR VALUES THE SEAM DOES NOT EXPOSE ARE THE INCUMBENT'S, because the committed goldens were baked
        /// through them: no comparison sampler, a minimum LOD of 0, a maximum LOD of <c>uint.MaxValue</c> and a
        /// transparent-black border. The maximum LOD is asserted as the widened <c>uint.MaxValue</c> rather than as
        /// a round number, because that widening is what the engine's Veldrid path passed and what reaches
        /// <c>VkSamplerCreateInfo.maxLod</c> there.
        /// </summary>
        [Fact]
        public void TheFourValuesTheSeamDoesNotExpose_AreTheIncumbents()
        {
            VulkanSamplerSpec spec = VulkanSamplerPolicy.For(
                GpuSamplerDescription.Linear, deviceSamplerAnisotropy: true);

            Assert.Equal(0f, spec.MinLod);
            Assert.Equal((float)uint.MaxValue, spec.MaxLod);
            Assert.Equal(BorderColor.FloatTransparentBlack, VulkanFormats.TransparentBlackBorder);
        }

        /// <summary>
        /// THE ANISOTROPY DEGRADATION IS LIVE HERE, unlike on the Direct3D 11 backend where it was unreachable. The
        /// engine's Veldrid path fell back from anisotropic filtering to trilinear on a device without
        /// <c>samplerAnisotropy</c> and drops the maximum anisotropy with it, and lavapipe is exactly such a
        /// device: it is the rasterizer the golden gate runs on. Asking for <c>anisotropyEnable</c> without the
        /// feature is <c>VUID-VkSamplerCreateInfo-anisotropyEnable-01070</c> rather than a slow path.
        /// </summary>
        [Fact]
        public void AnisotropyDegradesToTrilinear_OnADeviceWithoutTheFeature()
        {
            var requested = new GpuSamplerDescription(GpuSamplerFilter.Anisotropic,
                GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap, maximumAnisotropy: 16);

            VulkanSamplerSpec supported = VulkanSamplerPolicy.For(requested, deviceSamplerAnisotropy: true);
            Assert.Equal(GpuSamplerFilter.Anisotropic, supported.Filter);
            Assert.True(supported.AnisotropyEnable);
            Assert.Equal(16f, supported.MaxAnisotropy);

            VulkanSamplerSpec degraded = VulkanSamplerPolicy.For(requested, deviceSamplerAnisotropy: false);
            Assert.Equal(GpuSamplerFilter.MinLinearMagLinearMipLinear, degraded.Filter);
            Assert.False(degraded.AnisotropyEnable);
            Assert.Equal(0f, degraded.MaxAnisotropy);
        }

        /// <summary>
        /// THE LOD BIAS IS NEVER DROPPED, which is the OTHER degradation the Veldrid path carried and the one that
        /// really is unreachable here. It exists because Metal's sampler has no bias at all. <c>mipLodBias</c> is
        /// core Vulkan with no feature bit in front of it, and this backend's capability read answers true
        /// unconditionally, so a branch dropping it could never be taken and would be a branch nothing can test.
        /// </summary>
        [Fact]
        public void TheLodBias_SurvivesTheMapping()
        {
            var requested = new GpuSamplerDescription(GpuSamplerFilter.MinLinearMagLinearMipLinear,
                mipLodBias: -2);

            Assert.Equal(-2f, VulkanSamplerPolicy.For(requested, deviceSamplerAnisotropy: false).MipLodBias);
        }

        /// <summary>
        /// ANISOTROPIC IS LINEAR ON ALL THREE FILTERS PLUS THE ENABLE, which is what the incumbent's
        /// <c>GetFilterParams</c> sets for it, and point is nearest on all three. The engine has no comparison
        /// sampler, so the mixed combinations of that switch are unreachable here.
        /// </summary>
        [Fact]
        public void TheFilterParams_AreTheIncumbents()
        {
            VulkanFormats.GetFilterParams(GpuSamplerFilter.MinPointMagPointMipPoint, out Filter min,
                out Filter mag, out SamplerMipmapMode mip);
            Assert.Equal(Filter.Nearest, min);
            Assert.Equal(Filter.Nearest, mag);
            Assert.Equal(SamplerMipmapMode.Nearest, mip);

            VulkanFormats.GetFilterParams(GpuSamplerFilter.Anisotropic, out min, out mag, out mip);
            Assert.Equal(Filter.Linear, min);
            Assert.Equal(Filter.Linear, mag);
            Assert.Equal(SamplerMipmapMode.Linear, mip);
        }
    }
}
