using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE ARRAY-BATCHED FAN-OUT OF DECISION R6, and the constant-buffer arithmetic of decision R7, over the
    /// resource sets the renderers actually declare.
    /// <para>
    /// THIS IS WHERE #418 IS PINNED. That defect was one native call per resource per stage, and the cure is one
    /// call per register FILE per stage. The two numbers that matter are the model set at four and the water set
    /// at six, and both are seven-element sets, so a test over an invented set would have proved neither. The
    /// traces are spelled out rather than counted wherever the ORDER and the REGISTERS carry the meaning, because
    /// a count that is right with the wrong start register renders wrongly and passes.
    /// </para>
    /// </summary>
    public sealed class D3D11SetActivationTests
    {
        // ---- The two numbers the spec quotes ---------------------------------------------------------------

        /// <summary>
        /// THE MODEL SET IS FOUR NATIVE CALLS, from seven elements. One constant-buffer call per stage that reads
        /// the UBO, one shader-resource array covering all four textures at once, one sampler array covering both.
        /// The incumbent issued 42 for the same set before its own batching and 8 after.
        /// </summary>
        [Fact]
        public void TheModelSet_ActivatesInFourNativeCalls()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout layout = D3D11BindFixtures.ModelLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.ModelSet(layout);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            emitter.SetGraphicsResourceSet(0, set);
            harness.Log.Reset();

            emitter.Draw(3, 1, 0, 0);

            Assert.Equal(4, harness.BindTrace().Length);
            Assert.Equal(
                new[]
                {
                    $"VSSetConstantBuffers1(0,1,{Id(harness, set, 0)}@0+16)",
                    $"PSSetConstantBuffers1(0,1,{Id(harness, set, 0)}@0+16)",
                    "PSSetShaderResources(0,4," + $"{Id(harness, set, 1)}|{Id(harness, set, 2)}|"
                        + $"{Id(harness, set, 3)}|{Id(harness, set, 5)})",
                    $"PSSetSamplers(0,2,{Id(harness, set, 4)}|{Id(harness, set, 6)})",
                },
                harness.BindTrace());
        }

        /// <summary>
        /// THE WORST CASE IN THE ENGINE IS THE WATER SET AT SIX, not the model set, and that is the bound to
        /// quote. Both are seven elements. The difference is that <c>WaterRenderer</c> declares its bathymetry
        /// texture, its ocean map, their samplers and its dynamic UBO at <c>Vertex | Fragment</c>, so the vertex
        /// stage needs a shader-resource array and a sampler array of its own: two constant buffers, two
        /// shader-resource arrays, two sampler arrays.
        /// </summary>
        [Fact]
        public void TheWaterSet_IsTheWorstCaseAtSix()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout layout = D3D11BindFixtures.WaterLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.WaterSet(layout);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            emitter.SetGraphicsResourceSet(0, set, 0);
            harness.Log.Reset();

            emitter.Draw(3, 1, 0, 0);

            Assert.Equal(6, harness.BindTrace().Length);
            Assert.Equal(1, harness.Log.Count(D3D11NativeCall.VSSetConstantBuffers1));
            Assert.Equal(1, harness.Log.Count(D3D11NativeCall.PSSetConstantBuffers1));

            // The vertex stage sees the first two textures and their two samplers, the pixel stage sees all three
            // of each. Two arrays per file rather than five calls per file.
            Assert.Contains($"VSSetShaderResources(0,2,{Id(harness, set, 0)}|{Id(harness, set, 2)})",
                harness.BindTrace());
            Assert.Contains(
                $"PSSetShaderResources(0,3,{Id(harness, set, 0)}|{Id(harness, set, 2)}|{Id(harness, set, 4)})",
                harness.BindTrace());
            Assert.Contains($"VSSetSamplers(0,2,{Id(harness, set, 1)}|{Id(harness, set, 3)})", harness.BindTrace());
        }

        /// <summary>
        /// THE OFFSETS-ONLY REBIND IS EXACTLY ONE CALL PER VISIBLE STAGE, which is the shadow pass's
        /// thousands-per-frame path and the whole reason the middle dirty state exists. One dynamic UBO visible to
        /// the vertex stage alone means one <c>VSSetConstantBuffers1</c> and nothing else.
        /// </summary>
        [Fact]
        public void AnOffsetsOnlyRebind_IsOneConstantBufferCallPerVisibleStage()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout layout = D3D11BindFixtures.ShadowLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.ShadowSet(layout);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            emitter.SetGraphicsResourceSet(0, set, 0);
            emitter.Draw(3, 1, 0, 0);
            harness.Log.Reset();

            emitter.SetGraphicsResourceSet(0, set, 256);
            emitter.Draw(3, 1, 0, 0);

            Assert.Equal(
                new[] { $"VSSetConstantBuffers1(0,1,{Id(harness, set, 0)}@16+16)" },
                harness.BindTrace());
        }

        /// <summary>
        /// AND IT SKIPS TEXTURES AND SAMPLERS ENTIRELY (rule 3), which is what makes it worth having. The water
        /// set is six calls fully activated and TWO when only the offset moved, because its dynamic UBO is visible
        /// to both stages and nothing else is pushed at all.
        /// </summary>
        [Fact]
        public void AnOffsetsOnlyRebind_PushesNoTextureAndNoSampler()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout layout = D3D11BindFixtures.WaterLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.WaterSet(layout);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            emitter.SetGraphicsResourceSet(0, set, 0);
            emitter.Draw(3, 1, 0, 0);
            harness.Log.Reset();

            emitter.SetGraphicsResourceSet(0, set, 512);
            emitter.Draw(3, 1, 0, 0);

            Assert.Equal(2, harness.BindTrace().Length);
            Assert.Equal(0, harness.Log.Count(D3D11NativeCall.VSSetShaderResources));
            Assert.Equal(0, harness.Log.Count(D3D11NativeCall.PSSetShaderResources));
            Assert.Equal(0, harness.Log.Count(D3D11NativeCall.VSSetSamplers));
            Assert.Equal(0, harness.Log.Count(D3D11NativeCall.PSSetSamplers));
        }

        // ---- The register base across layouts (decision S2) ------------------------------------------------

        /// <summary>
        /// A SET'S REGISTERS START PAST EVERY LAYOUT BEFORE IT IN THE PIPELINE'S ARRAY, per file. The model layout
        /// consumes one constant buffer, four shader resources and two samplers, so a water set at slot one starts
        /// at <c>b1</c>, <c>t4</c> and <c>s2</c>. That flattening is the whole of decision S2's across-layout
        /// half, and getting it wrong compiles, draws and renders every pixel from the wrong resource.
        /// </summary>
        [Fact]
        public void ASetAtSlotOne_IsNumberedPastTheLayoutBeforeIt()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout model = D3D11BindFixtures.ModelLayout();
            using D3D11ResourceLayout water = D3D11BindFixtures.WaterLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.WaterSet(water);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(model, water));
            emitter.SetGraphicsResourceSet(1, set, 0);
            harness.Log.Reset();

            emitter.Draw(3, 1, 0, 0);

            Assert.Contains($"VSSetConstantBuffers1(1,1,{Id(harness, set, 6)}@0+64)", harness.BindTrace());
            Assert.Contains($"VSSetShaderResources(4,2,{Id(harness, set, 0)}|{Id(harness, set, 2)})",
                harness.BindTrace());
            Assert.Contains($"VSSetSamplers(2,2,{Id(harness, set, 1)}|{Id(harness, set, 3)})", harness.BindTrace());
        }

        // ---- The constant-buffer arithmetic (decision R7, decision U1) -------------------------------------

        /// <summary>
        /// EVERY CONSTANT-BUFFER BIND CARRIES AN EXPLICIT FIRST CONSTANT AND COUNT, INCLUDING A FULL-RANGE ONE.
        /// It is tempting to send a full range through the plain <c>*SetConstantBuffers</c>, and it is wrong the
        /// moment the buffer is ring-backed: the frame base is an addend on every bind, so a full-range bind of a
        /// ring-backed buffer still starts at a non-zero constant. One path means one place for the arithmetic to
        /// be wrong in.
        /// </summary>
        [Fact]
        public void AFullRangeBind_StillCarriesAnExplicitFirstConstantAndCount()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout layout = D3D11BindFixtures.Layout(
                D3D11BindFixtures.U("U", GpuShaderStages.Vertex));
            var buffer = new FakeBuffer(1024);
            using D3D11ResourceSet set = D3D11BindFixtures.Set(layout, buffer);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            emitter.SetGraphicsResourceSet(0, set);
            harness.Log.Reset();

            emitter.Draw(3, 1, 0, 0);

            Assert.Equal(new[] { $"VSSetConstantBuffers1(0,1,{harness.Log.Id(buffer)}@0+64)" }, harness.BindTrace());
        }

        /// <summary>A window shorter than the 256-byte minimum is rounded UP to it rather than refused, matching
        /// the incumbent. The shader reads only the fields its own block declares, so naming constants past the
        /// caller's window is safe, and refusing would break every 64-byte cascade slot the shadow pass
        /// binds.</summary>
        [Fact]
        public void AWindowUnderTheMinimum_IsRoundedUpRatherThanRefused()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout layout = D3D11BindFixtures.ShadowLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.ShadowSet(layout);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            emitter.SetGraphicsResourceSet(0, set, 0);
            harness.Log.Reset();

            emitter.Draw(3, 1, 0, 0);

            // 64 bytes asked for, 256 bound, which is 16 constants.
            Assert.Equal(new[] { $"VSSetConstantBuffers1(0,1,{Id(harness, set, 0)}@0+16)" }, harness.BindTrace());
        }

        /// <summary>
        /// THE RING'S PER-FRAME BASE IS TAKEN AT BIND TIME AND NEVER BAKED INTO THE SET (decisions U1 and U3).
        /// The same set, unchanged, binds at a different first constant once the allocator has rolled to the next
        /// frame segment, which is what keeps the pinned <c>GpuBufferRange</c> valid across all 68 load-time call
        /// sites that build one.
        /// </summary>
        [Fact]
        public void ARingBackedBuffer_TakesItsFrameBaseAtBindTime()
        {
            var harness = new D3D11BindFixtures.Harness();
            using var rings = new D3D11RingHarness(sizeInBytes: 512, framesInFlight: 3);
            var buffer = new FakeRingBackedBuffer(rings.Ring);
            using D3D11ResourceLayout layout = D3D11BindFixtures.ShadowLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.ShadowSet(layout, buffer);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            emitter.SetGraphicsResourceSet(0, set, 128);
            harness.Log.Reset();
            emitter.Draw(3, 1, 0, 0);

            // Segment zero: base 0, plus the 128-byte dynamic offset, is constant 8.
            Assert.Equal(new[] { $"VSSetConstantBuffers1(0,1,{harness.Log.Id(buffer)}@8+16)" }, harness.BindTrace());

            // The next frame, which is the next replay: the segment rolls and the recording opens again.
            rings.Allocator.BeginFrame();
            emitter.End();
            emitter.Begin();
            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            emitter.SetGraphicsResourceSet(0, set, 128);
            harness.Log.Reset();
            emitter.Draw(3, 1, 0, 0);

            // Segment one starts 512 bytes in, which is constant 32, so the SAME set at the SAME dynamic offset
            // is now constant 40. Nothing about the set changed, which is decision U3 holding.
            Assert.Equal(new[] { $"VSSetConstantBuffers1(0,1,{harness.Log.Id(buffer)}@40+16)" }, harness.BindTrace());
        }

        // ---- The !DriverCommandLists workaround (decision R7), BOTH arms -----------------------------------

        /// <summary>
        /// THE FAST ARM: a driver that builds its own command lists gets one call per constant-buffer bind, which
        /// is every arm of every other test in this file.
        /// </summary>
        [Fact]
        public void WithRealDriverCommandLists_AConstantBufferBindIsOneCall()
        {
            var harness = new D3D11BindFixtures.Harness(unsetConstantBuffersBeforeSet: false);
            using D3D11ResourceLayout layout = D3D11BindFixtures.ShadowLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.ShadowSet(layout);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            emitter.SetGraphicsResourceSet(0, set, 0);
            harness.Log.Reset();

            emitter.Draw(3, 1, 0, 0);

            Assert.False(harness.Binds.UnsetsConstantBuffersBeforeSet);
            Assert.Equal(1, harness.Log.Count(D3D11NativeCall.VSSetConstantBuffers1));
        }

        /// <summary>
        /// THE EMULATED ARM: when the runtime is emulating command lists, a re-bind of the same buffer at a
        /// different first constant is dropped, so the offset silently does not move and every draw after the
        /// first reads the first draw's constants. The workaround unbinds the same span IMMEDIATELY before the
        /// bind, which makes the bind a genuine change.
        /// <para>
        /// It doubles the constant-buffer call count, which is the cost of being correct there and the reason both
        /// arms are asserted: a budget that only ever saw the fast arm would read the slow one as a regression.
        /// </para>
        /// </summary>
        [Fact]
        public void WithEmulatedCommandLists_EveryConstantBufferBindIsPrecededByAnUnsetOfTheSameSpan()
        {
            var harness = new D3D11BindFixtures.Harness(unsetConstantBuffersBeforeSet: true);
            using D3D11ResourceLayout layout = D3D11BindFixtures.ShadowLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.ShadowSet(layout);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            emitter.SetGraphicsResourceSet(0, set, 0);
            harness.Log.Reset();

            emitter.Draw(3, 1, 0, 0);

            Assert.True(harness.Binds.UnsetsConstantBuffersBeforeSet);
            Assert.Equal(
                new[]
                {
                    "VSSetConstantBuffers1(0,1,unset)",
                    $"VSSetConstantBuffers1(0,1,{Id(harness, set, 0)}@0+16)",
                },
                harness.BindTrace());
        }

        /// <summary>And it applies to the offsets-only path too, which is the path the defect it works around
        /// actually bites on: the shadow pass rebinds one buffer thousands of times and changes nothing but the
        /// first constant.</summary>
        [Fact]
        public void WithEmulatedCommandLists_TheOffsetsOnlyPathIsUnsetToo()
        {
            var harness = new D3D11BindFixtures.Harness(unsetConstantBuffersBeforeSet: true);
            using D3D11ResourceLayout layout = D3D11BindFixtures.ShadowLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.ShadowSet(layout);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            emitter.SetGraphicsResourceSet(0, set, 0);
            emitter.Draw(3, 1, 0, 0);
            harness.Log.Reset();

            emitter.SetGraphicsResourceSet(0, set, 256);
            emitter.Draw(3, 1, 0, 0);

            Assert.Equal(
                new[]
                {
                    "VSSetConstantBuffers1(0,1,unset)",
                    $"VSSetConstantBuffers1(0,1,{Id(harness, set, 0)}@16+16)",
                },
                harness.BindTrace());
        }

        // ---- Files and stages that do not participate -------------------------------------------------------

        /// <summary>A compute set reaching the <c>u</c> file binds through <c>CSSetUnorderedAccessViews</c>, and
        /// the read-write structured buffers share that counter with storage textures, so one array carries
        /// both.</summary>
        [Fact]
        public void AComputeSet_BindsItsUnorderedAccessViewsInOneArray()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout layout = D3D11BindFixtures.Layout(
                D3D11BindFixtures.U("Params", GpuShaderStages.Compute),
                D3D11BindFixtures.StructRW("H0Buf"),
                D3D11BindFixtures.StructRW("WorkBuf"));
            using D3D11ResourceSet set = D3D11BindFixtures.Set(
                layout, new FakeBuffer(256), new FakeBuffer(64), new FakeBuffer(64));
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetComputePipeline(new D3D11BindFlushTests.FakeComputePipeline(layout));
            emitter.SetComputeResourceSet(0, set);
            harness.Log.Reset();

            emitter.Dispatch(1, 1, 1);

            Assert.Equal(2, harness.BindTrace().Length);
            Assert.Equal(1, harness.Log.Count(D3D11NativeCall.CSSetConstantBuffers1));
            Assert.Equal(1, harness.Log.Count(D3D11NativeCall.CSSetUnorderedAccessViews));
        }

        /// <summary>
        /// AN UNORDERED-ACCESS BINDING OUTSIDE COMPUTE IS REFUSED BY NAME. Direct3D 11 binds a pixel-shader
        /// unordered-access view through <c>OMSetRenderTargetsAndUnorderedAccessViews</c> alongside the render
        /// targets rather than through a stage setter, so the framebuffer bind would have to carry it. No shipped
        /// layout declares one, so the path does not exist and refusing is better than binding nothing.
        /// </summary>
        [Fact]
        public void AnUnorderedAccessBindingOutsideCompute_IsRefused()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout layout = D3D11BindFixtures.Layout(
                new GpuResourceLayoutElement("Storage", GpuResourceKind.TextureReadWrite, GpuShaderStages.Fragment));
            using D3D11ResourceSet set = D3D11BindFixtures.Set(layout, D3D11BindFixtures.Texture());
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            emitter.SetGraphicsResourceSet(0, set);

            Assert.Throws<System.NotSupportedException>(() => emitter.Draw(3, 1, 0, 0));
        }

        // The trace id of the resource bound at layout element <paramref name="element"/>, which is what the log
        // prints. Read AFTER the trace exists, so it returns the id the emitter already assigned rather than
        // minting a new one. A constant-buffer bind names the BUFFER rather than the resource as handed in,
        // because a GpuBufferRange is a window onto a buffer and the window is carried in the first constant.
        static string Id(D3D11BindFixtures.Harness harness, D3D11ResourceSet set, int element)
        {
            D3D11BoundResource binding = set.Bindings[element];
            return harness.Log.Id(
                binding.Kind == GpuResourceKind.UniformBuffer ? binding.Buffer : binding.Resource);
        }
    }
}
