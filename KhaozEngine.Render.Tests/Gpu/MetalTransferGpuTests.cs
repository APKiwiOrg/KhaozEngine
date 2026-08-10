using System;
using System.Runtime.Versioning;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// ROW 14'S TRANSFER FAMILY ON REAL HARDWARE: the mip chain, the multisample resolve and the whole-texture
    /// copy, each read back as texels. Row 14 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/580).
    ///
    /// <para><b>THESE THREE ARE THE MEMBERS WHOSE ONLY HONEST WITNESS IS A DEVICE.</b> The routing (which of the
    /// four staging cases a copy is), the arithmetic (the byte offsets and pitches the staging side supplies) and
    /// every refusal are device-free and asserted on the Linux leg by <c>MetalTransferPlanTests</c> and
    /// <c>MetalTransferPathTests</c>. What no fake can answer is whether <c>-generateMipmapsForTexture:</c>
    /// actually FILLED level 1, whether a <c>storeAction = MultisampleResolve</c> pass actually averaged the
    /// samples into the attachment named as its resolve texture, and whether the copy selectors move the bytes
    /// the software layout says they do. Each of those completes with a nil error either way.</para>
    ///
    /// <para><b>WHICH IS WHY EVERY ROW READS A TEXEL AND NONE ASSERTS NO-THROW.</b> Section 18's row 17 records
    /// that a <c>[GpuFact]</c> asserting only no-throw is how the all-black splat terrain shipped, and the
    /// transfer family is the shape most able to repeat it: a mip level nobody generated and a resolve nobody
    /// performed both leave a texture that maps, reads and returns bytes.</para>
    ///
    /// <para><b>DORMANT OFF macOS RATHER THAN SKIPPED</b>, which is phase 3's row-19 lesson: under
    /// <c>KE_GPU_TESTS=1</c> the Vulkan and Direct3D 11 legs run this assembly in strict mode where a skip is a
    /// failure.</para>
    ///
    /// <para><b>IT SITS IN <c>NativeDeviceLifecycle</c></b> because it builds a whole <c>MTLDevice</c> and queue
    /// beside the suite's own and registers that queue into the same four-slot process-static completion table.
    /// </para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class MetalTransferGpuTests
    {
        const uint Size = 4;

        static readonly Color Amber = new(64f / 255f, 128f / 255f, 192f / 255f, 1f);

        readonly ITestOutputHelper _output;

        public MetalTransferGpuTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// <c>-generateMipmapsForTexture:</c> FILLS THE CHAIN, read out of level 1 through the general
        /// <c>CopyTextureSubresource</c> overload.
        /// <para>
        /// LEVEL 0 IS A UNIFORM COLOUR, so the correct level 1 is that same colour whatever filter the driver
        /// chooses, which is what keeps this a statement about the CHAIN rather than about a filter kernel nobody
        /// specified. A level nobody generated holds whatever a freshly created <c>StorageModePrivate</c> texture
        /// holds, and matching a specific colour by accident is not a thing that happens.
        /// </para>
        /// <para>
        /// AND IT IS ALSO THE SUBRESOURCE COPY'S OWN ROW. Reading level 1 means the copy names mip 1 of the
        /// SOURCE while the pitches come from mip 0 of the 2x2 staging texture, which is exactly the split
        /// <c>MetalBufferImageRegion</c> warns is easy to get backwards. Getting it wrong reads the wrong bytes
        /// rather than failing.
        /// </para>
        /// </summary>
        [GpuFact]
        public void GeneratingAMipChainFillsLevelOneAndTheSubresourceCopyReadsIt()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            IGpuResourceFactory factory = device.Factory;

            using IGpuTexture mipped = factory.CreateTexture(new GpuTextureDescription(Size, Size,
                GpuPixelFormat.B8G8R8A8UNorm,
                GpuTextureUsage.Sampled | GpuTextureUsage.RenderTarget | GpuTextureUsage.GenerateMipmaps,
                mipLevels: 3));

            // A UNIFORM LEVEL 0, uploaded through the device-level path row 6 built.
            var level0 = new byte[Size * Size * 4];
            for (int i = 0; i < level0.Length; i += 4)
            {
                level0[i] = 192;      // B
                level0[i + 1] = 128;  // G
                level0[i + 2] = 64;   // R
                level0[i + 3] = 255;  // A
            }

            device.UpdateTexture(mipped, level0, 0, 0, Size, Size);

            using IGpuTexture staging = factory.CreateTexture(GpuTextureDescription.Texture2D(
                Size / 2, Size / 2, GpuPixelFormat.B8G8R8A8UNorm, GpuTextureUsage.Staging));

            using (MetalCommandList list = device.CreateCommandList())
            {
                list.Begin();
                list.GenerateMipmaps(mipped);
                list.CopyTextureSubresource(mipped, 1, 0, staging, 0, 0, Size / 2, Size / 2);
                list.End();
                device.Submit(list);
            }

            device.WaitForIdle();

            Assert.Null(device.Diagnostics.DeviceLossReason);

            Color texel = ReadStagingTexel(device, staging, Size / 2);
            _output.WriteLine($"mip 1 read back {texel}, wanted the level-0 colour {Amber}.");

            Assert.Equal(Amber, texel);
        }

        /// <summary>
        /// M-C4's STANDALONE RESOLVE, read as a texel: a multisampled attachment cleared to a colour, resolved
        /// into a single-sample texture, and the single-sample texture holds the colour.
        /// <para>
        /// A UNIFORM CLEAR RESOLVES TO ITSELF at any sample count, which is what makes this a statement about the
        /// RESOLVE rather than about a sample-averaging rule. What it separates is the two outcomes a completed
        /// command buffer cannot: a pass whose <c>storeAction</c> stayed at the descriptor's discarding default,
        /// and one whose <c>resolveTexture</c> was never named, both of which leave the destination holding
        /// whatever an untouched Private texture holds.
        /// </para>
        /// <para>
        /// <b>AND THE CLEAR IS A SEPARATE RECORDING, WHICH IS A FACT THIS ROW FOUND RATHER THAN A SHAPE IT
        /// CHOSE.</b> A resolve does not flush a clear-only pass: M-A3's flush sits at the incumbent's two
        /// forcing sites and row 12 declined to add a third at every illegal command. The incumbent's own
        /// <c>ResolveTextureCore</c> leaves its pending clears pending for the same reason, so
        /// clear-then-resolve-with-no-draw resolves pre-clear contents on BOTH implementations. That is parity
        /// holding rather than a defect, no real MSAA pass produces it (a real one draws), and the body says so
        /// at the line where it matters.
        /// </para>
        /// <para>
        /// THE SOURCE IS NOT ASSERTED AFTERWARDS, deliberately. Metal's resolve store action does not also store
        /// the multisampled attachment, so the source is undefined once this returns, and that divergence from
        /// <c>ResolveSubresource</c> and <c>vkCmdResolveImage</c> is reproduced rather than fixed and is written
        /// down in the package README. Asserting anything about it would be asserting the instability M-A4 rules
        /// against everywhere else.
        /// </para>
        /// </summary>
        [GpuFact]
        public void AMultisampledTargetResolvesIntoItsSingleSampleDestination()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            IGpuResourceFactory factory = device.Factory;

            using IGpuTexture multisampled = factory.CreateTexture(new GpuTextureDescription(Size, Size,
                GpuPixelFormat.B8G8R8A8UNorm, GpuTextureUsage.RenderTarget, mipLevels: 1, arrayLayers: 1,
                sampleCount: 4));
            using IGpuTexture resolved = factory.CreateTexture(GpuTextureDescription.Texture2D(Size, Size,
                GpuPixelFormat.B8G8R8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = factory.CreateFramebuffer(null, multisampled);

            // THE CLEAR IS ITS OWN RECORDING, AND THAT IS A FINDING RATHER THAN A CONVENIENCE. A resolve does
            // NOT flush a clear-only pass. Row 12 decided that in as many words: M-A3's flush stays at the
            // incumbent's TWO forcing sites, a framebuffer change and End, rather than at every command that
            // opens a different encoder kind, because the superset costs an encoder pair the budget counts and
            // buys one behaviour parity does not want. The incumbent's own ResolveTextureCore calls plain
            // EnsureNoRenderPass and leaves its pending clears pending for exactly the same reason. So
            // clear-then-resolve-with-no-draw resolves the source as it stood BEFORE the clear on both
            // implementations, which is parity holding rather than either one being wrong, and no real MSAA pass
            // produces it because a real one draws. Written as two recordings so this row is about the RESOLVE.
            using (MetalCommandList clear = device.CreateCommandList())
            {
                clear.Begin();
                clear.SetFramebuffer(fb);
                clear.ClearColorTarget(0, Amber);
                clear.End();
                device.Submit(clear);
            }

            device.WaitForIdle();

            using (MetalCommandList list = device.CreateCommandList())
            {
                list.Begin();
                list.ResolveTexture(multisampled, resolved);
                list.End();
                device.Submit(list);
            }

            device.WaitForIdle();

            Assert.Null(device.Diagnostics.DeviceLossReason);

            Color texel = ReadFirstTexel(device, resolved);
            _output.WriteLine($"resolve read back {texel}, wanted {Amber}.");

            Assert.Equal(Amber, texel);
        }

        /// <summary>
        /// THE WHOLE-TEXTURE COPY OVER A MIPPED TEXTURE, which is the one shape that emits MORE THAN ONE region
        /// from a single seam call: one per mip level per array layer, with the extents halving as it goes.
        /// <para>
        /// EVERY GOLDEN IN THE SUITE READS BACK THROUGH THIS MEMBER, so what a wrong region loop costs is not one
        /// test: it is 36 goldens whose pixels are in the wrong places, with the copy completing cleanly. Reading
        /// BOTH level 0 and level 1 out of the destination is what separates "the loop ran once" from "the loop
        /// ran per level", which a single-level read cannot.
        /// </para>
        /// </summary>
        [GpuFact]
        public void AWholeTextureCopyMovesEveryMipLevel()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            IGpuResourceFactory factory = device.Factory;

            using IGpuTexture source = factory.CreateTexture(new GpuTextureDescription(Size, Size,
                GpuPixelFormat.B8G8R8A8UNorm,
                GpuTextureUsage.Sampled | GpuTextureUsage.RenderTarget | GpuTextureUsage.GenerateMipmaps,
                mipLevels: 2));
            using IGpuTexture staging = factory.CreateTexture(new GpuTextureDescription(Size, Size,
                GpuPixelFormat.B8G8R8A8UNorm, GpuTextureUsage.Staging, mipLevels: 2));

            var level0 = new byte[Size * Size * 4];
            for (int i = 0; i < level0.Length; i += 4)
            {
                level0[i] = 192;
                level0[i + 1] = 128;
                level0[i + 2] = 64;
                level0[i + 3] = 255;
            }

            device.UpdateTexture(source, level0, 0, 0, Size, Size);

            using (MetalCommandList list = device.CreateCommandList())
            {
                list.Begin();
                list.GenerateMipmaps(source);
                list.CopyTexture(source, staging);
                list.End();
                device.Submit(list);
            }

            device.WaitForIdle();

            Assert.Null(device.Diagnostics.DeviceLossReason);

            var typed = (MetalTexture)staging;
            Assert.Equal(Amber, ReadStagingSubresource(device, staging, typed.SubresourceLayout(0, 0), 0));
            Assert.Equal(Amber, ReadStagingSubresource(device, staging, typed.SubresourceLayout(1, 0), 1));

            _output.WriteLine("both mip levels came across in one CopyTexture, which is the readback path every "
                + "golden in the suite goes through.");
        }

        /// <summary>
        /// EVERY TEXTURE-SHAPE REFUSAL, WHICH IS A DEVICE ROW ONLY BECAUSE A <c>MetalTexture</c> CANNOT BE BUILT
        /// WITHOUT ONE. The decisions here are as device-free as any other, and they are not tested that way for
        /// a mechanical reason worth writing down: the four texture members type-check their arguments to the
        /// concrete <c>MetalTexture</c> through <c>MetalResourceOwnership.Require</c>, whose only factory is
        /// <c>Create(MTLDevice, ...)</c> under a macOS guard, so no fake substitutes and no harness can stand one
        /// up. <c>MetalRenderPassScheduleTests</c> already records the same constraint.
        ///
        /// <para><b>SO THEY ARE ASSERTED TOGETHER RATHER THAN ONE PER ROW, and each is asserted by its own
        /// MESSAGE rather than by its exception type.</b> All of them are <c>ArgumentException</c> or
        /// <c>ArgumentOutOfRangeException</c>, so a row that only checked the type would pass while the wrong
        /// guard fired, which is the failure mode a refusal test exists to close. What each one prevents is a
        /// copy that CLIPS rather than throws: a whole-texture copy between mismatched shapes, a subresource
        /// index past the end, a chain generated from a texture with no chain, and a resolve whose two sides are
        /// not a multisampled and a single-sample pair.</para>
        /// </summary>
        [GpuFact]
        public void EveryTextureShapeRefusalFiresWithItsOwnMessage()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            IGpuResourceFactory factory = device.Factory;

            using IGpuTexture small = Target(factory, Size);
            using IGpuTexture large = Target(factory, Size * 2);
            using IGpuTexture staging = factory.CreateTexture(GpuTextureDescription.Texture2D(Size, Size,
                GpuPixelFormat.B8G8R8A8UNorm, GpuTextureUsage.Staging));
            using IGpuTexture multisampled = factory.CreateTexture(new GpuTextureDescription(Size, Size,
                GpuPixelFormat.B8G8R8A8UNorm, GpuTextureUsage.RenderTarget, mipLevels: 1, arrayLayers: 1,
                sampleCount: 4));

            using MetalCommandList list = device.CreateCommandList();
            list.Begin();

            Assert.Contains("do not agree on",
                Assert.Throws<ArgumentException>(() => list.CopyTexture(small, large)).Message,
                StringComparison.Ordinal);

            Assert.Contains("mip level(s)",
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => list.CopyTextureSubresource(small, 7, 0, staging, Size, Size)).Message,
                StringComparison.Ordinal);

            // THE DESTINATION SIDE HAS ITS OWN CHECK, and it names "destination" rather than "source", which is
            // the half a single shared guard would lose.
            Assert.Contains("destination",
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => list.CopyTextureSubresource(small, 0, 0, staging, 4, 0, Size, Size)).Message,
                StringComparison.Ordinal);

            Assert.Contains("generateMipmapsForTexture",
                Assert.Throws<ArgumentException>(() => list.GenerateMipmaps(small)).Message,
                StringComparison.Ordinal);

            Assert.Contains("STAGING texture",
                Assert.Throws<ArgumentException>(() => list.GenerateMipmaps(staging)).Message,
                StringComparison.Ordinal);

            // THE FOUR WAYS A RESOLVE IS NOT ONE, each through the same named refusal: a single-sample source, a
            // multisampled destination, a size mismatch, and a staging side.
            Assert.Throws<ArgumentException>(() => list.ResolveTexture(small, small));
            Assert.Throws<ArgumentException>(() => list.ResolveTexture(small, multisampled));
            Assert.Throws<ArgumentException>(() => list.ResolveTexture(multisampled, large));
            Assert.Contains("multisample resolve",
                Assert.Throws<ArgumentException>(() => list.ResolveTexture(multisampled, staging)).Message,
                StringComparison.Ordinal);

            list.End();

            _output.WriteLine("nine texture-shape refusals, each read by its own message rather than by its "
                + "exception type.");
        }

        // ---- Fixtures ----------------------------------------------------------------------------------------

        /// <summary>Texel (0, 0) of a non-staging texture, through the seam's own <c>CopyTexture</c>.</summary>
        [SupportedOSPlatform("macos")]
        static Color ReadFirstTexel(MetalGpuDevice device, IGpuTexture texture)
        {
            using IGpuTexture staging = device.Factory.CreateTexture(
                GpuTextureDescription.Texture2D(texture.Width, texture.Height, texture.Format,
                    GpuTextureUsage.Staging));

            using (MetalCommandList list = device.CreateCommandList())
            {
                list.Begin();
                list.CopyTexture(texture, staging);
                list.End();
                device.Submit(list);
            }

            device.WaitForIdle();

            return ReadStagingTexel(device, staging, texture.Width);
        }

        /// <summary>Texel (0, 0) of a staging texture's mip 0, decoded from BGRA8 memory order.</summary>
        [SupportedOSPlatform("macos")]
        static Color ReadStagingTexel(MetalGpuDevice device, IGpuTexture staging, uint width)
        {
            _ = width;

            MappedData mapped = device.Map(staging, GpuMapMode.Read);
            try
            {
                byte[] texel = new byte[4];
                System.Runtime.InteropServices.Marshal.Copy(mapped.Data, texel, 0, 4);
                return new Color(texel[2] / 255f, texel[1] / 255f, texel[0] / 255f, texel[3] / 255f);
            }
            finally
            {
                device.Unmap(staging);
            }
        }

        /// <summary>
        /// Texel (0, 0) of one SUBRESOURCE of a staging texture, at the offset the software layout puts it.
        /// <para>
        /// The offset is read off <c>MetalStagingLayout</c> rather than recomputed here, which is the whole point:
        /// this row asserts that the COPY put bytes where that arithmetic says they are, and a second arithmetic
        /// in the test would let both be wrong together.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        static Color ReadStagingSubresource(MetalGpuDevice device, IGpuTexture staging,
            MetalSubresourceLayout layout, uint subresource)
        {
            _ = subresource;

            MappedData mapped = device.Map(staging, GpuMapMode.Read);
            try
            {
                byte[] texel = new byte[4];
                System.Runtime.InteropServices.Marshal.Copy(mapped.Data + (int)layout.Offset, texel, 0, 4);
                return new Color(texel[2] / 255f, texel[1] / 255f, texel[0] / 255f, texel[3] / 255f);
            }
            finally
            {
                device.Unmap(staging);
            }
        }

        [SupportedOSPlatform("macos")]
        static IGpuTexture Target(IGpuResourceFactory factory, uint size)
            => factory.CreateTexture(GpuTextureDescription.Texture2D(size, size,
                GpuPixelFormat.B8G8R8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));

        [SupportedOSPlatform("macos")]
        static MetalGpuDevice CreateHeadless()
            => (MetalGpuDevice)new MetalBackendProvider().CreateHeadless().Device;

        [SupportedOSPlatformGuard("macos")]
        bool Available()
        {
            if (!KhaozEngineMetal.IsPlatformSupported)
            {
                _output.WriteLine("dormant: not macOS, so there is no Metal device to create.");
                return false;
            }

            string? missing = MetalSupportProbe.MissingRequirement();
            if (missing is null) return true;

            _output.WriteLine("dormant: this machine cannot run the native Metal backend (" + missing + ").");
            return false;
        }
    }
}
