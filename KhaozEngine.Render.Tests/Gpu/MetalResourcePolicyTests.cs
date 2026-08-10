using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// EVERY CREATION DECISION ROW 6 MAKES, DRIVEN WITH NO DEVICE. The format maps, the buffer policy, the eager
    /// view plan and the sampler policy are all pure functions of the seam's descriptions, which is why they are
    /// separate types from the wrappers that call them: it makes the part that can be wrong silently the part
    /// that runs on every leg on every <c>dotnet test</c>.
    /// <para>
    /// The one thing here that genuinely needs hardware is that Metal ACCEPTS what these functions produce, and
    /// that is <c>MetalResourceGpuTests</c>.
    /// </para>
    /// </summary>
    public sealed class MetalResourcePolicyTests
    {
        // ---- Formats ------------------------------------------------------------------------------------------

        /// <summary>
        /// The pixel-format map, every member, against the incumbent's <c>MTLFormats.VdToMTLPixelFormat</c>.
        /// A wrong row here is every pixel of every texture in that format.
        /// </summary>
        [Fact]
        public void ThePixelFormatMap_ReproducesTheIncumbent()
        {
            // A [Fact] rather than a [Theory] because MTLPixelFormat is internal to the backend and an
            // [InlineData] parameter would have to be public. Naming the enum member on both sides is what makes
            // a wrong row readable, so the value is spelled rather than passed as its number.
            Assert.Equal(MTLPixelFormat.R8Unorm, MetalFormats.ToPixelFormat(GpuPixelFormat.R8UNorm, false));
            Assert.Equal(MTLPixelFormat.RG16Float, MetalFormats.ToPixelFormat(GpuPixelFormat.R16G16Float, false));
            Assert.Equal(MTLPixelFormat.RGBA8Unorm,
                MetalFormats.ToPixelFormat(GpuPixelFormat.R8G8B8A8UNorm, false));
            Assert.Equal(MTLPixelFormat.BGRA8Unorm,
                MetalFormats.ToPixelFormat(GpuPixelFormat.B8G8R8A8UNorm, false));
            Assert.Equal(MTLPixelFormat.RGBA16Float,
                MetalFormats.ToPixelFormat(GpuPixelFormat.R16G16B16A16Float, false));
            Assert.Equal(MTLPixelFormat.Depth24UnormStencil8,
                MetalFormats.ToPixelFormat(GpuPixelFormat.D24UNormS8UInt, true));
            Assert.Equal(MTLPixelFormat.Depth32FloatStencil8,
                MetalFormats.ToPixelFormat(GpuPixelFormat.D32FloatS8UInt, true));
        }

        /// <summary>
        /// <see cref="GpuPixelFormat.R32Float"/> IS THE ONE FORMAT WHOSE ANSWER DEPENDS ON THE USAGE, which is why
        /// the map takes two arguments at all. The 3D pass uses it as a colour attachment for linear depth and as
        /// a depth attachment for a shadow map, and Metal has a different pixel format for each. Getting it
        /// backwards is a depth target the shadow pass cannot write, which renders black rather than throwing.
        /// </summary>
        [Fact]
        public void R32Float_BecomesADepthFormatOnlyWhenTheTextureDeclaresDepth()
        {
            Assert.Equal(MTLPixelFormat.R32Float, MetalFormats.ToPixelFormat(GpuPixelFormat.R32Float, false));
            Assert.Equal(MTLPixelFormat.Depth32Float, MetalFormats.ToPixelFormat(GpuPixelFormat.R32Float, true));
        }

        /// <summary>The texture-type ladder, in the incumbent's own order: cube beats multisample beats
        /// array.</summary>
        [Fact]
        public void TheTextureTypeMap_ReproducesTheIncumbent()
        {
            Assert.Equal(MTLTextureType.Type2D, MetalFormats.TextureTypeFor(1, false, false));
            Assert.Equal(MTLTextureType.Type2DArray, MetalFormats.TextureTypeFor(4, false, false));
            Assert.Equal(MTLTextureType.Type2DMultisample, MetalFormats.TextureTypeFor(1, true, false));
            Assert.Equal(MTLTextureType.TypeCube, MetalFormats.TextureTypeFor(1, false, true));
            Assert.Equal(MTLTextureType.TypeCubeArray, MetalFormats.TextureTypeFor(3, false, true));

            // Cube beats multisample, which is the incumbent's own order. Unreachable through the seam (an MSAA
            // texture is a render target with one mip level and is never a cubemap), so the ordering decides
            // nothing today and reproducing it costs nothing.
            Assert.Equal(MTLTextureType.TypeCube, MetalFormats.TextureTypeFor(1, true, true));
        }

        /// <summary>
        /// ONE METAL BIT SERVES BOTH ATTACHMENT USAGES, and three seam usages set no bit at all. Both halves are
        /// reproduction rather than omission: Metal takes the aspect from the pixel format, a staging texture is
        /// not a texture here, a cubemap is a texture TYPE, and
        /// <see cref="GpuTextureUsage.GenerateMipmaps"/> constrains the format rather than the usage bits.
        /// </summary>
        [Fact]
        public void TheTextureUsageMap_ReproducesTheIncumbent_IncludingWhatItDoesNotMap()
        {
            Assert.Equal(MTLTextureUsage.ShaderRead, MetalFormats.ToTextureUsage(GpuTextureUsage.Sampled));
            Assert.Equal(MTLTextureUsage.ShaderWrite, MetalFormats.ToTextureUsage(GpuTextureUsage.Storage));
            Assert.Equal(MTLTextureUsage.RenderTarget, MetalFormats.ToTextureUsage(GpuTextureUsage.RenderTarget));
            Assert.Equal(MTLTextureUsage.RenderTarget, MetalFormats.ToTextureUsage(GpuTextureUsage.DepthStencil));

            Assert.Equal(MTLTextureUsage.ShaderRead | MTLTextureUsage.RenderTarget,
                MetalFormats.ToTextureUsage(GpuTextureUsage.Sampled | GpuTextureUsage.RenderTarget));

            Assert.Equal(MTLTextureUsage.Unknown, MetalFormats.ToTextureUsage(GpuTextureUsage.Cubemap));
            Assert.Equal(MTLTextureUsage.Unknown, MetalFormats.ToTextureUsage(GpuTextureUsage.GenerateMipmaps));
            Assert.Equal(MTLTextureUsage.Unknown, MetalFormats.ToTextureUsage(GpuTextureUsage.Staging));
        }

        /// <summary>The address-mode map, total over the seam's four members.</summary>
        [Fact]
        public void TheAddressModeMap_ReproducesTheIncumbent()
        {
            Assert.Equal(MTLSamplerAddressMode.Repeat, MetalFormats.ToAddressMode(GpuSamplerAddress.Wrap));
            Assert.Equal(MTLSamplerAddressMode.MirrorRepeat, MetalFormats.ToAddressMode(GpuSamplerAddress.Mirror));
            Assert.Equal(MTLSamplerAddressMode.ClampToEdge, MetalFormats.ToAddressMode(GpuSamplerAddress.Clamp));
            Assert.Equal(MTLSamplerAddressMode.ClampToBorderColor,
                MetalFormats.ToAddressMode(GpuSamplerAddress.Border));
        }

        /// <summary>
        /// ANISOTROPIC AND TRILINEAR RESOLVE TO THE SAME THREE FILTERS, which is the incumbent's own shape rather
        /// than a collapsed case: its anisotropic arm sets linear on all three and the separate
        /// <c>maxAnisotropy</c> field is what makes the sampler anisotropic.
        /// </summary>
        [Fact]
        public void TheFilterMap_ResolvesAnisotropicToTrilinear_AndCarriesTheAnisotropyElsewhere()
        {
            MetalFilterSelection point = MetalFormats.ToFilterSelection(
                GpuSamplerFilter.MinPointMagPointMipPoint);
            Assert.Equal(new MetalFilterSelection(MTLSamplerMinMagFilter.Nearest, MTLSamplerMinMagFilter.Nearest,
                MTLSamplerMipFilter.Nearest), point);

            MetalFilterSelection linear = MetalFormats.ToFilterSelection(
                GpuSamplerFilter.MinLinearMagLinearMipLinear);
            Assert.Equal(MetalFormats.ToFilterSelection(GpuSamplerFilter.Anisotropic), linear);

            Assert.Equal(1u, (uint)MetalSamplerPolicy.For(GpuSamplerDescription.Linear).MaxAnisotropy);
            Assert.Equal(16u, (uint)MetalSamplerPolicy.For(
                new GpuSamplerDescription(GpuSamplerFilter.Anisotropic, maximumAnisotropy: 16)).MaxAnisotropy);
        }

        // ---- Buffers ------------------------------------------------------------------------------------------

        /// <summary>
        /// The four-byte size rounding, reproduced from <c>MTLBuffer.ActualCapacity</c>. It is reached rather than
        /// theoretical: the size-rounding half of the incumbent's <c>CopyBuffer</c> handling pads a copy up to a
        /// multiple of four, and it can only pad into bytes the destination really owns.
        /// </summary>
        [Theory]
        [InlineData(0u, 0u)]
        [InlineData(1u, 4u)]
        [InlineData(3u, 4u)]
        [InlineData(4u, 4u)]
        [InlineData(5u, 8u)]
        [InlineData(255u, 256u)]
        [InlineData(256u, 256u)]
        public void TheAllocationSize_RoundsUpToFour(uint requested, uint expected)
            => Assert.Equal(expected, MetalBufferPolicy.AllocationBytes(requested));

        /// <summary>U3's FIRST creation-time invariant (M-M6): only uniform usage is ring-backed.</summary>
        [Fact]
        public void OnlyUniformUsage_IsRingBacked()
        {
            Assert.True(MetalBufferPolicy.IsRingBacked(GpuBufferUsage.UniformBuffer));
            Assert.True(MetalBufferPolicy.IsRingBacked(GpuBufferUsage.UniformBuffer | GpuBufferUsage.Dynamic));

            Assert.False(MetalBufferPolicy.IsRingBacked(GpuBufferUsage.VertexBuffer));
            Assert.False(MetalBufferPolicy.IsRingBacked(GpuBufferUsage.IndexBuffer));
            Assert.False(MetalBufferPolicy.IsRingBacked(GpuBufferUsage.StructuredBufferReadOnly));
            Assert.False(MetalBufferPolicy.IsRingBacked(GpuBufferUsage.Staging));
        }

        /// <summary>
        /// U3's SECOND invariant (M-M6): a ring-backed buffer that also declares a structured binding throws at
        /// CREATION, as a documented backend-divergent creation failure. The message has to carry the reason,
        /// because both Veldrid backends accept the combination and a consumer hitting this needs to know it is a
        /// deliberate difference rather than a bug.
        /// </summary>
        [Fact]
        public void AUniformBufferThatIsAlsoStructured_IsRefusedAtCreation()
        {
            MetalBufferPolicy.RequireCreatable(GpuBufferUsage.UniformBuffer);
            MetalBufferPolicy.RequireCreatable(GpuBufferUsage.StructuredBufferReadWrite);

            foreach (GpuBufferUsage structured in new[]
            {
                GpuBufferUsage.StructuredBufferReadOnly, GpuBufferUsage.StructuredBufferReadWrite,
            })
            {
                ArgumentException thrown = Assert.Throws<ArgumentException>(
                    () => MetalBufferPolicy.RequireCreatable(GpuBufferUsage.UniformBuffer | structured));

                Assert.Contains("frame ring", thrown.Message, StringComparison.Ordinal);
                Assert.Contains("create two buffers", thrown.Message, StringComparison.Ordinal);
            }
        }

        /// <summary>A write past the end is refused by name, where the incumbent's own copy has no bound check at
        /// all.</summary>
        [Fact]
        public void AWritePastTheEnd_IsRefusedWithBothNumbers()
        {
            MetalBufferPolicy.RequireWriteFits(0, 64, 64);
            MetalBufferPolicy.RequireWriteFits(32, 32, 64);

            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(
                () => MetalBufferPolicy.RequireWriteFits(32, 33, 64));

            Assert.Contains("33", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("64", thrown.Message, StringComparison.Ordinal);
        }

        // ---- Textures and views -------------------------------------------------------------------------------

        /// <summary>
        /// THE EAGER VIEW SET IS EMPTY FOR EVERY USAGE THE SEAM CAN EXPRESS (M-M10), which is the assertion the
        /// design's "no view factory reachable from the recording type" reduces to on this backend: nothing this
        /// seam can ask for NARROWS a texture, so every case is the branch where the incumbent uses the target's
        /// own texture and creates no native object.
        /// </summary>
        [Fact]
        public void NoUsageTheSeamCanExpress_CreatesAView()
        {
            foreach (GpuTextureUsage usage in EveryBindableUsage())
            {
                MetalTextureViewPlan plan = MetalViewPolicy.ForTexture(usage, 1, 1);
                Assert.Equal(0, plan.ViewCount);
            }

            Assert.Equal(0, MetalViewPolicy.ForTexture(GpuTextureUsage.Staging, 1, 1).ViewCount);
        }

        /// <summary>Every real texture is Private (M-M2), and a staging texture is a Shared buffer with no
        /// texture at all (M-C5).</summary>
        [Fact]
        public void EveryRealTextureIsPrivate_AndStagingIsABuffer()
        {
            foreach (GpuTextureUsage usage in EveryBindableUsage())
            {
                MetalTextureViewPlan plan = MetalViewPolicy.ForTexture(usage, 1, 1);
                Assert.False(plan.Staging);
                Assert.Equal(MTLStorageMode.Private, plan.Storage);
            }

            MetalTextureViewPlan staging = MetalViewPolicy.ForTexture(GpuTextureUsage.Staging, 1, 1);
            Assert.True(staging.Staging);
            Assert.Equal(MTLTextureUsage.Unknown, staging.Usage);
        }

        /// <summary>
        /// The staging bit combined with anything else is refused, because a staging texture here is a buffer and
        /// there is no texture for the other bits to describe. Every staging texture the engine creates passes the
        /// bit alone.
        /// </summary>
        [Fact]
        public void StagingCombinedWithAnythingElse_IsRefused()
        {
            ArgumentException thrown = Assert.Throws<ArgumentException>(
                () => MetalViewPolicy.ForTexture(GpuTextureUsage.Staging | GpuTextureUsage.Sampled, 1, 1));

            Assert.Contains("MTLBuffer", thrown.Message, StringComparison.Ordinal);
        }

        /// <summary>The depth reading travels on the plan, because it is what picks between the two Metal formats
        /// <see cref="GpuPixelFormat.R32Float"/> can become.</summary>
        [Fact]
        public void ThePlanCarriesTheDepthReading()
        {
            Assert.True(MetalViewPolicy.ForTexture(GpuTextureUsage.DepthStencil, 1, 1).DepthStencil);
            Assert.False(MetalViewPolicy.ForTexture(GpuTextureUsage.RenderTarget, 1, 1).DepthStencil);
        }

        // ---- Samplers -----------------------------------------------------------------------------------------

        /// <summary>
        /// THE SHARED PAIR IS WRAP ON ALL THREE AXES, and this is the row that would have caught the Direct3D 11
        /// leg's two-golden defect. The engine's own <see cref="GpuSamplerDescription.Point"/> and
        /// <see cref="GpuSamplerDescription.Linear"/> statics carry the same NAMES and CLAMP, so the assertion is
        /// deliberately written against both: the shared pair wraps, and the engine statics still clamp, because
        /// changing the second would be changing documented public API.
        /// </summary>
        [Fact]
        public void TheSharedSamplerPair_WrapsWhereTheEngineStaticsClamp()
        {
            foreach (GpuSamplerDescription shared in new[]
            {
                MetalSharedSamplers.Point, MetalSharedSamplers.Linear,
            })
            {
                Assert.Equal(GpuSamplerAddress.Wrap, shared.AddressModeU);
                Assert.Equal(GpuSamplerAddress.Wrap, shared.AddressModeV);
                Assert.Equal(GpuSamplerAddress.Wrap, shared.AddressModeW);
                Assert.Equal(0u, shared.MaximumAnisotropy);
                Assert.Equal(0, shared.MipLodBias);
            }

            Assert.Equal(GpuSamplerFilter.MinPointMagPointMipPoint, MetalSharedSamplers.Point.Filter);
            Assert.Equal(GpuSamplerFilter.MinLinearMagLinearMipLinear, MetalSharedSamplers.Linear.Filter);

            Assert.Equal(GpuSamplerAddress.Clamp, GpuSamplerDescription.Point.AddressModeU);
            Assert.Equal(GpuSamplerAddress.Clamp, GpuSamplerDescription.Linear.AddressModeU);

            MetalSamplerSpec spec = MetalSamplerPolicy.For(MetalSharedSamplers.Linear);
            Assert.Equal(MTLSamplerAddressMode.Repeat, spec.AddressS);
            Assert.Equal(MTLSamplerAddressMode.Repeat, spec.AddressT);
            Assert.Equal(MTLSamplerAddressMode.Repeat, spec.AddressR);
        }

        /// <summary>
        /// The four values the seam does not expose, hardcoded to what the engine's Veldrid path passes: no
        /// comparison function, a minimum LOD of 0, a maximum LOD of <c>uint.MaxValue</c>, and a transparent-black
        /// border colour. Changing one would move pixels.
        /// </summary>
        [Fact]
        public void TheFourValuesTheSeamDoesNotExpose_AreTheIncumbentsOwn()
        {
            MetalSamplerSpec spec = MetalSamplerPolicy.For(GpuSamplerDescription.Point);

            Assert.Equal(0f, spec.LodMinClamp);
            Assert.Equal(uint.MaxValue, spec.LodMaxClamp);
            Assert.Equal(MTLSamplerBorderColor.TransparentBlack, spec.BorderColor);
        }

        /// <summary>
        /// THE ANISOTROPY IS RAISED TO AT LEAST ONE, which is <c>Math.Max(1, MaximumAnisotropy)</c> in the
        /// incumbent: Metal rejects zero, and the seam documents 0 as "keep the historical behaviour" rather than
        /// as a value.
        /// </summary>
        [Fact]
        public void TheAnisotropy_IsRaisedToAtLeastOne()
        {
            Assert.Equal(1u, (uint)MetalSamplerPolicy.For(
                new GpuSamplerDescription(GpuSamplerFilter.Anisotropic, maximumAnisotropy: 0)).MaxAnisotropy);
        }

        // ---- Resource ownership -------------------------------------------------------------------------------

        /// <summary>A resource created by the asking device passes through, which is the positive control the two
        /// refusals below need in order to mean anything.</summary>
        [Fact]
        public void AResourceFromTheAskingDevice_IsAccepted()
        {
            var device = new FakeMetalDeviceLiveness();
            var owned = new OwnedResource(device);

            Assert.Same(owned, MetalResourceOwnership.Require<OwnedResource>(owned, device, "resource"));
        }

        /// <summary>
        /// A RESOURCE FROM ANOTHER DEVICE IS REFUSED, which a type-only cast cannot do. The identity is the
        /// liveness token rather than the <c>MTLDevice</c> handle, because Apple silicon reports one
        /// <c>MTLDevice</c> for the whole process and two devices there would compare equal on the handle.
        /// </summary>
        [Fact]
        public void AResourceFromAnotherDevice_IsRefused()
        {
            var owned = new OwnedResource(new FakeMetalDeviceLiveness());

            ArgumentException thrown = Assert.Throws<ArgumentException>(
                () => MetalResourceOwnership.Require<OwnedResource>(owned, new FakeMetalDeviceLiveness(),
                    "resource"));

            Assert.Contains("DIFFERENT native Metal device", thrown.Message, StringComparison.Ordinal);
            Assert.Equal("resource", thrown.ParamName);
        }

        /// <summary>And a resource from another BACKEND is refused by name rather than by
        /// <see cref="InvalidCastException"/>, which is what a plain cast produced and which says nothing a
        /// caller can act on.</summary>
        [Fact]
        public void AResourceFromAnotherBackend_IsRefusedByName()
        {
            ArgumentException thrown = Assert.Throws<ArgumentException>(
                () => MetalResourceOwnership.Require<OwnedResource>("not a resource",
                    new FakeMetalDeviceLiveness(), "resource"));

            Assert.Contains("was not created by the native Metal backend", thrown.Message,
                StringComparison.Ordinal);
        }

        // A resource that knows its owner and nothing else. The real wrappers cannot be built without a device,
        // and what is under test here is the ownership rule rather than any of them.
        sealed class OwnedResource(IMetalDeviceLiveness owner) : IMetalOwnedResource
        {
            public IMetalDeviceLiveness Owner { get; } = owner;
        }

        // ---- Upload regions -----------------------------------------------------------------------------------

        /// <summary>
        /// A region inside its destination subresource is accepted, on every shape the seam can name: the whole
        /// mip, a sub-rectangle at a non-zero origin, one that ends exactly on both edges, a non-zero mip level
        /// and a non-zero array layer.
        /// </summary>
        [Fact]
        public void ARegionInsideItsSubresource_IsAccepted()
        {
            var shape = new MetalStagingShape(64, 32, 4, 3, GpuPixelFormat.R8G8B8A8UNorm);

            MetalStagingLayout.RequireRegionFits(shape, 0, 0, 0, 0, 64, 32);
            MetalStagingLayout.RequireRegionFits(shape, 0, 0, 1, 1, 2, 2);
            MetalStagingLayout.RequireRegionFits(shape, 0, 0, 63, 31, 1, 1);

            // Mip 2 of a 64 by 32 texture is 16 by 8, and the region is checked against THAT rather than against
            // mip 0, which is the whole reason the check takes the level.
            MetalStagingLayout.RequireRegionFits(shape, 2, 0, 0, 0, 16, 8);
            MetalStagingLayout.RequireRegionFits(shape, 2, 2, 8, 4, 8, 4);
        }

        /// <summary>
        /// ONE TEXEL PAST THE RIGHT EDGE IS REFUSED, which is the case the length check cannot see: the payload
        /// is exactly the right size for the region and the region is in the wrong place.
        /// </summary>
        [Fact]
        public void ARegionOneTexelPastTheRightEdge_IsRefused()
        {
            var shape = new MetalStagingShape(64, 32, 4, 1, GpuPixelFormat.R8G8B8A8UNorm);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => MetalStagingLayout.RequireRegionFits(shape, 0, 0, 1, 0, 64, 32));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => MetalStagingLayout.RequireRegionFits(shape, 0, 0, 0, 0, 65, 32));

            // And against the MIP's dimensions rather than mip 0's: 32 by 16 fits the texture and not level 2.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => MetalStagingLayout.RequireRegionFits(shape, 2, 0, 0, 0, 32, 8));
        }

        /// <summary>The same one texel past the BOTTOM edge, because a check that only compared one axis would
        /// pass every row of this class and still corrupt memory.</summary>
        [Fact]
        public void ARegionOneTexelPastTheBottomEdge_IsRefused()
        {
            var shape = new MetalStagingShape(64, 32, 4, 1, GpuPixelFormat.R8G8B8A8UNorm);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => MetalStagingLayout.RequireRegionFits(shape, 0, 0, 0, 1, 64, 32));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => MetalStagingLayout.RequireRegionFits(shape, 0, 0, 0, 0, 64, 33));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => MetalStagingLayout.RequireRegionFits(shape, 2, 0, 0, 0, 16, 16));
        }

        /// <summary>A subresource that does not exist is refused before its dimensions are asked for, since there
        /// is nothing to compare a region against.</summary>
        [Fact]
        public void AMipLevelOrArrayLayerOutsideTheTexture_IsRefused()
        {
            var shape = new MetalStagingShape(64, 32, 4, 3, GpuPixelFormat.R8G8B8A8UNorm);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => MetalStagingLayout.RequireRegionFits(shape, 4, 0, 0, 0, 1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => MetalStagingLayout.RequireRegionFits(shape, 0, 3, 0, 0, 1, 1));
        }

        // Every usage combination a BINDABLE texture can carry, which is the power set of the five bits that are
        // not Staging, minus the empty set. Enumerated rather than sampled, because "for every usage" is what the
        // eager-view claim says and a sampled subset would be a weaker statement wearing the same words.
        static GpuTextureUsage[] EveryBindableUsage()
        {
            GpuTextureUsage[] bits =
            [
                GpuTextureUsage.Sampled, GpuTextureUsage.RenderTarget, GpuTextureUsage.DepthStencil,
                GpuTextureUsage.Cubemap, GpuTextureUsage.GenerateMipmaps, GpuTextureUsage.Storage,
            ];

            return Enumerable.Range(1, (1 << bits.Length) - 1)
                .Select(mask => bits.Where((_, i) => (mask & (1 << i)) != 0)
                    .Aggregate(GpuTextureUsage.None, (a, b) => a | b))
                .ToArray();
        }
    }
}
