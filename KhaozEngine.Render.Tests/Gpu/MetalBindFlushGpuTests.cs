using System;
using System.Linq;
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
    /// EVERY ARGUMENT-TABLE SELECTOR THIS ROW ADDS, SENT TO A REAL METAL ENCODER. Row 13 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/579).
    ///
    /// <para><b>THIS FILE EXISTS BECAUSE OF ONE RULE: A PROTOTYPE WITH NO TEST THAT RUNS IT IS AN OBJECTIVE-C
    /// DECLARATION NOBODY HAS EVER EXECUTED.</b> Row 1's own regression evidence is that a wrong ABI assumption
    /// in interop is a memory corruption rather than a compile error, which is why the design has each row add
    /// its native prototypes only alongside the caller and the test that drives them. TWELVE selectors arrive
    /// with this row: eight on the render encoder (a buffer, a texture and a sampler array setter plus an offset
    /// setter, each spelled once per stage) and four on the compute encoder, which is a different protocol whose
    /// selectors carry no stage word at all. They go through three new <c>objc_msgSend</c> shapes, one of which
    /// passes an <c>NSRange</c> BY VALUE: sixteen bytes of integers is exactly the arm64 boundary, so it rides
    /// two general registers rather than going indirectly, and getting that wrong shifts every argument after it
    /// with nothing after it to notice.</para>
    ///
    /// <para><b>ALL TWELVE ARE SENT HERE, AND COUNTING THEM WAS THE POINT.</b> This file first shipped claiming
    /// seven and sending ten: its only render fixture has a vertex function that reads ONE BUFFER, so
    /// <c>setVertexTextures:withRange:</c> and <c>setVertexSamplerStates:withRange:</c> were unreachable from it
    /// and had never been executed anywhere, by this row or by row 1's spike. A wrong selector string is an
    /// unrecognised-selector abort, so the other ten being fine says nothing about those two, and row 14's first
    /// <c>Water</c> draw would have been where a Mac found out.
    /// <see cref="TheVertexStageTextureAndSamplerSettersAreAcceptedByARealEncoder"/> binds a program whose vertex
    /// stage samples, and asserts the executed set off <see cref="RecordingMetalEncoderSink"/>'s log rather than
    /// from the absence of a throw.</para>
    ///
    /// <para><b>WHAT A RED RUN HERE MEANS, AND WHAT IT DOES NOT.</b> Every DECISION is covered device-free by
    /// <see cref="MetalBindRecordsTests"/>, <see cref="MetalBindFlushTests"/>,
    /// <see cref="MetalArgumentBatchTests"/> and <see cref="MetalBindBudgetTests"/>, which run on the free Linux
    /// leg on every <c>dotnet test</c>. A failure here is about the native calls underneath and nothing else.
    /// </para>
    ///
    /// <para><b>THERE IS NO PIXEL TO READ BACK YET AND THAT IS A SCHEDULING FACT RATHER THAN A GAP.</b> A bind is
    /// only observable through a DRAW, and the draw path is row 14's
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/580), which is also where the goldens start passing. What
    /// this row can prove on hardware is that every selector is accepted by a real encoder with the arguments
    /// this backend composes, that the command buffer completes with a nil error, and that the device records no
    /// loss. Section 18's row 17 records that a <c>[GpuFact]</c> asserting only no-throw is how the all-black
    /// splat terrain shipped, so it is worth being explicit that these rows assert exactly that and why it is the
    /// most this row can assert: the alternative would be a bind test that quietly waits for row 14 and never
    /// runs the prototypes at all, which is the outcome the rule above exists to prevent.</para>
    ///
    /// <para><b>DORMANT OFF macOS RATHER THAN SKIPPED</b>, which is phase 3's row-19 lesson: under
    /// <c>KE_GPU_TESTS=1</c> the Vulkan and Direct3D 11 legs run this assembly in strict mode where a skip is a
    /// failure, so each row returns early with the platform recorded instead.</para>
    ///
    /// <para><b>IT SITS IN <c>NativeDeviceLifecycle</c></b> because it builds a whole <c>MTLDevice</c> and queue
    /// beside the suite's own and registers that queue into the same process-static completion table.
    /// </para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class MetalBindFlushGpuTests
    {
        const uint Size = 4;

        static readonly Color Blue = new(0f, 0f, 1f, 1f);

        readonly ITestOutputHelper _output;

        public MetalBindFlushGpuTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// THE PARTIAL-STAGE ACTIVATION ON A REAL RENDER ENCODER: <c>setVertexBuffers:offsets:withRange:</c>,
        /// <c>setFragmentBuffers:offsets:withRange:</c>, <c>setFragmentTextures:withRange:</c> and
        /// <c>setFragmentSamplerStates:withRange:</c>, at the indices a real index table read out of a real MSL
        /// emission put the elements at, over real <c>MTLBuffer</c>, <c>MTLTexture</c> and <c>MTLSamplerState</c>
        /// objects, with offsets composed against a real uniform ring. The vertex stage of this fixture reads one
        /// buffer, which is what makes it the partial-stage row and also what leaves the two vertex-stage
        /// selectors to <see cref="TheVertexStageTextureAndSamplerSettersAreAcceptedByARealEncoder"/>.
        /// <para>
        /// THE OFFSET IS ALIGNED AND THAT IS LOAD-BEARING RATHER THAN INCIDENTAL. An unaligned buffer offset is a
        /// validation error under the debug layer M-T7 arms on every run and undefined behaviour without it,
        /// which is one of this row's three named regressions. The composition is a 256-byte segment base plus a
        /// 256-byte caller offset, so what reaches the driver here is aligned by construction.
        /// </para>
        /// </summary>
        [GpuFact]
        public void EveryRenderArgumentTableSetterIsAcceptedByARealEncoder()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            IGpuResourceFactory factory = device.Factory;

            MetalShaderIndexTable table = MetalShaderBuild.Pair(
                MetalBindProgram.VertexGlsl, MetalBindProgram.FragmentGlsl, "MetalBindProgram").Table;

            using IGpuResourceLayout layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                Element("Frame", GpuResourceKind.UniformBuffer, dynamic: true),
                Element("Material", GpuResourceKind.UniformBuffer),
                Element("Albedo", GpuResourceKind.TextureReadOnly),
                Element("Samp", GpuResourceKind.Sampler)));

            // The shape check row 11 runs at pipeline creation, run here instead, because the whole point of
            // binding through the table is that the declared array and the reflected one describe one thing.
            table.RequireLayoutShape([((MetalResourceLayout)layout).Description], "MetalBindProgram");

            using IGpuBuffer frame = factory.CreateBuffer(
                new GpuBufferDescription(1024, GpuBufferUsage.UniformBuffer));
            using IGpuBuffer material = factory.CreateBuffer(
                new GpuBufferDescription(256, GpuBufferUsage.UniformBuffer));
            using IGpuTexture albedo = factory.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            using IGpuSampler sampler = factory.CreateSampler(GpuSamplerDescription.Linear);

            // THE DYNAMIC ELEMENT IS BOUND AS A WINDOW AND NOT AS THE WHOLE BUFFER, and that is a REQUIREMENT
            // rather than a preference. A bare-buffer binding takes the buffer's logical size as its range, and
            // the ring stride is that size rounded up, so range == stride and M-M4 leaves no room for a caller
            // offset of any size at all. The device run caught this file asking for exactly that, which is the
            // refusal firing on the live path with the caller's real offset in hand, where the same shared check
            // at set creation passes a zero and cannot fire.
            using IGpuResourceSet set = factory.CreateResourceSet(new GpuResourceSetDescription(
                layout, new GpuBufferRange(frame, 0, 256), material, albedo, sampler));

            using IGpuTexture colour = Target(device);
            using IGpuFramebuffer fb = factory.CreateFramebuffer(null, colour);
            using IGpuBuffer stream = factory.CreateBuffer(
                new GpuBufferDescription(256, GpuBufferUsage.VertexBuffer));

            var sink = new MetalEncoderSink();

            using (MetalCommandList list = device.CreateCommandList())
            {
                list.Begin();
                list.SetFramebuffer(fb);
                list.ClearColorTarget(0, Blue);

                // What row 11 does at SetPipeline, which is the seam this row defines and that row wires.
                list.GraphicsBinds.SetIndexTable(table);
                list.SetGraphicsResourceSet(0, set, 256);

                IntPtr encoder = list.Passes.PrepareDraw();
                Assert.NotEqual(IntPtr.Zero, encoder);

                list.FlushGraphicsBinds(ref sink, encoder);

                // AND THE VERTEX STREAM, which goes through the SAME array setter at the top of the buffer
                // table (M-B2). What row 14 wires is SetVertexBuffer, and the cache underneath is this row's.
                list.VertexStreams.Record(0, ((MetalBuffer)stream).Handle.Handle, 0);
                list.FlushVertexStreams(ref sink, encoder);

                // The offsets-only path, which is a different selector rather than a cheaper variant (M-R7).
                list.SetGraphicsResourceSet(0, set, 512);
                list.FlushGraphicsBinds(ref sink, encoder);

                list.End();
                device.Submit(list);
            }

            device.WaitForIdle();

            Assert.Null(device.Diagnostics.DeviceLossReason);
            _output.WriteLine("the four render argument-table setters and the two offset setters were accepted "
                + "by a real MTLRenderCommandEncoder, and the buffer completed with no error.");
        }

        /// <summary>
        /// THE VERTEX STAGE'S TEXTURE AND SAMPLER TABLES, WHICH NOTHING HAD EVER WRITTEN INTO ON A DEVICE.
        /// <c>setVertexTextures:withRange:</c> and <c>setVertexSamplerStates:withRange:</c> were declared, called
        /// by the flush for any program that references them, and unreachable from every fixture this row had:
        /// the one above has a vertex function that reads a single buffer, and row 1's interop spike never sent
        /// them either. A wrong selector string is an unrecognised-selector abort rather than a wrong pixel, so
        /// "the other four were fine" is not evidence about these two.
        /// <para>
        /// THE PROGRAM IS THE SHAPE ROW 14's FIRST WATER DRAW HAS, which is the shipped catalog's worst full
        /// activation for exactly this reason: two stages that between them read all three argument tables. Both
        /// stages also read the dynamic uniform, so the second flush drives BOTH offset setters as well.
        /// </para>
        /// <para>
        /// AND WHAT MAKES IT AN ASSERTION RATHER THAN A NO-THROW IS THE SINK'S OWN LOG.
        /// <see cref="RecordingMetalEncoderSink"/> sends each call to the live encoder and then records it, so
        /// what is asserted below is the set of selector paths that actually executed on this device. Section
        /// 18's row 17 records that a <c>[GpuFact]</c> asserting only no-throw is how the all-black splat terrain
        /// shipped, and a no-throw row cannot tell "the call was accepted" from "the call was never made".
        /// </para>
        /// </summary>
        [GpuFact]
        public void TheVertexStageTextureAndSamplerSettersAreAcceptedByARealEncoder()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            IGpuResourceFactory factory = device.Factory;

            MetalShaderIndexTable table = MetalShaderBuild.Pair(
                MetalVertexSamplingProgram.VertexGlsl, MetalVertexSamplingProgram.FragmentGlsl,
                "MetalVertexSamplingProgram").Table;

            // THE PREMISE, READ OFF THE TABLE: the vertex function really does reference a texture and a
            // sampler, so the flush really will reach the two selectors this row exists for.
            Assert.True(table.TryGetIndex(0, MetalVertexSamplingProgram.TextureBinding, MetalShaderStage.Vertex,
                out _));
            Assert.True(table.TryGetIndex(0, MetalVertexSamplingProgram.SamplerBinding, MetalShaderStage.Vertex,
                out _));

            using IGpuResourceLayout layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                Element("Frame", GpuResourceKind.UniformBuffer, dynamic: true),
                Element("Material", GpuResourceKind.UniformBuffer),
                Element("Albedo", GpuResourceKind.TextureReadOnly),
                Element("Samp", GpuResourceKind.Sampler)));

            table.RequireLayoutShape([((MetalResourceLayout)layout).Description], "MetalVertexSamplingProgram");

            using IGpuBuffer frame = factory.CreateBuffer(
                new GpuBufferDescription(1024, GpuBufferUsage.UniformBuffer));
            using IGpuBuffer material = factory.CreateBuffer(
                new GpuBufferDescription(256, GpuBufferUsage.UniformBuffer));
            using IGpuTexture albedo = factory.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            using IGpuSampler sampler = factory.CreateSampler(GpuSamplerDescription.Linear);

            // A WINDOW rather than the whole buffer, for the reason the row above records.
            using IGpuResourceSet set = factory.CreateResourceSet(new GpuResourceSetDescription(
                layout, new GpuBufferRange(frame, 0, 256), material, albedo, sampler));

            using IGpuTexture colour = Target(device);
            using IGpuFramebuffer fb = factory.CreateFramebuffer(null, colour);

            var calls = new FakeMetalEncoderCalls();
            var sink = new RecordingMetalEncoderSink(calls);

            using (MetalCommandList list = device.CreateCommandList())
            {
                list.Begin();
                list.SetFramebuffer(fb);
                list.ClearColorTarget(0, Blue);

                list.GraphicsBinds.SetIndexTable(table);
                list.SetGraphicsResourceSet(0, set, 256);

                IntPtr encoder = list.Passes.PrepareDraw();
                Assert.NotEqual(IntPtr.Zero, encoder);

                list.FlushGraphicsBinds(ref sink, encoder);

                list.SetGraphicsResourceSet(0, set, 512);
                list.FlushGraphicsBinds(ref sink, encoder);

                list.End();
                device.Submit(list);
            }

            device.WaitForIdle();

            Assert.Null(device.Diagnostics.DeviceLossReason);

            // EVERY RENDER SELECTOR THIS ROW ADDED, CONFIRMED TO HAVE EXECUTED. Three array setters on each of
            // two stages, plus the two offset setters on the rebind.
            Assert.Equal(1, Sent(calls, MetalShaderStage.Vertex, MetalIndexSpace.Buffer));
            Assert.Equal(1, Sent(calls, MetalShaderStage.Vertex, MetalIndexSpace.Texture));
            Assert.Equal(1, Sent(calls, MetalShaderStage.Vertex, MetalIndexSpace.Sampler));
            Assert.Equal(1, Sent(calls, MetalShaderStage.Fragment, MetalIndexSpace.Buffer));
            Assert.Equal(1, Sent(calls, MetalShaderStage.Fragment, MetalIndexSpace.Texture));
            Assert.Equal(1, Sent(calls, MetalShaderStage.Fragment, MetalIndexSpace.Sampler));

            Assert.Equal(
                new[] { MetalShaderStage.Vertex, MetalShaderStage.Fragment },
                calls.OffsetWrites.Select(o => o.Stage).ToArray());

            foreach (string line in calls.Log) _output.WriteLine(line);
            _output.WriteLine("all eight MTLRenderCommandEncoder argument-table selectors executed against a "
                + "real encoder, including the vertex stage's texture and sampler tables, and the buffer "
                + "completed with no error.");
        }

        /// <summary>
        /// THE COMPUTE SIBLINGS, WHICH ARE A DIFFERENT PROTOCOL AND THEREFORE DIFFERENT SELECTORS:
        /// <c>setBuffers:offsets:withRange:</c>, <c>setTextures:withRange:</c>,
        /// <c>setSamplerStates:withRange:</c> and <c>setBufferOffset:atIndex:</c>, all unprefixed because a
        /// compute encoder has one stage. Sending a <c>setVertexBuffers:</c> to one is an unrecognised selector,
        /// which is why <see cref="MetalEncoderSink"/> forks on the stage before it picks a receiver at all.
        /// </summary>
        [GpuFact]
        public void EveryComputeArgumentTableSetterIsAcceptedByARealEncoder()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            IGpuResourceFactory factory = device.Factory;

            (MetalMslProgram program, _, _, _) = MetalShaderBuild.Compute(ComputeGlsl, "MetalBindCompute");
            MetalShaderIndexTable table = program.Table;

            using IGpuResourceLayout layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Params", GpuResourceKind.UniformBuffer, GpuShaderStages.Compute,
                    true),
                new GpuResourceLayoutElement("Out", GpuResourceKind.TextureReadWrite, GpuShaderStages.Compute),
                new GpuResourceLayoutElement("Src", GpuResourceKind.TextureReadOnly, GpuShaderStages.Compute),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Compute)));

            table.RequireLayoutShape([((MetalResourceLayout)layout).Description], "MetalBindCompute");

            using IGpuBuffer parameters = factory.CreateBuffer(
                new GpuBufferDescription(1024, GpuBufferUsage.UniformBuffer));
            using IGpuTexture target = factory.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Storage));
            using IGpuTexture source = factory.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            using IGpuSampler sampler = factory.CreateSampler(GpuSamplerDescription.Linear);

            // A WINDOW rather than the whole buffer, for the render row's reason above.
            using IGpuResourceSet set = factory.CreateResourceSet(new GpuResourceSetDescription(
                layout, new GpuBufferRange(parameters, 0, 256), target, source, sampler));

            var sink = new MetalEncoderSink();

            using (MetalCommandList list = device.CreateCommandList())
            {
                list.Begin();

                list.ComputeBinds.SetIndexTable(table);
                list.SetComputeResourceSet(0, set, 256);

                IntPtr encoder = list.Encoders.EnsureComputeEncoder();
                Assert.NotEqual(IntPtr.Zero, encoder);

                list.FlushComputeBinds(ref sink, encoder);

                list.SetComputeResourceSet(0, set, 0);
                list.FlushComputeBinds(ref sink, encoder);

                list.End();
                device.Submit(list);
            }

            device.WaitForIdle();

            Assert.Null(device.Diagnostics.DeviceLossReason);
            _output.WriteLine("the three compute array setters and setBufferOffset:atIndex: were accepted by a "
                + "real MTLComputeCommandEncoder, and the buffer completed with no error.");
        }

        // A COMPUTE KERNEL THAT REFERENCES ALL FOUR ELEMENTS, so the emission puts all four into the one stage's
        // tables and the flush has something to write into each of the three. It writes a texel so nothing is
        // optimised away, and nothing dispatches it: the dispatch is row 14's.
        const string ComputeGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 8) in;
layout(set = 0, binding = 0) uniform Params { vec4 Tint; };
layout(set = 0, binding = 1, rgba8) uniform writeonly image2D Out;
layout(set = 0, binding = 2) uniform texture2D Src;
layout(set = 0, binding = 3) uniform sampler Samp;
void main() {
    ivec2 at = ivec2(gl_GlobalInvocationID.xy);
    imageStore(Out, at, texture(sampler2D(Src, Samp), vec2(0.5)) * Tint);
}
";

        // HOW MANY CALLS OF ONE (stage, space) THE DRIVER ACTUALLY TOOK, off the recording sink's log.
        static int Sent(FakeMetalEncoderCalls calls, MetalShaderStage stage, MetalIndexSpace space)
            => calls.ArrayWrites.Count(w => w.Stage == stage && w.Space == space);

        [SupportedOSPlatform("macos")]
        static IGpuTexture Target(MetalGpuDevice device) => device.Factory.CreateTexture(
            GpuTextureDescription.Texture2D(Size, Size, GpuPixelFormat.B8G8R8A8UNorm,
                GpuTextureUsage.RenderTarget));

        static GpuResourceLayoutElement Element(string name, GpuResourceKind kind, bool dynamic = false)
            => new(name, kind, GpuShaderStages.Vertex | GpuShaderStages.Fragment, dynamic);

        [SupportedOSPlatform("macos")]
        static MetalGpuDevice CreateHeadless()
            => (MetalGpuDevice)new MetalBackendProvider().CreateHeadless().Device;

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
