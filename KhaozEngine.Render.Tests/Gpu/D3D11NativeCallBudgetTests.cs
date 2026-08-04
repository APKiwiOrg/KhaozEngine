using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE NATIVE-CALL BUDGET (decision T2), a device-free <c>[Fact]</c> suite that runs on every
    /// <c>dotnet test</c>, on macOS and on the cheap Linux leg. It exists to catch ONE class of defect: the #418
    /// fan-out, where a bind cost one native call per resource per stage and the shadow pass paid 42 calls for a
    /// seven-element set.
    /// <para>
    /// WHAT IS THE GATE AND WHAT IS DOCUMENTATION, because the difference is the whole design of this file.
    /// The GATE is:
    /// </para>
    /// <list type="number">
    /// <item><description>The four STRUCTURAL INVARIANTS: zero <c>Create*</c> (a compile error by construction),
    /// zero <c>Map</c> or <c>Unmap</c> during a replay, exactly one <c>ClearState</c> per submit, and one
    /// <c>RSSetViewports</c> plus one <c>RSSetScissorRects</c> per framebuffer CHANGE with zero for a redundant
    /// re-bind.</description></item>
    /// <item><description>The MARGINAL per-draw and per-mesh deltas: five distinct meshes against one, and
    /// eighteen draws against six, move the total by an exact delta, and an offsets-only rebind is exactly one
    /// call per visible stage.</description></item>
    /// <item><description>TRACE IDENTITY across instance count: eight instances of one mesh and one instance
    /// produce the same binding trace.</description></item>
    /// <item><description>UPPER BOUNDS on the full-activation fan-out.</description></item>
    /// </list>
    /// <para>
    /// The ABSOLUTE TOTALS are documentation. They are asserted so the number in this file is a measured one
    /// rather than a claim, and they may be updated freely whenever a legitimate change moves them. A test
    /// routinely edited to match reality stops being a gate, which is why the gate is the deltas: the per-draw
    /// delta jumping from two to eight is the #418 defect returning, and no legitimate renderer change causes
    /// that.
    /// </para>
    /// <para>
    /// THE NAME DELIBERATELY DOES NOT CONTAIN "Golden". <c>cross-platform-gpu.yml</c> selects with
    /// <c>--filter FullyQualifiedName~Golden</c>, and this must run on the Linux leg rather than inside the golden
    /// filter.
    /// </para>
    /// <para>
    /// WHAT IT MEASURES IS THE SHIPPED PATH. The schedule and the fan-out live in <see cref="D3D11BindFlush"/> and
    /// <see cref="D3D11SetActivation"/>, which the real emitter uses unchanged, and the guards live in
    /// <see cref="D3D11DeviceState"/>. What is device-free here is the SINK: which method name a register file
    /// plus a stage picks, through <see cref="D3D11NativeCallName"/>. Decision T3's WARP <c>[GpuFact]</c> guards
    /// that one translation, and it has landed beside this file as
    /// <see cref="D3D11NativeCallParityGpuTests"/>: it drives the real emitter on a live device and reads the
    /// context back with the <c>Get*</c> counterparts, so the arm-to-stage routing and the Vortice array-overload
    /// count semantics are checked against a device rather than against this harness's own model of one.
    /// </para>
    /// </summary>
    public sealed class D3D11NativeCallBudgetTests
    {
        // ---- The measured absolute totals. DOCUMENTATION, not the gate. Update freely. ----------------------
        //
        // Measured on the frame RenderFrame builds below:
        //   one mesh,   six draws,  one instance   ->  42 native calls
        //   one mesh,   eighteen draws             ->  66
        //   five meshes, eighteen draws            ->  82
        // The fixed part is 26 and the rest is 4 per distinct mesh plus 2 per draw. The 26 is one ClearState,
        // three for the framebuffer change, two clears, seven for the first pipeline bind, two for the extra
        // width of the per-draw set's one FULL activation over its offsets-only pushes, and eleven for the tail:
        // one drained bind, SIX for the second pipeline (the two fixtures declare the same primitive topology, so
        // that one state object of the seven is not re-issued), three for the full activation the switch's wipe
        // makes the next bind owe, and one draw.

        const int FixedHead = 26;
        const int PerMesh = 4;
        const int PerDraw = 2;

        // ---- (1) The structural invariants -----------------------------------------------------------------

        /// <summary>
        /// ZERO <c>Create*</c>, AND IT IS A COMPILE ERROR RATHER THAN AN ASSERTION (decision X1). Every SRV, RTV,
        /// DSV, UAV and state object is created at resource, set or pipeline creation, so there is no way to
        /// create one during a replay: neither emission seam has a <c>Create</c> member and the call vocabulary
        /// names none. All 25 <c>DEVICE_REMOVED</c> stacks in the incumbent's field reports surfaced inside a view
        /// constructor reached from activation, which is the failure this shape rules out.
        /// <para>
        /// Asserted by reflection anyway, because "there is no member" is only a compile error while it stays
        /// true, and adding one would be a one-line change that no other test in the suite would notice.
        /// </para>
        /// </summary>
        [Fact]
        public void Invariant_NoEmissionSeamCanCreateAnythingDuringAReplay()
        {
            string[] offenders = new[] { typeof(ID3D11Emitter), typeof(ID3D11BindSink) }
                .SelectMany(t => t.GetMethods())
                .Select(m => m.DeclaringType!.Name + "." + m.Name)
                .Where(name => name.Contains("Create", StringComparison.Ordinal))
                .Concat(Enum.GetNames<D3D11NativeCall>()
                    .Where(n => n.StartsWith("Create", StringComparison.Ordinal)))
                .ToArray();

            Assert.True(offenders.Length == 0,
                "Creating a Direct3D object during a replay is decision X1's compile error, so neither emission "
                + "seam may name one: " + string.Join(", ", offenders));
        }

        /// <summary>
        /// ZERO <c>Map</c> AND ZERO <c>Unmap</c> DURING A REPLAY. The uniform ring is mapped
        /// <c>MAP_WRITE_NO_OVERWRITE</c> for the whole record phase and unmapped at the head of the next
        /// <c>Submit</c>, BEFORE the replay, because Direct3D 11 does not permit a draw against a mapped resource.
        /// A map landing inside a replay would mean a bound constant buffer was mapped underneath a draw.
        /// <para>
        /// EXECUTABLE RATHER THAN VACUOUS, which needed two things: a call vocabulary that can NAME a map, and a
        /// ring whose two context calls reach the same trace as the emitter's. Both are here, so the assertion is
        /// over a trace that genuinely contains maps rather than over one that could not have shown them.
        /// </para>
        /// </summary>
        [Fact]
        public void Invariant_AReplayContainsNoMapAndNoUnmap()
        {
            var log = new D3D11NativeCallLog();
            var submitLock = new object();
            var allocator = new D3D11RingAllocator(3, new FakeD3D11Completion(), submitLock);
            using var memory = new D3D11BindFixtures.TracedRingMemory(D3D11UniformRing.TotalBytesFor(512, 3), log);
            var ring = new D3D11UniformRing(allocator, memory, 512);
            var buffer = new FakeRingBackedBuffer(ring);
            var signal = new D3D11SubmitSignalTests.FakeD3D11SubmitSignal();

            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            using D3D11ResourceLayout layout = D3D11BindFixtures.ShadowLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.ShadowSet(layout, buffer);

            using D3D11CommandRecorder<D3D11StreamEmitter> list = D3D11CommandDrivers.CreateDeferred();
            list.Begin();
            list.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            for (int draw = 0; draw < 8; draw++)
            {
                // A record-time uniform write, which is what maps the ring and what a replay must never do.
                list.UpdateBuffer(buffer, 0u, new byte[] { (byte)draw, 1, 2, 3 });
                list.SetGraphicsResourceSet(0, set, (uint)draw * 256u);
                list.DrawIndexed(6, 1, 0, 0, 0);
            }

            list.End();
            D3D11CommandDrivers.Submit(submitLock, list, ref emitter, signal, null, allocator);

            Assert.Contains("Map(NO_OVERWRITE)", log.Trace);
            Assert.Contains("Unmap()", log.Trace);

            int replayStart = log.Trace.ToList().IndexOf("ClearState()");
            Assert.True(replayStart >= 0, "The replay never opened, so this asserted nothing.");

            string[] duringReplay = log.Trace.Skip(replayStart).ToArray();
            Assert.DoesNotContain("Map(NO_OVERWRITE)", duringReplay);
            Assert.DoesNotContain("Unmap()", duringReplay);
        }

        /// <summary>EXACTLY ONE <c>ClearState</c> PER SUBMIT (decision R3), which is what makes the redundancy
        /// caches trustable at all: it is the one moment the caches and the context are guaranteed to agree.
        /// </summary>
        [Fact]
        public void Invariant_EachSubmitOpensWithExactlyOneClearState()
        {
            var harness = new D3D11BindFixtures.Harness();
            using Scene scene = Scene.Build();
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            // The harness already opened one recording, so this is the second and third.
            emitter.End();
            for (int frame = 0; frame < 3; frame++)
            {
                emitter.Begin();
                RenderFrame(emitter, scene, distinctMeshes: 2, draws: 4, instancesPerDraw: 1);
                emitter.End();
            }

            Assert.Equal(3, harness.Log.Count(D3D11NativeCall.ClearState));
        }

        /// <summary>
        /// ONE <c>RSSetViewports</c> AND ONE <c>RSSetScissorRects</c> PER FRAMEBUFFER CHANGE, AND ZERO FOR A
        /// REDUNDANT RE-BIND (section 9.4). There is no <c>SetViewport</c> on the seam, so a backend that does not
        /// replicate the auto-applied full viewport rasterises nothing, and one that emits unconditionally
        /// silently restores the full scissor over a live one.
        /// </summary>
        [Fact]
        public void Invariant_ViewportAndScissorFollowFramebufferChangesOnly()
        {
            var harness = new D3D11BindFixtures.Harness();
            using Scene scene = Scene.Build();
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            RenderFrame(emitter, scene, distinctMeshes: 2, draws: 4, instancesPerDraw: 1);
            emitter.SetFramebuffer(scene.Framebuffer);      // redundant
            emitter.SetFramebuffer(scene.Framebuffer);      // redundant
            emitter.SetFramebuffer(scene.SecondTarget);     // a change
            emitter.SetFramebuffer(scene.Framebuffer);      // a change back

            Assert.Equal(3, harness.Log.Count(D3D11NativeCall.OMSetRenderTargets));
            Assert.Equal(3, harness.Log.Count(D3D11NativeCall.RSSetViewports));
            Assert.Equal(3, harness.Log.Count(D3D11NativeCall.RSSetScissorRects));
        }

        /// <summary>
        /// THE MID-FRAME PIPELINE SWITCH DRAINS UNDER THE OUTGOING LAYOUTS, AND AHEAD OF THE INCOMING PIPELINE'S
        /// STATE CALLS. This is the clause a total cannot see on its own: a drain taken under the incoming
        /// numbering issues the same NUMBER of calls at different registers, which compiles, draws and renders the
        /// wrong constants. So it is asserted as the exact line and its position.
        /// <para>
        /// ALL THREE BASES ARE PINNED, not just the constant buffer's, because the OUTGOING pipeline's model
        /// layout contributes one constant buffer, four shader resources and two samplers, so the drained set
        /// lands at <c>b1 t4 s2</c>. Under the tail pipeline it would be <c>b0 t0 s0</c>, and in fact it would not
        /// bind at all: the tail pipeline declares one layout, so slot one does not exist under it and a drain
        /// taken after the switch throws.
        /// </para>
        /// </summary>
        [Fact]
        public void Invariant_AMidFramePipelineSwitchDrainsUnderTheOutgoingLayouts()
        {
            var harness = new D3D11BindFixtures.Harness();
            using Scene scene = Scene.Build();
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(scene.Pipeline);
            emitter.SetGraphicsResourceSet(1, scene.PerDraw, 256);
            harness.Log.Reset();

            emitter.SetPipeline(scene.TailPipeline);

            Assert.Equal(
                $"VSSetConstantBuffers1(1,1,{harness.Log.Id(scene.PerDrawBuffer)}@16+16)",
                harness.Log.Trace[0]);
            Assert.StartsWith("PSSetShaderResources(4,1,", harness.Log.Trace[1], StringComparison.Ordinal);
            Assert.StartsWith("PSSetSamplers(2,1,", harness.Log.Trace[2], StringComparison.Ordinal);

            // Those three, then the incoming pipeline's state calls, and nothing else: the wipe that follows the
            // drain issues no call of its own. Six rather than seven, because the two pipeline fixtures declare
            // the same primitive topology and the redundancy cache holds that one.
            Assert.Equal(9, harness.Log.TotalCalls);
        }

        // ---- (2) The marginals, which ARE the gate ---------------------------------------------------------

        /// <summary>
        /// FIVE DISTINCT MESHES AGAINST ONE MOVES THE TOTAL BY AN EXACT PER-MESH DELTA, and that delta is one
        /// FULL ACTIVATION of the model set. Four native calls, from a seven-element set. If it becomes eight or
        /// fourteen, the array batching has come apart and #418 is back.
        /// </summary>
        [Fact]
        public void FiveDistinctMeshes_CostExactlyFourMoreFullActivationsThanOne()
        {
            int one = TotalFor(distinctMeshes: 1, draws: 18);
            int five = TotalFor(distinctMeshes: 5, draws: 18);

            Assert.Equal(4 * PerMesh, five - one);
            Assert.Equal(4, PerMesh);
        }

        /// <summary>
        /// EIGHTEEN DRAWS AGAINST SIX MOVES THE TOTAL BY AN EXACT PER-DRAW DELTA, and that delta is TWO: one
        /// offsets-only constant-buffer push for the per-draw uniform window, and the draw itself. That is the
        /// shadow pass's shape, thousands of times a frame, and it is the number the whole design is for.
        /// <para>
        /// TWO OF THE THREE MUTATIONS THIS FILE EXISTS TO CATCH LAND ON THIS NUMBER. The frame rebinds the
        /// per-draw window TWICE between two draws, so a flush moved from the draw to the bind makes it three, and
        /// the per-draw set carries a texture and a sampler, so tracking that collapses to always-Full makes it
        /// four. Both used to be free.
        /// </para>
        /// </summary>
        [Fact]
        public void EighteenDraws_CostExactlyTwelveMoreDrawsWorthThanSix()
        {
            int six = TotalFor(distinctMeshes: 1, draws: 6);
            int eighteen = TotalFor(distinctMeshes: 1, draws: 18);

            Assert.Equal(12 * PerDraw, eighteen - six);
            Assert.Equal(2, PerDraw);
        }

        /// <summary>An offsets-only rebind is EXACTLY one call per visible stage, which is the marginal the two
        /// deltas above are built out of. One dynamic uniform buffer visible to one stage is one call: not one per
        /// element, not one per resource, and not a full re-activation.</summary>
        [Fact]
        public void AnOffsetsOnlyRebind_IsExactlyOneCallPerVisibleStage()
        {
            Assert.Equal(1, OffsetsOnlyCallsFor(GpuShaderStages.Vertex));
            Assert.Equal(2, OffsetsOnlyCallsFor(GpuShaderStages.Vertex | GpuShaderStages.Fragment));
        }

        /// <summary>The absolute totals, recorded so the numbers in this file are measured rather than claimed.
        /// DOCUMENTATION: a legitimate change to the frame or to the emitted trace moves these and they are
        /// updated in the same commit, which is exactly what must NOT happen to the deltas above.</summary>
        [Theory]
        [InlineData(1, 6, 42)]
        [InlineData(1, 18, 66)]
        [InlineData(5, 18, 82)]
        public void TheAbsoluteTotals_AreWhatTheyWereMeasuredToBe(int distinctMeshes, int draws, int expected)
        {
            Assert.Equal(expected, TotalFor(distinctMeshes, draws));
            Assert.Equal(expected, FixedHead + (PerMesh * distinctMeshes) + (PerDraw * draws));
        }

        // ---- (3) Trace identity across instance count ------------------------------------------------------

        /// <summary>
        /// EIGHT INSTANCES OF ONE MESH AND ONE INSTANCE PRODUCE THE SAME BINDING TRACE. Instancing is the classic
        /// case where per-object cost must not exist at all: the instance count is an argument of one draw call,
        /// so nothing about the binding work may scale with it.
        /// <para>
        /// The instance count IS in the trace, as the draw's own second argument, so the two full traces are not
        /// byte-identical and asserting that they were would be asserting something false. What is byte-identical
        /// is the BINDING trace, and the draw count is equal alongside it, which together say exactly what the
        /// gate means: the instance count changes one argument of one call and nothing else.
        /// </para>
        /// </summary>
        [Fact]
        public void EightInstancesOfOneMesh_ProduceTheSameBindingTraceAsOne()
        {
            var one = new D3D11BindFixtures.Harness();
            var eight = new D3D11BindFixtures.Harness();
            using Scene first = Scene.Build();
            using Scene second = Scene.Build();

            RenderFrame(one.Emitter, first, distinctMeshes: 1, draws: 6, instancesPerDraw: 1);
            RenderFrame(eight.Emitter, second, distinctMeshes: 1, draws: 6, instancesPerDraw: 8);

            Assert.Equal(one.BindTrace(), eight.BindTrace());
            Assert.Equal(one.Log.TotalCalls, eight.Log.TotalCalls);
            Assert.Equal(
                one.Log.Count(D3D11NativeCall.DrawIndexedInstanced),
                eight.Log.Count(D3D11NativeCall.DrawIndexedInstanced));

            // And the one thing that legitimately differs is the argument it is supposed to be.
            Assert.Contains("DrawIndexedInstanced(6,8,0,0,0)", eight.Log.Trace);
            Assert.Contains("DrawIndexedInstanced(6,1,0,0,0)", one.Log.Trace);
        }

        // ---- (4) Upper bounds on the full-activation fan-out ------------------------------------------------

        /// <summary>
        /// THE FAN-OUT BOUND: one native call per register FILE per STAGE, so at most four times six, and in
        /// practice six. The model set is four and the WATER set is the worst case in the engine at six, both from
        /// seven elements. Asserted as bounds rather than as equalities, because the gate is that the fan-out does
        /// not scale with element count and a legitimate layout change may move an exact figure.
        /// </summary>
        [Theory]
        [InlineData("model", 4)]
        [InlineData("water", 6)]
        public void AFullActivation_StaysWithinItsFanOutBound(string which, int bound)
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout layout = which == "model"
                ? D3D11BindFixtures.ModelLayout()
                : D3D11BindFixtures.WaterLayout();
            using D3D11ResourceSet set = which == "model"
                ? D3D11BindFixtures.ModelSet(layout)
                : D3D11BindFixtures.WaterSet(layout);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            emitter.SetGraphicsResourceSet(0, set, 0);
            harness.Log.Reset();
            emitter.Draw(3, 1, 0, 0);

            Assert.Equal(bound, harness.BindTrace().Length);
            Assert.True(harness.BindTrace().Length <= 4 * 6,
                "One call per register file per stage bounds a full activation at four files times six stages.");
        }

        /// <summary>
        /// AND THE BOUND DOES NOT MOVE WHEN THE SET GETS WIDER, which is the property #418 broke. A set of sixteen
        /// textures visible to one stage is ONE shader-resource call, not sixteen, so widening a set by nine
        /// elements adds nothing to its activation cost.
        /// </summary>
        [Fact]
        public void WideningASet_DoesNotWidenItsFanOut()
        {
            int narrow = FullActivationCallsForTextureCount(4);
            int wide = FullActivationCallsForTextureCount(16);

            Assert.Equal(narrow, wide);
            Assert.Equal(2, wide);   // one constant-buffer call, one shader-resource array
        }

        // ---- The frame the budget is taken over ------------------------------------------------------------

        // A frame built the way a renderer builds one: bind the target, clear it, bind the pipeline, then walk the
        // draws GROUPED BY MATERIAL, rebinding the material set when the mesh changes and pushing a per-draw
        // uniform window on every draw. Grouping is what makes the marginals separable: the material set changes
        // once per distinct mesh, and the per-draw window changes once per draw.
        //
        // THREE THINGS IN HERE ARE NOT DECORATION. Each one exists because the budget, taken over the plain frame,
        // was blind to a mutation its sibling suites catch, and a gate that cannot see a break is not a gate:
        //
        //  1. The per-draw set carries a TEXTURE AND A SAMPLER (see Scene.Build). A full activation of it is three
        //     calls against an offsets-only push's one, so collapsing the three-state tracking to always-Full
        //     re-pushes them on every draw and moves the per-draw marginal from two to four. With a lone dynamic
        //     UBO the two costs were both one call and the collapse was invisible.
        //  2. The per-draw window is rebound TWICE between two draws, which is rule 7's collapse. A flush moved
        //     from the draw to the bind issues both pushes and moves the same marginal to three. The plain frame
        //     never rebound one slot between two draws, so that move cost nothing.
        //  3. The tail leaves a set PENDING across a mid-frame pipeline switch, which is the only thing that puts
        //     rule 5's drain in the measured frame at all: the plain frame called SetPipeline once, before any
        //     bind, so the drain never ran. Dropping the drain now drops a call from the total, and running it
        //     under the INCOMING layouts throws, because the tail pipeline declares ONE layout and the pending set
        //     sits at slot one.
        static void RenderFrame(D3D11NativeTraceEmitter emitter, Scene scene, int distinctMeshes, int draws,
            int instancesPerDraw)
        {
            emitter.SetFramebuffer(scene.Framebuffer);
            emitter.ClearColorTarget(0, KhaozEngine.Primitives.Color.Black);
            emitter.ClearDepthStencil(1f);
            emitter.SetPipeline(scene.Pipeline);

            uint offset = 0;
            for (int mesh = 0; mesh < distinctMeshes; mesh++)
            {
                emitter.SetGraphicsResourceSet(0, scene.Materials[mesh]);

                int drawsForThisMesh = (draws / distinctMeshes) + (mesh < draws % distinctMeshes ? 1 : 0);
                for (int i = 0; i < drawsForThisMesh; i++)
                {
                    // Twice, at two windows, and the draw pays for ONE push.
                    emitter.SetGraphicsResourceSet(1, scene.PerDraw, offset);
                    emitter.SetGraphicsResourceSet(1, scene.PerDraw, offset + 128);
                    emitter.DrawIndexed(6, (uint)instancesPerDraw, 0, 0, 0);
                    offset += 256;
                }
            }

            RenderTail(emitter, scene, offset, instancesPerDraw);
        }

        // The fixed tail: a mid-frame pipeline switch with a set left pending across it. Everything above it
        // scales with the mesh and draw counts and this does not, so the marginals stay exactly what they say.
        static void RenderTail(D3D11NativeTraceEmitter emitter, Scene scene, uint offset, int instancesPerDraw)
        {
            // Pending at the moment of the switch, so the drain issues it under the OUTGOING numbering (slot one,
            // past the model layout's one constant buffer, so b1).
            emitter.SetGraphicsResourceSet(1, scene.PerDraw, offset);
            emitter.SetPipeline(scene.TailPipeline);

            // The switch forgot the records, so the same set at slot zero of the tail pipeline owes a FULL
            // activation and numbers from b0 t0 s0.
            emitter.SetGraphicsResourceSet(0, scene.PerDraw, offset);
            emitter.DrawIndexed(6, (uint)instancesPerDraw, 0, 0, 0);
        }

        static int TotalFor(int distinctMeshes, int draws)
        {
            var harness = new D3D11BindFixtures.Harness();
            using Scene scene = Scene.Build();

            RenderFrame(harness.Emitter, scene, distinctMeshes, draws, instancesPerDraw: 1);

            // The opening ClearState is part of the budget and the harness cleared it out of the log, so it is
            // added back rather than the harness being reshaped for one caller.
            return harness.Log.TotalCalls + 1;
        }

        static int OffsetsOnlyCallsFor(GpuShaderStages stages)
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout layout = D3D11BindFixtures.Layout(
                D3D11BindFixtures.U("U", stages, dynamic: true));
            using D3D11ResourceSet set = D3D11BindFixtures.Set(layout, new FakeBuffer(4096));
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            emitter.SetGraphicsResourceSet(0, set, 0);
            emitter.Draw(3, 1, 0, 0);
            harness.Log.Reset();

            emitter.SetGraphicsResourceSet(0, set, 256);
            emitter.Draw(3, 1, 0, 0);

            return harness.BindTrace().Length;
        }

        static int FullActivationCallsForTextureCount(int textures)
        {
            var elements = new List<GpuResourceLayoutElement>
            {
                D3D11BindFixtures.U("U", GpuShaderStages.Fragment),
            };
            var resources = new List<IGpuBindableResource> { new FakeBuffer(256) };
            for (int i = 0; i < textures; i++)
            {
                elements.Add(D3D11BindFixtures.T("Tex" + i, GpuShaderStages.Fragment));
                resources.Add(D3D11BindFixtures.Texture());
            }

            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout layout = D3D11BindFixtures.Layout(elements.ToArray());
            using D3D11ResourceSet set = D3D11BindFixtures.Set(layout, resources.ToArray());
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            emitter.SetGraphicsResourceSet(0, set);
            harness.Log.Reset();
            emitter.Draw(3, 1, 0, 0);

            return harness.BindTrace().Length;
        }

        /// <summary>
        /// The fixed contents of the frame: one target, a second one to change to, the pipeline over the model
        /// layout plus a per-draw layout, a SECOND pipeline over the per-draw layout alone for the tail's
        /// mid-frame switch, five materials and one per-draw set.
        /// <para>
        /// THE PER-DRAW SET IS NOT A LONE DYNAMIC UBO, and that is deliberate rather than a fuller-looking
        /// fixture. With one element a full activation and an offsets-only push both cost one call, so the
        /// difference between the two states was unmeasurable here and the three-state tracking could collapse to
        /// always-Full without moving a number. A texture and a sampler the pixel stage reads make a full
        /// activation three calls, which is what gives the per-draw marginal something to move by.
        /// </para>
        /// <para>
        /// THE TAIL PIPELINE DECLARES ONE LAYOUT ON PURPOSE. The tail leaves a set pending at slot ONE across the
        /// switch, so a drain taken under the incoming layouts rather than the outgoing ones addresses a slot the
        /// tail pipeline does not declare and throws, instead of quietly binding at the wrong register.
        /// </para>
        /// </summary>
        sealed class Scene : IDisposable
        {
            const int MaxMaterials = 5;

            Scene(D3D11ResourceLayout model, D3D11ResourceLayout perDraw)
            {
                ModelLayout = model;
                PerDrawLayout = perDraw;
                Pipeline = D3D11BindFixtures.Pipeline(model, perDraw);
                TailPipeline = D3D11BindFixtures.Pipeline(perDraw);
                Framebuffer = Target(1280, 720);
                SecondTarget = Target(512, 512);
                Materials = new D3D11ResourceSet[MaxMaterials];
                for (int i = 0; i < MaxMaterials; i++) Materials[i] = D3D11BindFixtures.ModelSet(model);
                PerDrawBuffer = new FakeBuffer(65536);
                PerDraw = D3D11BindFixtures.Set(perDraw, new GpuBufferRange(PerDrawBuffer, 0, 256),
                    D3D11BindFixtures.Texture(), new FakeSampler());
            }

            internal D3D11ResourceLayout ModelLayout { get; }
            internal D3D11ResourceLayout PerDrawLayout { get; }
            internal D3D11StateCacheTests.FakeD3D11Pipeline Pipeline { get; }
            internal D3D11StateCacheTests.FakeD3D11Pipeline TailPipeline { get; }
            internal FakeFramebuffer Framebuffer { get; }
            internal FakeFramebuffer SecondTarget { get; }
            internal D3D11ResourceSet[] Materials { get; }
            internal D3D11ResourceSet PerDraw { get; }

            /// <summary>The buffer behind <see cref="PerDraw"/>, for the one assertion that names a register.
            /// </summary>
            internal IGpuBuffer PerDrawBuffer { get; }

            internal static Scene Build() => new(
                D3D11BindFixtures.ModelLayout(),
                D3D11BindFixtures.Layout(
                    D3D11BindFixtures.U("Draw", GpuShaderStages.Vertex, dynamic: true),
                    D3D11BindFixtures.T("DrawTex", GpuShaderStages.Fragment),
                    D3D11BindFixtures.S("DrawSamp", GpuShaderStages.Fragment)));

            public void Dispose()
            {
                PerDraw.Dispose();
                foreach (D3D11ResourceSet material in Materials) material.Dispose();
                PerDrawLayout.Dispose();
                ModelLayout.Dispose();
            }

            static FakeFramebuffer Target(uint width, uint height) => new(
                new GpuOutputDescription(GpuPixelFormat.D32FloatS8UInt, GpuPixelFormat.R8G8B8A8UNorm),
                width, height);
        }
    }
}
