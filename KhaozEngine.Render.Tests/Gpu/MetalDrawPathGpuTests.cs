using System;
using System.Collections.Generic;
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
    /// ROW 14'S DRAWS AND DISPATCHES ON REAL HARDWARE, READ BACK AS PIXELS AND AS BYTES. Row 14 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/580).
    ///
    /// <para><b>THIS FILE IS THE ARGUMENT-LANDING PROBE FOR <c>drawIndexedPrimitives:</c>, AND THAT IS WHY IT
    /// READS A TEXEL RATHER THAN AN OUTCOME.</b> Every other native prototype this backend added is answered by
    /// ACCEPTANCE: row 6's eleven-argument copy is checked by a command buffer completing with a nil error,
    /// because a wrong argument placement there names a region the driver refuses or a texture that faults. The
    /// draws are the family where that reasoning fails.
    /// <c>drawIndexedPrimitives:indexCount:indexType:indexBuffer:indexBufferOffset:instanceCount:baseVertex:baseInstance:</c>
    /// takes TEN arguments counting the receiver and the selector, against arm64's eight general-purpose argument
    /// registers, so <c>baseVertex</c> and <c>baseInstance</c> cross ON THE STACK, and a misplaced vertex base is
    /// a draw that reads the wrong vertices and completes with a nil error. Row 1's spike did not measure that
    /// shape, so this row measures it, to the standard the spike held <c>MTLClearColor</c> to: a value read back,
    /// not a call accepted.</para>
    ///
    /// <para><b>SO THE PROBE IS BUILT SO THAT EVERY WRONG PLACEMENT PRODUCES A DIFFERENT, VALID COLOUR.</b>
    /// <see cref="TheSpilledBaseVertexAndBaseInstanceLandWhereTheDriverReadsThem"/> issues ONE indexed draw whose
    /// <c>indexBufferOffset</c>, <c>baseVertex</c> and <c>baseInstance</c> each select a different piece of the
    /// same two buffers, and the vertex buffer is sized so that even the wrong index range lands on a real vertex
    /// group rather than off the end. Dropping the offset paints white, dropping the base vertex paints magenta,
    /// dropping the base instance paints green, TRANSPOSING the two spilled arguments paints blue, and only all
    /// three landing correctly paints cyan. Undefined behaviour is deliberately not part of the answer, because
    /// "it did not crash" is the evidence this file exists to replace.</para>
    ///
    /// <para><b>THE TWO SPILLED ARGUMENTS CARRY DIFFERENT VALUES, WHICH IS THE WHOLE POINT OF THE ONE THEY BOTH
    /// USED TO CARRY.</b> The first version of this row passed 3 for both, so swapping the two stack slots painted
    /// cyan and the probe was blind to the one failure mode a stack-spilled pair has that a register pair does not.
    /// They are 3 and 6 now. Both are multiples of THREE deliberately, because a base vertex that is not lands the
    /// draw on a triangle straddling two groups, which interpolates two tints across the target and answers with a
    /// fraction rather than with a colour.</para>
    ///
    /// <para><b>WHAT A RED RUN MEANS.</b> Every DECISION in the draw path is covered device-free by
    /// <c>MetalDrawPathTests</c> and <c>MetalIndexBindingTests</c>, which run on the free Linux leg on every
    /// <c>dotnet test</c>: the four-step order, the state block's emission and its depth-target guard, the
    /// nil-encoder arm, the offset arithmetic and the budget marginal are all asserted there. A failure HERE is
    /// about the native calls underneath and about nothing else.</para>
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
    public sealed class MetalDrawPathGpuTests
    {
        const uint Size = 4;

        static readonly Color Black = new(0f, 0f, 0f, 1f);

        readonly ITestOutputHelper _output;

        public MetalDrawPathGpuTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// THE ABI PROBE, BY VALUE. One <c>drawIndexedPrimitives:</c> whose three interesting arguments each
        /// select a different piece of the same buffers, with the result read back as a texel.
        ///
        /// <para><b>THE ARITHMETIC, SPELLED OUT, because the whole value of this row is that a reader can check
        /// it.</b> The vertex buffer holds FOUR groups of three vertices, each group a full-screen triangle
        /// carrying its own tint in <c>r</c> and <c>g</c>: group 0 red, group 1 green, group 2 neither, group 3
        /// yellow. The per-instance buffer holds SEVEN tints whose only interesting channel is <c>b</c>, and only
        /// instances 3 and 6 have it set. The index buffer holds <c>[6, 7, 8, 0, 1, 2]</c> as 16-bit elements. The
        /// draw asks for 3 indices starting at element 3, with a base vertex of 3 and a base instance of 6.</para>
        ///
        /// <list type="bullet">
        /// <item><description><b>Correct:</b> the offset is <c>2 * 3 = 6</c> bytes, so the indices read are
        /// <c>0, 1, 2</c>. Base vertex 3 makes them vertices 3, 4 and 5, which is the GREEN group, and base
        /// instance 6 makes the instance tint one with blue set. The fragment writes
        /// <c>(r, g, b) = (0, 1, 1)</c>: CYAN.</description></item>
        /// <item><description><b>Index offset dropped:</b> the indices read are <c>6, 7, 8</c>, which base vertex
        /// 3 turns into vertices 9, 10 and 11, the YELLOW group. The result is <c>(1, 1, 1)</c>, white, and NOT
        /// an out-of-range fetch, which is why the buffer has a fourth group at all.</description></item>
        /// <item><description><b>Base vertex dropped:</b> vertices 0, 1 and 2, the RED group, giving
        /// <c>(1, 0, 1)</c>: magenta.</description></item>
        /// <item><description><b>Base instance dropped:</b> instance 0's tint, whose blue channel is 0, giving
        /// <c>(0, 1, 0)</c>: green.</description></item>
        /// <item><description><b>The two TRANSPOSED:</b> base vertex 6 and base instance 3, which is vertices 6, 7
        /// and 8, the group carrying NEITHER r nor g, with instance 3's blue, giving <c>(0, 0, 1)</c>: blue. That
        /// is the failure mode a spilled pair has and a register pair does not, and it is the reason instance 3
        /// carries blue at all.</description></item>
        /// </list>
        ///
        /// <para><b>AND THE FIVE OUTCOMES ARE FIVE DIFFERENT COLOURS, which is the property that makes this a
        /// measurement rather than an assertion.</b> A test whose only failure mode was "black" could not tell a
        /// misplaced argument from a pipeline that never bound, and the design's own row-17 note records that a
        /// <c>[GpuFact]</c> asserting no-throw is how the all-black splat terrain shipped.</para>
        /// </summary>
        [GpuFact]
        public void TheSpilledBaseVertexAndBaseInstanceLandWhereTheDriverReadsThem()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            using DrawFixture fixture = new(device);

            using (MetalCommandList list = device.CreateCommandList())
            {
                list.Begin();
                list.SetFramebuffer(fixture.Framebuffer);
                list.ClearColorTarget(0, Black);
                list.SetPipeline(fixture.Pipeline);
                list.SetVertexBuffer(0, fixture.Vertices);
                list.SetVertexBuffer(1, fixture.Instances);
                list.SetIndexBuffer(fixture.Indices, GpuIndexFormat.UInt16);

                // THE TWO SPILLED ARGUMENTS ARE DIFFERENT NUMBERS, so a transposition of the two stack slots is a
                // different colour rather than the same one.
                list.DrawIndexed(indexCount: 3, instanceCount: 1, indexStart: 3, vertexOffset: 3,
                    instanceStart: 6);

                list.End();
                device.Submit(list);
            }

            device.WaitForIdle();

            Assert.Null(device.Diagnostics.DeviceLossReason);

            Color texel = ReadFirstTexel(device, fixture.Target);
            _output.WriteLine($"indexed draw read back {texel}, wanted cyan (0,1,1). "
                + "White means the index-buffer offset did not land, magenta means the base vertex did not, "
                + "green means the base instance did not, and blue means the two spilled arguments are "
                + "transposed.");

            Assert.Equal(new Color(0f, 1f, 1f, 1f), texel);
        }

        /// <summary>
        /// THE NON-INDEXED DRAW AND THE VERTEX-STREAM OFFSET, which between them are the OTHER half of the
        /// geometry path. <c>drawPrimitives:</c> spills nothing, so its own ABI needs no probe, and what this
        /// checks instead is the whole activation reaching a pixel: the deferred begin, the pipeline-state block,
        /// the vertex-stream array setter at M-B2's top-of-space index, and the draw.
        /// <para>
        /// THE STREAM IS BOUND AT AN OFFSET RATHER THAN AT ZERO, and that is the point of the row: the seam's
        /// <c>SetVertexBuffer(slot, buffer, offsetBytes)</c> is the one place a whole mesh moves by one number,
        /// and a backend that dropped it would render the FIRST group here with no error anywhere. Six vertices
        /// in, at a 24-byte stride, is the BLUE group.
        /// </para>
        /// </summary>
        [GpuFact]
        public void ANonIndexedDrawReadsTheStreamFromTheOffsetItWasBoundAt()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            using DrawFixture fixture = new(device);

            using (MetalCommandList list = device.CreateCommandList())
            {
                list.Begin();
                list.SetFramebuffer(fixture.Framebuffer);
                list.ClearColorTarget(0, Black);
                list.SetPipeline(fixture.Pipeline);

                // GROUP 2, the blue one, reached by moving the stream rather than by moving the draw.
                list.SetVertexBuffer(0, fixture.Vertices, 6 * DrawFixture.VertexStride);
                list.SetVertexBuffer(1, fixture.Instances, 3 * DrawFixture.InstanceStride);

                list.Draw(3);

                list.End();
                device.Submit(list);
            }

            device.WaitForIdle();

            Assert.Null(device.Diagnostics.DeviceLossReason);

            Color texel = ReadFirstTexel(device, fixture.Target);
            _output.WriteLine($"non-indexed draw read back {texel}, wanted (0,0,1) plus the instance blue.");

            // Group 2 is (0, 0) in r and g, and the instance stream is bound at entry 3, whose blue is set.
            Assert.Equal(new Color(0f, 0f, 1f, 1f), texel);
        }

        /// <summary>
        /// A NON-INDEXED DRAW AT A NON-ZERO BASE INSTANCE, which is the row that keeps the LONG
        /// <c>drawPrimitives:vertexStart:vertexCount:instanceCount:baseInstance:</c> selector executed by
        /// something.
        /// <para>
        /// <b>IT EXISTS BECAUSE THE SELECTOR STOPPED BEING UNCONDITIONAL</b>
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/598). Every other non-indexed row in this file draws
        /// at a base instance of zero, so once
        /// <c>MTLRenderCommandEncoder.DrawPrimitives</c> forks on that argument, the long form is reachable in
        /// production and reached by no test at all, which is the declared-but-never-sent state that file's
        /// header rule exists to prevent. The indexed pair needs no equivalent row: the ABI probe above already
        /// draws at a base vertex of 3 and a base instance of 6.
        /// </para>
        /// <para>
        /// THE ANSWER IS A COLOUR RATHER THAN AN OUTCOME, like everything else here. The vertex stream is bound
        /// at zero, which is the RED group, and the instance stream is bound at zero with a base instance of 6,
        /// whose blue is set. Correct is magenta. A base instance that did not land reads instance 0, whose blue
        /// is dark, and paints red.
        /// </para>
        /// </summary>
        [GpuFact]
        public void ANonIndexedDrawCarriesItsBaseInstanceThroughTheLongSelector()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            using DrawFixture fixture = new(device);

            using (MetalCommandList list = device.CreateCommandList())
            {
                list.Begin();
                list.SetFramebuffer(fixture.Framebuffer);
                list.ClearColorTarget(0, Black);
                list.SetPipeline(fixture.Pipeline);
                list.SetVertexBuffer(0, fixture.Vertices);
                list.SetVertexBuffer(1, fixture.Instances);

                list.Draw(vertexCount: 3, instanceCount: 1, vertexStart: 0, instanceStart: 6);

                list.End();
                device.Submit(list);
            }

            device.WaitForIdle();

            Assert.Null(device.Diagnostics.DeviceLossReason);

            Color texel = ReadFirstTexel(device, fixture.Target);
            _output.WriteLine($"instanced non-indexed draw read back {texel}, wanted magenta (1,0,1). "
                + "Red means the base instance did not reach the driver.");

            Assert.Equal(new Color(1f, 0f, 1f, 1f), texel);
        }

        /// <summary>
        /// TWO DRAWS IN ONE PASS, WHERE THE SECOND CHANGES ONLY THE VERTEX STREAM. What this proves on hardware
        /// is that the redundancy tracking is safe rather than merely cheap: the second draw re-binds the stream
        /// it changed and NOT the pipeline state, and it still renders the group it asked for.
        /// <para>
        /// THE INCUMBENT'S CACHE IS PERMANENTLY COLD AND THIS ONE IS NOT (section 6.2), so this is the shape
        /// where porting the tracking without the M-R4 invalidation would ship a corruption. The device-free row
        /// asserts the CALL COUNTS. This one asserts the resulting pixel, which is the half a count cannot see.
        /// </para>
        /// </summary>
        [GpuFact]
        public void ASecondDrawRebindsOnlyTheStreamItChangedAndStillRendersIt()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            using DrawFixture fixture = new(device);

            using (MetalCommandList list = device.CreateCommandList())
            {
                list.Begin();
                list.SetFramebuffer(fixture.Framebuffer);
                list.ClearColorTarget(0, Black);
                list.SetPipeline(fixture.Pipeline);
                list.SetVertexBuffer(1, fixture.Instances, 3 * DrawFixture.InstanceStride);

                // RED first, then YELLOW over the top of it, with nothing but the stream offset moving.
                list.SetVertexBuffer(0, fixture.Vertices, 0);
                list.Draw(3);

                list.SetVertexBuffer(0, fixture.Vertices, 9 * DrawFixture.VertexStride);
                list.Draw(3);

                list.End();
                device.Submit(list);
            }

            device.WaitForIdle();

            Assert.Null(device.Diagnostics.DeviceLossReason);
            Assert.Equal(new Color(1f, 1f, 1f, 1f), ReadFirstTexel(device, fixture.Target));
        }

        /// <summary>
        /// THE DISPATCH PATH: <c>-setComputePipelineState:</c> and
        /// <c>-dispatchThreadgroups:threadsPerThreadgroup:</c> against a real compute encoder, with the result
        /// read back as BYTES.
        ///
        /// <para><b>THE THREADGROUP SIZE IS THE CLAIM ONLY A DEVICE CAN SETTLE.</b> Metal takes it as an argument
        /// where Direct3D 11 and Vulkan read it out of the compiled module, so row 9 reads it out of the SPIR-V
        /// (<c>SpirvLocalSize</c>) and it travels through the pipeline to the call. A backend that passed the
        /// wrong number here writes the wrong number of elements and every byte past that stays zero, which is
        /// exactly what this reads.</para>
        ///
        /// <para><b>AND IT EXERCISES <c>CopyBuffer</c> ON THE WAY OUT</b>, because a storage buffer has no CPU
        /// pointer of its own: the readback is the seam's own record-time buffer copy into a staging buffer, which
        /// is section 9.3's alignment ruling running on real hardware with all three numbers aligned by
        /// construction.</para>
        /// </summary>
        [GpuFact]
        public void ADispatchRunsTheKernelWithTheGroupSizeReadOffTheShader()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            IGpuResourceFactory factory = device.Factory;

            const uint Elements = 256;
            const uint Bytes = Elements * sizeof(uint);

            using IGpuComputeShader shader = factory.CreateComputeShaderFromSpirv(FillComputeSrc);
            var metalShader = (MetalComputeShader)shader;

            var layouts = new List<IGpuResourceLayout>();
            foreach (GpuResourceLayoutDescription reflected in metalShader.Table.Layouts)
                layouts.Add(factory.CreateResourceLayout(reflected));

            try
            {
                using IGpuComputePipeline pipeline = factory.CreateComputePipeline(
                    new GpuComputePipelineDescription(shader, layouts.ToArray()));

                using IGpuBuffer storage = factory.CreateBuffer(
                    new GpuBufferDescription(Bytes, GpuBufferUsage.StructuredBufferReadWrite, sizeof(uint)));
                using IGpuBuffer staging = factory.CreateBuffer(
                    new GpuBufferDescription(Bytes, GpuBufferUsage.Staging));
                using IGpuResourceSet set = factory.CreateResourceSet(
                    new GpuResourceSetDescription(layouts[0], storage));

                // THE GROUP SIZE THE KERNEL DECLARED, which is what the dispatch has to pass. 256 elements at 64
                // threads per group is four groups, and a wrong threads-per-group leaves the tail at zero.
                Assert.Equal(64u, shader.ThreadGroupSizeX);

                using (MetalCommandList list = device.CreateCommandList())
                {
                    list.Begin();
                    list.SetComputePipeline(pipeline);
                    list.SetComputeResourceSet(0, set);
                    list.Dispatch(Elements / 64, 1, 1);

                    // THE READBACK IS A RECORD-TIME CopyBuffer, which ends the compute encoder and opens a blit
                    // one, so this row also covers the M-A5 transition in the direction the draws do not.
                    list.CopyBuffer(storage, 0, staging, 0, Bytes);

                    list.End();
                    device.Submit(list);
                }

                device.WaitForIdle();

                Assert.Null(device.Diagnostics.DeviceLossReason);

                MappedData mapped = device.Map(staging, GpuMapMode.Read);
                try
                {
                    var read = new uint[Elements];
                    for (int i = 0; i < read.Length; i++)
                    {
                        read[i] = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(mapped.Data, i * 4);
                    }

                    for (uint i = 0; i < Elements; i++) Assert.Equal(i * 2, read[i]);
                }
                finally
                {
                    device.Unmap(staging);
                }

                _output.WriteLine($"{Elements} elements written by {Elements / 64} threadgroups of "
                    + $"{shader.ThreadGroupSizeX}, read back through the seam's own CopyBuffer.");
            }
            finally
            {
                foreach (IGpuResourceLayout layout in layouts) layout.Dispose();
            }
        }

        /// <summary>
        /// THE SEAM'S COMPUTE RULE 2 ON THIS BACKEND: two DEPENDENT dispatches inside ONE recording, with no
        /// drain between them, and the second one reads what the first wrote.
        ///
        /// <para><b>THAT IS M-H4 AND IT IS A BACKEND PROPERTY RATHER THAN A CONTRACT CHANGE.</b> The compute
        /// encoder is opened with the SERIAL dispatch type, so dispatches inside it do not overlap and a
        /// read-after-write between them is ordered by the encoder itself, which is why this backend carries no
        /// barrier batch, no layout tracker and no dependency analysis at all. The seam's rule 2 still says a
        /// dependent chain needs <c>End</c>, <c>Submit</c> and <c>WaitForIdle</c>, because the Veldrid legs need
        /// the drain and a consumer that dropped it because THIS backend tolerates the chain would break on them.
        /// So this row asserts the backend property and changes nothing about what portable code may assume.</para>
        ///
        /// <para><b>AND IT IS THE ONE CLAIM IN THE ROW THAT IS NOT GROUNDED IN ITS OWN USAGE</b>, which section
        /// 18 says in as many words: the vendored fork never chains dependent dispatches, so without this row the
        /// serial dispatch type would be a decision nothing exercised.</para>
        /// </summary>
        [GpuFact]
        public void TwoDependentDispatchesInOneEncoderAreOrderedBySerialDispatch()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            IGpuResourceFactory factory = device.Factory;

            const uint Elements = 64;
            const uint Bytes = Elements * sizeof(uint);

            using IGpuComputeShader fill = factory.CreateComputeShaderFromSpirv(FillComputeSrc);
            using IGpuComputeShader doubler = factory.CreateComputeShaderFromSpirv(DoubleComputeSrc);

            var layouts = new List<IGpuResourceLayout>();
            foreach (GpuResourceLayoutDescription reflected in ((MetalComputeShader)fill).Table.Layouts)
                layouts.Add(factory.CreateResourceLayout(reflected));

            try
            {
                using IGpuComputePipeline first = factory.CreateComputePipeline(
                    new GpuComputePipelineDescription(fill, layouts.ToArray()));
                using IGpuComputePipeline second = factory.CreateComputePipeline(
                    new GpuComputePipelineDescription(doubler, layouts.ToArray()));

                using IGpuBuffer storage = factory.CreateBuffer(
                    new GpuBufferDescription(Bytes, GpuBufferUsage.StructuredBufferReadWrite, sizeof(uint)));
                using IGpuBuffer staging = factory.CreateBuffer(
                    new GpuBufferDescription(Bytes, GpuBufferUsage.Staging));
                using IGpuResourceSet set = factory.CreateResourceSet(
                    new GpuResourceSetDescription(layouts[0], storage));

                using (MetalCommandList list = device.CreateCommandList())
                {
                    list.Begin();

                    list.SetComputePipeline(first);
                    list.SetComputeResourceSet(0, set);
                    list.Dispatch(1, 1, 1);

                    // NO DRAIN, NO BARRIER, NO SECOND SUBMIT. The pipeline change re-emits its state block into
                    // the SAME open compute encoder, and the serial dispatch type is what orders the two.
                    list.SetComputePipeline(second);
                    list.Dispatch(1, 1, 1);

                    list.CopyBuffer(storage, 0, staging, 0, Bytes);
                    list.End();
                    device.Submit(list);
                }

                device.WaitForIdle();

                Assert.Null(device.Diagnostics.DeviceLossReason);

                MappedData mapped = device.Map(staging, GpuMapMode.Read);
                try
                {
                    for (uint i = 0; i < Elements; i++)
                    {
                        uint read = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(mapped.Data,
                            (int)i * 4);

                        // The fill writes i * 2 and the doubler doubles what it finds, so an unordered pair
                        // would leave i * 2 behind rather than i * 4.
                        Assert.Equal(i * 4, read);
                    }
                }
                finally
                {
                    device.Unmap(staging);
                }

                _output.WriteLine("two dependent dispatches inside one SERIAL compute encoder, ordered with no "
                    + "hazard machinery anywhere in the backend.");
            }
            finally
            {
                foreach (IGpuResourceLayout layout in layouts) layout.Dispose();
            }
        }

        // ---- Fixtures ----------------------------------------------------------------------------------------

        /// <summary>
        /// Texel (0, 0) of <paramref name="texture"/>, through the SEAM's own <c>CopyTexture</c> into a staging
        /// texture and the engine's <c>Map</c>. A <c>StorageModePrivate</c> texture has no CPU pointer at all
        /// (M-M2), so a copy is the only route, and going through the seam member rather than through the interop
        /// layer directly is deliberate: this is the readback path every golden uses, so a row that bypassed it
        /// would be checking a draw with the copy family taken out.
        /// </summary>
        [SupportedOSPlatform("macos")]
        static Color ReadFirstTexel(MetalGpuDevice device, IGpuTexture texture)
        {
            using IGpuTexture staging = device.Factory.CreateTexture(
                GpuTextureDescription.Texture2D(Size, Size, GpuPixelFormat.B8G8R8A8UNorm,
                    GpuTextureUsage.Staging));

            using (MetalCommandList list = device.CreateCommandList())
            {
                list.Begin();
                list.CopyTexture(texture, staging);
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

        // A kernel whose workgroup size is DECLARED in the module, which is the number the dispatch has to carry.
        const string FillComputeSrc = @"#version 450
layout(local_size_x = 64) in;
layout(set=0, binding=0) buffer Data { uint Values[]; };
void main() { Values[gl_GlobalInvocationID.x] = gl_GlobalInvocationID.x * 2u; }";

        // Reads what the first kernel wrote and doubles it, which is what makes the pair DEPENDENT.
        const string DoubleComputeSrc = @"#version 450
layout(local_size_x = 64) in;
layout(set=0, binding=0) buffer Data { uint Values[]; };
void main() { Values[gl_GlobalInvocationID.x] = Values[gl_GlobalInvocationID.x] * 2u; }";

        /// <summary>
        /// THE GEOMETRY THE PROBE IS BUILT ON, in one place so the four groups and the four instance tints are
        /// described once and read by three rows.
        /// <para>
        /// EVERY GROUP IS A FULL-SCREEN TRIANGLE, so which group was drawn is a question about COLOUR rather than
        /// about coverage, and a single texel answers it. The three that would be selected by a misplaced
        /// argument are all REAL groups, which is what keeps every failure mode a defined value rather than an
        /// out-of-range fetch.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        sealed class DrawFixture : IDisposable
        {
            /// <summary>Two floats of position plus four of tint.</summary>
            internal const uint VertexStride = (2 + 4) * sizeof(float);

            /// <summary>Four floats of tint.</summary>
            internal const uint InstanceStride = 4 * sizeof(float);

            /// <summary>How many per-instance tints there are. SEVEN rather than four, because the ABI probe's
            /// base instance is 6: the two spilled arguments have to be different numbers for a transposition to
            /// be visible, and both have to be multiples of three for the transposed base VERTEX to land on a
            /// whole group.</summary>
            internal const uint InstanceTints = 7;

            readonly List<IGpuResourceLayout> _layouts = new();

            internal DrawFixture(MetalGpuDevice device)
            {
                IGpuResourceFactory factory = device.Factory;

                Shaders = factory.CreateShadersFromSpirv(TintVert, TintFrag);
                Target = factory.CreateTexture(GpuTextureDescription.Texture2D(Size, Size,
                    GpuPixelFormat.B8G8R8A8UNorm, GpuTextureUsage.RenderTarget));
                Framebuffer = factory.CreateFramebuffer(null, Target);

                // THE LAYOUT ARRAY COMES OFF THE REFLECTION rather than being hand-declared as empty, which is
                // the shape MetalPipelineGpuTests already settled on: 2.2b's shape check compares the DECLARED
                // array against what the emission reflected, and a fixture that hand-wrote the array would be
                // asserting agreement with itself. These shaders read no resource at all, so what comes back is
                // whatever the emission still declares, and the point is that the pipeline is built from it.
                foreach (GpuResourceLayoutDescription reflected in ((MetalShaderSet)Shaders).Table.Layouts)
                    _layouts.Add(factory.CreateResourceLayout(reflected));

                Pipeline = factory.CreateGraphicsPipeline(new GpuPipelineDescription
                {
                    ShaderSet = Shaders,
                    ResourceLayouts = _layouts.ToArray(),
                    BlendAttachments = [GpuBlendAttachment.OverrideBlend],
                    DepthStencil = GpuDepthStencilState.Disabled,
                    Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid,
                        GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: false),
                    Topology = GpuPrimitiveTopology.TriangleList,
                    VertexLayouts = new List<GpuVertexLayoutDescription>
                    {
                        new(new GpuVertexElement("Position", GpuVertexElementFormat.Float2),
                            new GpuVertexElement("VertexTint", GpuVertexElementFormat.Float4)),
                        new(0, 1, [new GpuVertexElement("InstanceTint", GpuVertexElementFormat.Float4)]),
                    },
                    Outputs = new GpuOutputDescription(null, GpuPixelFormat.B8G8R8A8UNorm),
                });

                Vertices = factory.CreateBuffer(
                    new GpuBufferDescription(12 * VertexStride, GpuBufferUsage.VertexBuffer));
                Instances = factory.CreateBuffer(
                    new GpuBufferDescription(InstanceTints * InstanceStride, GpuBufferUsage.VertexBuffer));
                Indices = factory.CreateBuffer(
                    new GpuBufferDescription(6 * sizeof(ushort), GpuBufferUsage.IndexBuffer));

                device.UpdateBuffer(Vertices, 0, VertexData());
                device.UpdateBuffer(Instances, 0, InstanceData());
                device.UpdateBuffer(Indices, 0, new ushort[] { 6, 7, 8, 0, 1, 2 }.AsSpan());
            }

            internal IGpuShaderSet Shaders { get; }

            internal IGpuTexture Target { get; }

            internal IGpuFramebuffer Framebuffer { get; }

            internal IGpuPipeline Pipeline { get; }

            internal IGpuBuffer Vertices { get; }

            internal IGpuBuffer Instances { get; }

            internal IGpuBuffer Indices { get; }

            public void Dispose()
            {
                Indices.Dispose();
                Instances.Dispose();
                Vertices.Dispose();
                Pipeline.Dispose();
                foreach (IGpuResourceLayout layout in _layouts) layout.Dispose();
                Framebuffer.Dispose();
                Target.Dispose();
                Shaders.Dispose();
            }

            // FOUR GROUPS OF THREE, each a full-screen triangle, each carrying its group's tint in r and g.
            static ReadOnlySpan<float> VertexData()
            {
                var data = new List<float>();

                // r and g PER GROUP, in group order: red, green, none, yellow. The third group carries neither
                // channel on purpose, so what a draw selecting it paints comes from the INSTANCE stream alone,
                // which is what makes the non-indexed row's blue mean the stream offset landed.
                ReadOnlySpan<float> tints = [1f, 0f, 0f, 1f, 0f, 0f, 1f, 1f];

                for (int group = 0; group < 4; group++)
                {
                    float r = tints[group * 2];
                    float g = tints[(group * 2) + 1];

                    // The full-screen triangle every renderer in this engine uses, in clip space.
                    ReadOnlySpan<float> positions = [-1f, -1f, 3f, -1f, -1f, 3f];
                    for (int corner = 0; corner < 3; corner++)
                    {
                        data.Add(positions[corner * 2]);
                        data.Add(positions[(corner * 2) + 1]);
                        data.Add(r);
                        data.Add(g);
                        data.Add(0f);
                        data.Add(1f);
                    }
                }

                return data.ToArray();
            }

            // SEVEN INSTANCE TINTS whose only interesting channel is blue, and only entries 3 and 6 have it set.
            // Entry 6 is the ABI probe's base instance and entry 3 is what a TRANSPOSITION of the two spilled
            // arguments reads instead, so both carry blue: with entry 3 dark the transposed case would paint
            // black, which is also what a pipeline that never bound paints. Entry 3 is what the two stream-offset
            // rows above bind at.
            static ReadOnlySpan<float> InstanceData()
                =>
                [
                    0f, 0f, 0f, 1f,
                    0f, 0f, 0f, 1f,
                    0f, 0f, 0f, 1f,
                    0f, 0f, 1f, 1f,
                    0f, 0f, 0f, 1f,
                    0f, 0f, 0f, 1f,
                    0f, 0f, 1f, 1f,
                ];

            const string TintVert = @"#version 450
layout(location=0) in vec2 Position;
layout(location=1) in vec4 VertexTint;
layout(location=2) in vec4 InstanceTint;
layout(location=0) out vec4 vTint;
void main()
{
    gl_Position = vec4(Position, 0.0, 1.0);
    vTint = vec4(VertexTint.r, VertexTint.g, InstanceTint.b, 1.0);
}";

            const string TintFrag = @"#version 450
layout(location=0) in vec4 vTint;
layout(location=0) out vec4 Colour;
void main() { Colour = vTint; }";
        }
    }
}
