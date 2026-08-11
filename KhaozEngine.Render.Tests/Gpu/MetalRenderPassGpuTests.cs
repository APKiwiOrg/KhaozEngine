using System;
using System.Runtime.Versioning;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using KhaozEngine.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// RENDER PASSES ON REAL HARDWARE, WITH THE RESULT READ BACK AS PIXELS. Row 12 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>.
    ///
    /// <para><b>THE READBACK IS THE POINT, AND SKIPPING IT IS A NAMED FAILURE IN THIS REPOSITORY.</b> Section 18's
    /// row 17 records that a <c>[GpuFact]</c> which only asserts no-throw is how the all-black splat terrain
    /// shipped, and this row has two claims a completed command buffer cannot distinguish. M-A2 says a clear
    /// lands on the attachment the CALLER named, and a pass that cleared the wrong one completes with a nil error.
    /// M-A4 says the store action is set explicitly rather than left at the descriptor's discarding default, and a
    /// pass that threw its whole result away completes with a nil error too. So each is read as a texel.</para>
    ///
    /// <para><b>WHAT A RED RUN MEANS.</b> Every DECISION is covered device-free by
    /// <see cref="MetalRenderPassScheduleTests"/> and <see cref="MetalClearPolicyTests"/>, so a failure here is
    /// about the native calls underneath: <c>+renderPassDescriptor</c> and the retain around it,
    /// <c>-objectAtIndexedSubscript:</c> on an attachment array that is NOT an <c>NSArray</c>, the four-double
    /// <c>MTLClearColor</c> HFA, the depth and stencil slots, and the two plural setters M-A7 takes.</para>
    ///
    /// <para><b>DORMANT OFF macOS RATHER THAN SKIPPED</b>, which is phase 3's row-19 lesson: under
    /// <c>KE_GPU_TESTS=1</c> the Vulkan and Direct3D 11 legs run this assembly in strict mode where a skip is a
    /// failure, so each row returns early with the platform recorded instead.</para>
    ///
    /// <para><b>IT SITS IN <c>NativeDeviceLifecycle</c></b> because it builds a whole <c>MTLDevice</c> and queue
    /// beside the suite's own and registers that queue into the same four-slot process-static completion table.
    /// </para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class MetalRenderPassGpuTests
    {
        const uint Size = 4;

        static readonly Color Red = new(1f, 0f, 0f, 1f);
        static readonly Color Green = new(0f, 1f, 0f, 1f);
        static readonly Color Blue = new(0f, 0f, 1f, 1f);

        readonly ITestOutputHelper _output;

        public MetalRenderPassGpuTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// M-A2 AND M-A3 AND M-A4, ALL THREE READ AS PIXELS FROM ONE RECORDING. Three colour attachments, three
        /// different clear colours, no draw at all, and every one of them comes back its own colour.
        /// <para>
        /// THIS IS THE SHAPE <c>ModelRenderer.BeginModelPass</c> RECORDS, and under the incumbent attachments 1
        /// and 2 would come back holding whatever a freshly created <c>StorageModePrivate</c> texture holds,
        /// because every clear goes into <c>colorAttachments[0]</c>. It is also the clear-only pass: nothing draws,
        /// so the whole recording is a framebuffer, three clears and an <c>End</c>, and if M-A3's flush were
        /// missing the readback would find no clear at all. And it is the store action: at the descriptor's own
        /// default the result would be discarded rather than written out.
        /// </para>
        /// </summary>
        [GpuFact]
        public void ThreeAttachmentsClearToThreeColoursWithNoDrawAtAll()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            using IGpuTexture a = Target(device);
            using IGpuTexture b = Target(device);
            using IGpuTexture c = Target(device);
            using IGpuFramebuffer fb = device.Factory.CreateFramebuffer(null, a, b, c);

            using (MetalCommandList list = device.CreateCommandList())
            {
                list.Begin();
                list.SetFramebuffer(fb);
                list.ClearColorTarget(0, Red);
                list.ClearColorTarget(1, Green);
                list.ClearColorTarget(2, Blue);
                list.End();

                device.Submit(list);
            }

            device.WaitForIdle();

            Assert.Equal(Red, ReadFirstTexel(device, a));
            Assert.Equal(Green, ReadFirstTexel(device, b));
            Assert.Equal(Blue, ReadFirstTexel(device, c));

            _output.WriteLine("three attachments, three colours, no draw: the per-attachment clear (M-A2), the "
                + "clear-only flush (M-A3) and the explicit store action (M-A4) all read back.");
        }

        /// <summary>
        /// THE KILL SWITCH REPRODUCES THE DEFECT ON HARDWARE, which is what makes gate 1's A/B a comparison
        /// rather than a hope. Under <see cref="MetalClearMode.Attachment0"/> the same three calls leave
        /// attachment 0 holding the LAST colour and attachments 1 and 2 holding nothing anybody wrote.
        /// <para>
        /// THE LIST IS BUILT DIRECTLY RATHER THAN THROUGH THE DEVICE, because the device reads
        /// <c>KE_METAL_CLEAR</c> once per process and a test that mutated the environment would be racing every
        /// other row in the collection. The mode is a constructor argument for exactly this reason.
        /// </para>
        /// <para>
        /// ATTACHMENTS 1 AND 2 ARE ASSERTED ONLY TO BE NOT-THE-COLOUR-ASKED-FOR, not to hold any particular
        /// value. What an uncleared <c>StorageModePrivate</c> texture holds is undefined, and V-A6's rule (which
        /// M-A4 inherits) is precisely that undefined contents are not stable across runs. Asserting a specific
        /// value here would be asserting the instability this decision exists to end.
        /// </para>
        /// </summary>
        [GpuFact]
        public void TheIncumbentPositionLeavesTheOtherAttachmentsUncleared()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            using IGpuTexture a = Target(device);
            using IGpuTexture b = Target(device);
            using IGpuFramebuffer fb = device.Factory.CreateFramebuffer(null, a, b);

            using (MetalCommandList list = device.CreateCommandList(MetalClearMode.Attachment0))
            {
                list.Begin();
                list.SetFramebuffer(fb);
                list.ClearColorTarget(0, Red);
                list.ClearColorTarget(1, Green);
                list.End();

                device.Submit(list);
            }

            device.WaitForIdle();

            Assert.Equal(Green, ReadFirstTexel(device, a));
            Assert.NotEqual(Green, ReadFirstTexel(device, b));

            _output.WriteLine("attachment0 mode: both clears collapsed onto slot 0 and the second attachment was "
                + "never cleared, which is the defect M-A2 ends and the position gate 1 measures against.");
        }

        /// <summary>
        /// A DEPTH-STENCIL ATTACHMENT RECORDS AND COMPLETES, which is the one thing the device can say about the
        /// stencil slot that a device-free test cannot: naming a stencil attachment over a texture whose format
        /// carries no stencil plane is a validation error, and naming one over a texture that does is required.
        /// Both formats are recorded here, in one run, so the guard is exercised in both directions.
        /// <para>
        /// THERE IS NO PIXEL READ ON THIS ONE, and the reason is a real limit rather than a shortcut: reading a
        /// depth texture back needs a depth-format staging path the seam does not have, and the pixel claim that
        /// matters (a cleared depth buffer rejecting fragments) belongs to the first golden with a depth test in
        /// it. What IS read is the command-buffer outcome, which is where a validation failure lands.
        /// </para>
        /// </summary>
        [GpuFact]
        public void BothDepthFormatsRecordAPassAndTheBufferCompletesCleanly()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();

            foreach (GpuPixelFormat format in new[] { GpuPixelFormat.R32Float, GpuPixelFormat.D32FloatS8UInt })
            {
                using IGpuTexture colour = Target(device);
                using IGpuTexture depth = device.Factory.CreateTexture(
                    GpuTextureDescription.Texture2D(Size, Size, format, GpuTextureUsage.DepthStencil));
                using IGpuFramebuffer fb = device.Factory.CreateFramebuffer(depth, colour);

                using MetalCommandList list = device.CreateCommandList();
                list.Begin();
                list.SetFramebuffer(fb);
                list.ClearColorTarget(0, Red);
                list.ClearDepthStencil(0.5f);
                list.End();

                device.Submit(list);
                device.WaitForIdle();

                Assert.Null(device.Diagnostics.DeviceLossReason);
                Assert.Equal(Red, ReadFirstTexel(device, colour));

                _output.WriteLine($"{format}: stencil plane = {MetalFormats.IsStencilFormat(format)}, colour "
                    + "attachment cleared and stored.");
            }
        }

        /// <summary>
        /// THE TWO PLURAL SETTERS RECORD ON A REAL ENCODER (M-A7), which is the only place their ABI is
        /// exercised: they cross an array ADDRESS and a count rather than a by-value struct, and a wrong
        /// prototype here is a memory corruption rather than a compile error.
        /// <para>
        /// <c>PrepareDraw</c> IS CALLED DIRECTLY BECAUSE ROW 14 OWNS THE DRAW ITSELF
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/580). What this row can prove without it is that the
        /// pass opens, both setters are accepted, and the buffer completes.
        /// </para>
        /// </summary>
        [GpuFact]
        public void TheViewportAndTheScissorRecordOnARealRenderEncoder()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            using IGpuTexture colour = Target(device);
            using IGpuFramebuffer fb = device.Factory.CreateFramebuffer(null, colour);

            using (MetalCommandList list = device.CreateCommandList())
            {
                list.Begin();
                list.SetFramebuffer(fb);
                list.ClearColorTarget(0, Blue);

                // What row 11 does at SetPipeline. Without it the scissor is gated out, which is the behaviour
                // MetalRenderPassScheduleTests asserts and the reason this row sets it explicitly.
                list.Passes.SetScissorTestEnabled(true);
                list.SetScissorRect(0, 1, 1, 2, 2);

                Assert.NotEqual(IntPtr.Zero, list.Passes.PrepareDraw());
                Assert.False(list.Passes.ViewportOwed);
                Assert.False(list.Passes.ScissorOwed);

                list.End();
                device.Submit(list);
            }

            device.WaitForIdle();

            Assert.Null(device.Diagnostics.DeviceLossReason);
            Assert.Equal(Blue, ReadFirstTexel(device, colour));
        }

        /// <summary>
        /// THE FRAMEBUFFER'S OWN CONSTRUCTION RULES, which need real textures and therefore cannot be device-free.
        /// A staging texture is an <c>MTLBuffer</c> on this backend and carries no attachment handle at all, so it
        /// is refused by name instead of reaching the descriptor as a nil texture, and attachments of different
        /// sizes are refused because the render area is a single value.
        /// </summary>
        [GpuFact]
        public void AFramebufferRefusesAStagingTextureAndAMismatchedAttachment()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            using IGpuTexture colour = Target(device);
            using IGpuTexture staging = device.Factory.CreateTexture(
                GpuTextureDescription.Texture2D(Size, Size, GpuPixelFormat.B8G8R8A8UNorm,
                    GpuTextureUsage.Staging));
            using IGpuTexture larger = device.Factory.CreateTexture(
                GpuTextureDescription.Texture2D(Size * 2, Size * 2, GpuPixelFormat.B8G8R8A8UNorm,
                    GpuTextureUsage.RenderTarget));

            ArgumentException stagingRefusal = Assert.Throws<ArgumentException>(
                () => device.Factory.CreateFramebuffer(null, staging));
            Assert.Contains("Staging texture", stagingRefusal.Message, StringComparison.Ordinal);

            ArgumentException sizeRefusal = Assert.Throws<ArgumentException>(
                () => device.Factory.CreateFramebuffer(null, colour, larger));
            Assert.Contains("one size and one sample count", sizeRefusal.Message, StringComparison.Ordinal);

            ArgumentException emptyRefusal = Assert.Throws<ArgumentException>(
                () => device.Factory.CreateFramebuffer(null));
            Assert.Contains("at least one attachment", emptyRefusal.Message, StringComparison.Ordinal);

            // A NULL ELEMENT INSIDE THE ARRAY, which the array's own null check does not see. It is refused by
            // the attachment's parameter name rather than reaching the ownership cast, whose wrong-backend
            // message dereferences the resource to build itself.
            ArgumentNullException nullRefusal = Assert.Throws<ArgumentNullException>(
                () => device.Factory.CreateFramebuffer(null, colour, null!));
            Assert.Equal("colour", nullRefusal.ParamName);
        }

        /// <summary>The framebuffer reports the attachment formats a matching pipeline is created from, which is
        /// what <see cref="IGpuFramebuffer.Outputs"/> exists for and what row 11 reads.</summary>
        [GpuFact]
        public void AFramebufferReportsItsAttachmentFormatsAndExtent()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            using IGpuTexture colour = Target(device);
            using IGpuTexture depth = device.Factory.CreateTexture(
                GpuTextureDescription.Texture2D(Size, Size, GpuPixelFormat.D32FloatS8UInt,
                    GpuTextureUsage.DepthStencil));
            using IGpuFramebuffer fb = device.Factory.CreateFramebuffer(depth, colour);

            Assert.Equal(Size, fb.Width);
            Assert.Equal(Size, fb.Height);
            Assert.Equal(GpuPixelFormat.D32FloatS8UInt, fb.Outputs.Depth);
            Assert.Equal(GpuPixelFormat.B8G8R8A8UNorm, Assert.Single(fb.Outputs.Colour));
            Assert.Equal(1, fb.Outputs.SampleCount);
        }

        // ---- Fixtures ----------------------------------------------------------------------------------------

        [SupportedOSPlatform("macos")]
        static IGpuTexture Target(MetalGpuDevice device) => device.Factory.CreateTexture(
            GpuTextureDescription.Texture2D(Size, Size, GpuPixelFormat.B8G8R8A8UNorm,
                GpuTextureUsage.RenderTarget));

        /// <summary>
        /// Texel (0, 0) of <paramref name="texture"/>, through a blit into a Shared staging buffer and the
        /// engine's own <c>Map</c>. A <see cref="MTLStorageMode.Private"/> texture has no CPU pointer at all
        /// (M-M2), so a copy is the only route, and it goes through the same software subresource layout every
        /// golden readback uses.
        /// </summary>
        [SupportedOSPlatform("macos")]
        static Color ReadFirstTexel(MetalGpuDevice device, IGpuTexture texture)
        {
            using IGpuTexture staging = device.Factory.CreateTexture(
                GpuTextureDescription.Texture2D(Size, Size, GpuPixelFormat.B8G8R8A8UNorm,
                    GpuTextureUsage.Staging));

            var source = (MetalTexture)texture;
            var destination = (MetalTexture)staging;
            ulong rowPitch = MetalStagingLayout.RowPitch(Size, GpuPixelFormat.B8G8R8A8UNorm);

            using (MetalCommandList list = device.CreateCommandList())
            {
                list.Begin();

                IntPtr encoder = list.Encoders.EnsureBlitEncoder();
                Assert.NotEqual(IntPtr.Zero, encoder);

                new MTLBlitCommandEncoder(encoder).CopyFromTextureToBuffer(
                    source.Handle, 0, 0, new MTLOrigin(0, 0, 0), new MTLSize(Size, Size, 1),
                    destination.StagingBuffer, 0, (nuint)rowPitch, (nuint)(rowPitch * Size));

                list.End();
                device.Submit(list);
            }

            device.WaitForIdle();

            MappedData mapped = device.Map(staging, GpuMapMode.Read);
            try
            {
                // BGRA8 in memory order, which is what MTLPixelFormatBGRA8Unorm stores and what every golden
                // readback in this engine already decodes.
                byte[] texel = new byte[4];
                System.Runtime.InteropServices.Marshal.Copy(mapped.Data, texel, 0, 4);
                return new Color(texel[2] / 255f, texel[1] / 255f, texel[0] / 255f, texel[3] / 255f);
            }
            finally
            {
                device.Unmap(staging);
            }
        }

        [SupportedOSPlatform("macos")]
        static MetalGpuDevice CreateHeadless()
            => (MetalGpuDevice)new MetalBackendProvider().CreateHeadless().Device;

        // [SupportedOSPlatformGuard] rather than an inline check at every call site, which is the same mechanism
        // KhaozEngineMetal.IsPlatformSupported uses one level down.
        [SupportedOSPlatformGuard("macos")]
        bool Available()
        {
            if (!KhaozEngineMetal.IsPlatformSupported)
            {
                // KE_METAL_REQUIRED=1 turns this into a throw on the leg that declared a device mandatory.
                MetalDormancy.ThrowIfRequired("this is not macOS at all");
                _output.WriteLine("dormant: not macOS, so there is no Metal device to create.");
                return false;
            }

            string? missing = MetalSupportProbe.MissingRequirement();
            if (missing is null) return true;

            MetalDormancy.ThrowIfRequired(missing);
            _output.WriteLine("dormant: this machine cannot run the native Metal backend (" + missing + ").");
            return false;
        }
    }
}
