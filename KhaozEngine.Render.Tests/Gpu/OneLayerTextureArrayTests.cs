using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A ONE-LAYER TEXTURE ARRAY IS EXPRESSIBLE, AND IT SURVIVES THE TRIP TO A BACKEND (#666).
    ///
    /// <para><b>The bug this pins.</b> Every backend used to derive a texture's array-ness from
    /// <c>ArrayLayers &gt; 1</c> alone, so a set with exactly one layer created a plain 2D texture. A pipeline
    /// whose fragment declares <c>texture2DArray</c> then bound the wrong type: Metal killed the process with
    /// <c>incorrect type of texture (MTLTextureType2D) bound ... (expect MTLTextureType2DArray)</c> when
    /// validation was armed, and lavapipe tolerated it silently, which is worse. The tile-ground path shipped a
    /// caller-side pad (a one-layer set duplicated into two) to get round it, and that pad is gone.</para>
    ///
    /// <para><b>The rule.</b> <see cref="GpuTextureDescription.IsArray"/> says it outright, and
    /// <see cref="GpuTextureDescription.Texture2DArray"/> sets it. The old layer-count inference is kept as the
    /// default, so a caller that never heard of the flag gets exactly the texture it got before, which is what
    /// makes the change additive.</para>
    ///
    /// <para><b>What runs where.</b> The description and the fake-device round-trip below run on every leg. The
    /// Metal and Vulkan type derivations have their rows in <c>MetalResourcePolicyTests</c> and
    /// <c>VulkanViewPolicyTests</c>, device-free, and the Vulkan view spec below pins the flag reaching the
    /// driver call. Direct3D 11 decides its shader resource view dimension inside a Windows-only creation path
    /// with no device-free seam, so its evidence is the WARP legs of <c>cross-platform-gpu.yml</c>. That the
    /// three natives and the incumbent all DRAW a one-layer array correctly is
    /// <c>TileGroundMaterialGpuTests.Single_flat_layer_reproduces_a_vertex_colour_look</c>, which is a
    /// <c>[GpuFact]</c> and therefore runs on Metal locally and on WARP and lavapipe on the golden matrix.</para>
    /// </summary>
    public sealed class OneLayerTextureArrayTests
    {
        /// <summary>The whole point: the factory that NAMES the array case makes an array even at one layer.
        /// </summary>
        [Fact]
        public void Texture2DArray_IsAnArrayEvenAtOneLayer()
        {
            GpuTextureDescription d = GpuTextureDescription.Texture2DArray(
                8, 8, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled, arrayLayers: 1, mipLevels: 1);

            Assert.True(d.IsArray);
            Assert.Equal(1u, d.ArrayLayers);
        }

        /// <summary>A plain 2D texture stays a plain 2D texture. Without this the flag would be a rename of
        /// "every texture is an array" and every 2D bind would take an array view.</summary>
        [Fact]
        public void Texture2D_IsNotAnArray()
        {
            GpuTextureDescription d = GpuTextureDescription.Texture2D(
                8, 8, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled);

            Assert.False(d.IsArray);
            Assert.Equal(1u, d.ArrayLayers);
        }

        /// <summary>The OLD inference is kept, which is what makes the change additive: a caller that passes a
        /// layer count and nothing else gets the array it has always got.</summary>
        [Theory]
        [InlineData(1u, false)]
        [InlineData(2u, true)]
        [InlineData(5u, true)]
        public void TheLayerCount_StillInfersArrayNessOnItsOwn(uint layers, bool expected)
        {
            var d = new GpuTextureDescription(8, 8, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled,
                mipLevels: 1, arrayLayers: layers);

            Assert.Equal(expected, d.IsArray);
        }

        /// <summary>The flag reaches the device seam, which is where each backend reads it. The fake device
        /// records what a real one would have been handed.</summary>
        [Fact]
        public void AOneLayerArray_ReachesTheDeviceAsAnArray()
        {
            var device = new FakeGpuDevice();
            var factory = (FakeGpuResourceFactory)device.Factory;

            factory.CreateTexture(GpuTextureDescription.Texture2DArray(
                4, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled, arrayLayers: 1, mipLevels: 1));
            factory.CreateTexture(GpuTextureDescription.Texture2D(
                4, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));

            FakeTexture array = factory.Textures[0];
            FakeTexture plain = factory.Textures[1];

            Assert.True(array.IsArray);
            Assert.Equal(1u, array.ArrayLayers);
            Assert.False(plain.IsArray);
        }

        /// <summary>
        /// THE NATIVE VULKAN SAMPLED VIEW CARRIES IT, so <c>vkCreateImageView</c> is called with
        /// <c>VK_IMAGE_VIEW_TYPE_2D_ARRAY</c> over a single layer. Before this the spec said one layer and the
        /// view type map answered <c>TYPE_2D</c>, which is the mismatch a fragment declaring
        /// <c>texture2DArray</c> reads through.
        /// </summary>
        [Fact]
        public void TheVulkanSampledView_CarriesTheArrayFlagAtOneLayer()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuTexture texture = fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                16, 16, GpuTextureUsage.Sampled, arrayLayers: 1, isArray: true));

            VulkanImageViewSpec view = Assert.Single(fixture.Views);
            Assert.True(view.ArrayView);
            Assert.Equal(1u, view.ArrayLayers);
        }

        /// <summary>And a plain sampled texture does not, so the flag is not simply on for everything.</summary>
        [Fact]
        public void TheVulkanSampledView_LeavesAPlain2DTextureAlone()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuTexture texture = fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                16, 16, GpuTextureUsage.Sampled));

            VulkanImageViewSpec view = Assert.Single(fixture.Views);
            Assert.False(view.ArrayView);
        }

        /// <summary>
        /// THE INCUMBENT'S PADDING RULE, AS ROWS, with no device anywhere near it. <c>VeldridArrayLayers</c> is
        /// the whole of the one-layer-array emulation: it is the only place in the engine that hands a backend a
        /// layer count the caller did not ask for, and every consequence of the phantom slice (the narrowed copy
        /// path, the refused upload) exists because of what this returns. It ran untested until now.
        /// <para>
        /// STAGING IS EXCLUDED, deliberately. A staging texture is CPU-visible memory with no view and nothing
        /// that binds it to a shader, so there is no type for a pad to fix and array-ness cannot be observed on
        /// one at all. Padding it would only double the buffer a readback maps.
        /// </para>
        /// <para>
        /// There is no MULTISAMPLED row because there is no multisampled array to describe:
        /// <see cref="AMultisampledArrayIsRefused"/> is where that shape is pinned, at the description
        /// constructor that refuses it.
        /// </para>
        /// </summary>
        [Theory]
        // A one-layer ARRAY is the only thing that pads, and 2 is the whole workaround.
        [InlineData(GpuTextureUsage.Sampled, 1u, 1u, true, 2u)]
        // A plain 2D texture is left alone: the flag is what asks for the pad, not the layer count.
        [InlineData(GpuTextureUsage.Sampled, 1u, 1u, false, 1u)]
        // Above one layer Veldrid already infers the array type, so there is nothing to emulate.
        [InlineData(GpuTextureUsage.Sampled, 2u, 1u, true, 2u)]
        [InlineData(GpuTextureUsage.Sampled, 5u, 1u, true, 5u)]
        // A cubemap counts CUBES, so a second one would be six real faces, and IsArray does not claim the case.
        [InlineData(GpuTextureUsage.Sampled | GpuTextureUsage.Cubemap, 1u, 1u, true, 1u)]
        // A staging texture is never sampled and has no view, so it gets no pad.
        [InlineData(GpuTextureUsage.Staging, 1u, 1u, true, 1u)]
        public void TheIncumbentPadsAOneLayerArrayAndNothingElse(
            GpuTextureUsage usage, uint arrayLayers, uint sampleCount, bool isArray, uint expected)
        {
            var d = new GpuTextureDescription(8, 8, GpuPixelFormat.R8G8B8A8UNorm, usage,
                mipLevels: 1, arrayLayers: arrayLayers, sampleCount: sampleCount, isArray: isArray);

            Assert.Equal(expected, VeldridGpuDevice.VeldridArrayLayers(d));
        }

        /// <summary>
        /// A MULTISAMPLED ARRAY IS REFUSED AT DESCRIPTION TIME. No backend agrees on which type such a texture
        /// takes: Metal and Vulkan derive the multisample type first, and Direct3D 11's shader resource view
        /// nests its multisample test INSIDE the non-array arm, so the same description would take a plain
        /// <c>Texture2DArray</c> view over a multisampled resource there. Nothing in the engine asks for the
        /// shape, so the seam says so once rather than leaving three answers in place.
        /// </summary>
        [Theory]
        [InlineData(2u)]
        [InlineData(4u)]
        public void AMultisampledArrayIsRefused(uint sampleCount)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new GpuTextureDescription(
                8, 8, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget,
                mipLevels: 1, arrayLayers: 2, sampleCount: sampleCount));
        }

        /// <summary>And a multisampled texture that is NOT an array still builds, which is every MSAA render
        /// target the engine makes. Without this the refusal above would read as "no MSAA".</summary>
        [Fact]
        public void AMultisampledPlain2DTextureIsStillFine()
        {
            var d = new GpuTextureDescription(8, 8, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget,
                mipLevels: 1, arrayLayers: 1, sampleCount: 4);

            Assert.Equal(4u, d.SampleCount);
            Assert.False(d.IsArray);
        }
    }
}
