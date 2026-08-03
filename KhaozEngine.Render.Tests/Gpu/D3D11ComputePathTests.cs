using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE COMPUTE PATH'S OWN DECISIONS, all device-free: C1's SRV-versus-UAV auto-unbind in BOTH directions, C2's
    /// RAW structured views, C3's absence of a barrier member, and C4's single-subresource resolve, plus the copies
    /// and the mip generation the same row owes.
    /// <para>
    /// Driven through <see cref="D3D11NativeTraceEmitter"/>, which applies the SHIPPED schedule (the same
    /// <see cref="D3D11BindFlush"/>, <see cref="D3D11SetActivation"/> and <see cref="D3D11ViewConflicts"/> the real
    /// emitter uses unchanged) and writes down the <c>ID3D11DeviceContext</c> calls it would have made. So the
    /// unbind these pin IS the one a frame issues. What they cannot reach is which Vortice method the real emitter
    /// picks, which needs a device and arrives with the WARP leg, where the compute <c>[GpuFact]</c> suite
    /// (<see cref="ComputeTextureHandoffGpuTests"/> above all) is the regression evidence the issue names.
    /// </para>
    /// </summary>
    public sealed class D3D11ComputePathTests
    {
        // ---- Decision C1: the auto-unbind, both directions ---------------------------------------------------

        /// <summary>
        /// RULE 1'S HANDOFF, IN THE DIRECTION THE SEAM DOCUMENTS. A compute pass writes a storage texture through
        /// its unordered access view, then a graphics pass samples the same texture. <c>GpuInterfaces.cs</c> names
        /// this backend's mechanism in as many words ("Direct3D11 unbinds the UAV as the SRV is bound"), so the
        /// draw's flush must null the <c>u</c> register before it fills the <c>t</c> one.
        /// <para>
        /// AND THE TWO LAND IN ONE FLUSH, which is the same-batch half of C1 and the whole difference from the
        /// fork. Veldrid nulls the slot and marks the owning SET fully dirty, so the register comes back at the
        /// NEXT command through a whole re-activation. Here both calls are in the trace of the single
        /// <c>Draw</c> below, in that order, with nothing between them.
        /// </para>
        /// </summary>
        [Fact]
        public void AGraphicsSrvBind_UnbindsTheComputeUavOfTheSameTexture_InTheSameFlush()
        {
            var harness = new D3D11BindFixtures.Harness();
            FakeTexture storage = D3D11BindFixtures.Texture();
            using D3D11ResourceLayout computeLayout = StorageLayout();
            using D3D11ResourceLayout drawLayout = SampledLayout();
            using D3D11ResourceSet computeSet = D3D11BindFixtures.Set(computeLayout, storage);
            using D3D11ResourceSet drawSet = D3D11BindFixtures.Set(drawLayout, storage);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetComputePipeline(new D3D11BindFlushTests.FakeComputePipeline(computeLayout));
            emitter.SetComputeResourceSet(0, computeSet);
            emitter.Dispatch(1, 1, 1);

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(drawLayout));
            emitter.SetGraphicsResourceSet(0, drawSet);
            harness.Log.Reset();
            emitter.Draw(3, 1, 0, 0);

            Assert.Equal(
                new[]
                {
                    "CSSetUnorderedAccessViews(0,1,-)",
                    $"PSSetShaderResources(0,1,{harness.Log.Id(storage)})",
                },
                harness.BindTrace());
        }

        /// <summary>
        /// THE OTHER DIRECTION, which is the half a mechanism-shaped reading of rule 1 leaves out. The ocean's
        /// ping-pong reads a buffer in one stage and writes it in the next, so a compute set binding a resource at
        /// a <c>u</c> register has to null whatever <c>t</c> register a GRAPHICS set left it at. Same shape, same
        /// flush, opposite files.
        /// </summary>
        [Fact]
        public void AComputeUavBind_UnbindsTheGraphicsSrvOfTheSameTexture_InTheSameFlush()
        {
            var harness = new D3D11BindFixtures.Harness();
            FakeTexture storage = D3D11BindFixtures.Texture();
            using D3D11ResourceLayout drawLayout = SampledLayout();
            using D3D11ResourceLayout computeLayout = StorageLayout();
            using D3D11ResourceSet drawSet = D3D11BindFixtures.Set(drawLayout, storage);
            using D3D11ResourceSet computeSet = D3D11BindFixtures.Set(computeLayout, storage);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(drawLayout));
            emitter.SetGraphicsResourceSet(0, drawSet);
            emitter.Draw(3, 1, 0, 0);

            emitter.SetComputePipeline(new D3D11BindFlushTests.FakeComputePipeline(computeLayout));
            emitter.SetComputeResourceSet(0, computeSet);
            harness.Log.Reset();
            emitter.Dispatch(1, 1, 1);

            Assert.Equal(
                new[]
                {
                    "PSSetShaderResources(0,1,-)",
                    $"CSSetUnorderedAccessViews(0,1,{harness.Log.Id(storage)})",
                },
                harness.BindTrace());
        }

        /// <summary>
        /// THE UNBIND OBEYS THE SAME BATCHING LAW AS THE BIND, which is what "under array batching this costs
        /// nothing extra" means in the spec: two conflicting registers are ONE array call over the span, not two
        /// calls. A per-register unbind would be the #418 fan-out defect arriving on the compute side.
        /// </summary>
        [Fact]
        public void TheUnbind_IsOneArrayCallOverTheSpanRatherThanOnePerRegister()
        {
            var harness = new D3D11BindFixtures.Harness();
            FakeTexture first = D3D11BindFixtures.Texture();
            FakeTexture second = D3D11BindFixtures.Texture();
            using D3D11ResourceLayout computeLayout = StorageLayout("A", "B");
            using D3D11ResourceLayout drawLayout = SampledLayout("A", "B");
            using D3D11ResourceSet computeSet = D3D11BindFixtures.Set(computeLayout, first, second);
            using D3D11ResourceSet drawSet = D3D11BindFixtures.Set(drawLayout, first, second);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetComputePipeline(new D3D11BindFlushTests.FakeComputePipeline(computeLayout));
            emitter.SetComputeResourceSet(0, computeSet);
            emitter.Dispatch(1, 1, 1);

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(drawLayout));
            emitter.SetGraphicsResourceSet(0, drawSet);
            harness.Log.Reset();
            emitter.Draw(3, 1, 0, 0);

            Assert.Equal(1, harness.Log.Count(D3D11NativeCall.CSSetUnorderedAccessViews));
            Assert.Equal("CSSetUnorderedAccessViews(0,2,-|-)", harness.BindTrace()[0]);
        }

        /// <summary>
        /// A NON-CONFLICTING REGISTER SWEPT IN BY THE SPAN IS REBOUND TO WHAT IT ALREADY HOLDS, never nulled. The
        /// span runs from the lowest conflicting register to the highest, so a live storage buffer at <c>u1</c>
        /// between two conflicting textures at <c>u0</c> and <c>u2</c> is inside it, and writing a null across it
        /// would unbind a resource the dispatch still writes with its owner's record still calling that slot
        /// clean. Same rule as the batched vertex flush, same reason.
        /// </summary>
        [Fact]
        public void ALiveRegisterInsideTheUnbindSpan_IsReboundRatherThanNulled()
        {
            var harness = new D3D11BindFixtures.Harness();
            FakeTexture conflicting = D3D11BindFixtures.Texture();
            var untouched = new FakeBuffer(64);
            FakeTexture alsoConflicting = D3D11BindFixtures.Texture();
            using D3D11ResourceLayout computeLayout = D3D11BindFixtures.Layout(
                Storage("A"), D3D11BindFixtures.StructRW("Work"), Storage("B"));
            using D3D11ResourceLayout drawLayout = SampledLayout("A", "B");
            using D3D11ResourceSet computeSet = D3D11BindFixtures.Set(
                computeLayout, conflicting, untouched, alsoConflicting);
            using D3D11ResourceSet drawSet = D3D11BindFixtures.Set(drawLayout, conflicting, alsoConflicting);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetComputePipeline(new D3D11BindFlushTests.FakeComputePipeline(computeLayout));
            emitter.SetComputeResourceSet(0, computeSet);
            emitter.Dispatch(1, 1, 1);

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(drawLayout));
            emitter.SetGraphicsResourceSet(0, drawSet);
            harness.Log.Reset();
            emitter.Draw(3, 1, 0, 0);

            Assert.Equal($"CSSetUnorderedAccessViews(0,3,-|{harness.Log.Id(untouched)}|-)",
                harness.BindTrace()[0]);
        }

        /// <summary>
        /// THE RAISE-TO-DIRTY, which is the half the fork's precedent shows and the half a same-batch unbind
        /// cannot do for itself. The draw has just nulled a register belonging to a COMPUTE slot, whose flush is
        /// not the one running, and that slot's record still says it is bound. Without the raise the next dispatch
        /// issues nothing for it and runs against a null unordered access view: no throw, no log, wrong output.
        /// </summary>
        [Fact]
        public void TheUnbind_RaisesTheOwningSlotBackToFull_SoTheNextFlushRebindsIt()
        {
            var harness = new D3D11BindFixtures.Harness();
            FakeTexture storage = D3D11BindFixtures.Texture();
            using D3D11ResourceLayout computeLayout = StorageLayout();
            using D3D11ResourceLayout drawLayout = SampledLayout();
            using D3D11ResourceSet computeSet = D3D11BindFixtures.Set(computeLayout, storage);
            using D3D11ResourceSet drawSet = D3D11BindFixtures.Set(drawLayout, storage);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetComputePipeline(new D3D11BindFlushTests.FakeComputePipeline(computeLayout));
            emitter.SetComputeResourceSet(0, computeSet);
            emitter.Dispatch(1, 1, 1);
            Assert.Equal(D3D11SlotDirty.Clean, harness.Binds.ComputeDirty(0));

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(drawLayout));
            emitter.SetGraphicsResourceSet(0, drawSet);
            emitter.Draw(3, 1, 0, 0);

            // The draw unbound the compute set's register, so the compute slot owes a FULL activation again even
            // though nothing rebound it. Full rather than offsets-only: the offsets-only path skips the two files
            // that can conflict entirely, so it would put nothing back.
            Assert.Equal(D3D11SlotDirty.Full, harness.Binds.ComputeDirty(0));

            harness.Log.Reset();
            emitter.Dispatch(1, 1, 1);

            Assert.Contains($"CSSetUnorderedAccessViews(0,1,{harness.Log.Id(storage)})", harness.BindTrace());
        }

        /// <summary>
        /// A REBIND THAT CONFLICTS WITH NOTHING COSTS NOTHING, which is the property that keeps C1 off the hot
        /// path. Two textures that never meet the other file produce exactly the fan-out decision R6 already
        /// freezes, with no extra call and no raised slot.
        /// </summary>
        [Fact]
        public void WithNoSharedResource_TheAutoUnbindIssuesNothingAndRaisesNothing()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout computeLayout = StorageLayout();
            using D3D11ResourceLayout drawLayout = SampledLayout();
            using D3D11ResourceSet computeSet = D3D11BindFixtures.Set(computeLayout, D3D11BindFixtures.Texture());
            using D3D11ResourceSet drawSet = D3D11BindFixtures.Set(drawLayout, D3D11BindFixtures.Texture());
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetComputePipeline(new D3D11BindFlushTests.FakeComputePipeline(computeLayout));
            emitter.SetComputeResourceSet(0, computeSet);
            emitter.Dispatch(1, 1, 1);

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(drawLayout));
            emitter.SetGraphicsResourceSet(0, drawSet);
            harness.Log.Reset();
            emitter.Draw(3, 1, 0, 0);

            Assert.Single(harness.BindTrace());
            Assert.Equal(0, harness.Log.Count(D3D11NativeCall.CSSetUnorderedAccessViews));
            Assert.Equal(D3D11SlotDirty.Clean, harness.Binds.ComputeDirty(0));
        }

        /// <summary>
        /// A STRUCTURED BUFFER BOUND AS A BARE BUFFER ONE SIDE AND AS A RANGE THE OTHER IS ONE RESOURCE AND ONE
        /// HAZARD. <see cref="GpuBufferRange"/> is a readonly STRUCT that implements
        /// <see cref="IGpuBindableResource"/>, so a set stores it BOXED and two boxes of the same window are two
        /// references. Comparing what the caller bound rather than the buffer underneath would call this pair
        /// unrelated and skip the unbind, silently, which is the exact shape the ocean's ping-pong would hit.
        /// </summary>
        [Fact]
        public void ABufferRangeAndItsBuffer_AreOneResourceForTheUnbind()
        {
            var harness = new D3D11BindFixtures.Harness();
            var shared = new FakeBuffer(256);
            using D3D11ResourceLayout computeLayout = D3D11BindFixtures.Layout(D3D11BindFixtures.StructRW("Work"));
            using D3D11ResourceLayout drawLayout = D3D11BindFixtures.Layout(
                new GpuResourceLayoutElement("Read", GpuResourceKind.StructuredBufferReadOnly,
                    GpuShaderStages.Fragment));
            using D3D11ResourceSet computeSet = D3D11BindFixtures.Set(computeLayout, shared);
            using D3D11ResourceSet drawSet = D3D11BindFixtures.Set(drawLayout, new GpuBufferRange(shared, 0, 128));
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetComputePipeline(new D3D11BindFlushTests.FakeComputePipeline(computeLayout));
            emitter.SetComputeResourceSet(0, computeSet);
            emitter.Dispatch(1, 1, 1);

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(drawLayout));
            emitter.SetGraphicsResourceSet(0, drawSet);
            harness.Log.Reset();
            emitter.Draw(3, 1, 0, 0);

            Assert.Equal("CSSetUnorderedAccessViews(0,1,-)", harness.BindTrace()[0]);
            Assert.Equal(D3D11SlotDirty.Full, harness.Binds.ComputeDirty(0));
        }

        /// <summary>
        /// THE TRACKER DIES WITH THE REPLAY, like every other record in this backend. <c>ClearState</c> unbinds
        /// every shader resource and every unordered access view, so a tracker that survived the boundary would
        /// null a register holding nothing on the first draw of the next replay and charge a slot a full
        /// activation it does not owe.
        /// </summary>
        [Fact]
        public void AClearStateBoundary_ForgetsEveryTrackedRegister()
        {
            var harness = new D3D11BindFixtures.Harness();
            FakeTexture storage = D3D11BindFixtures.Texture();
            using D3D11ResourceLayout computeLayout = StorageLayout();
            using D3D11ResourceLayout drawLayout = SampledLayout();
            using D3D11ResourceSet computeSet = D3D11BindFixtures.Set(computeLayout, storage);
            using D3D11ResourceSet drawSet = D3D11BindFixtures.Set(drawLayout, storage);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetComputePipeline(new D3D11BindFlushTests.FakeComputePipeline(computeLayout));
            emitter.SetComputeResourceSet(0, computeSet);
            emitter.Dispatch(1, 1, 1);

            // The next submit: one ClearState, and nothing is bound on the context afterwards.
            emitter.End();
            emitter.Begin();

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(drawLayout));
            emitter.SetGraphicsResourceSet(0, drawSet);
            harness.Log.Reset();
            emitter.Draw(3, 1, 0, 0);

            Assert.Equal(new[] { $"PSSetShaderResources(0,1,{harness.Log.Id(storage)})" }, harness.BindTrace());
        }

        /// <summary>
        /// AN OFFSETS-ONLY REBIND CANNOT TRIP C1 AT ALL, and that is worth pinning because it is what keeps the
        /// shadow pass's thousands-per-frame path free of the tracker. Rule 3 pushes constant buffers and skips
        /// both files that can conflict, so there is nothing to compare and nothing to raise.
        /// </summary>
        [Fact]
        public void AnOffsetsOnlyRebind_TouchesNeitherFileThatCanConflict()
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

            Assert.Equal(0, harness.Log.Count(D3D11NativeCall.PSSetShaderResources));
            Assert.Equal(0, harness.Log.Count(D3D11NativeCall.CSSetUnorderedAccessViews));
        }

        // ---- The raise entry point itself --------------------------------------------------------------------

        /// <summary>
        /// THE ENTRY POINT #453's REVIEW FOUND MISSING, on its own. Recording is a COMPARE, so re-recording the
        /// same set at the same offset marks the slot clean and could never express "this slot is bound and
        /// nevertheless owes a full activation". <see cref="D3D11BindFlush.Raise"/> is what says it, and a slot
        /// the record has never seen is ignored rather than grown into, so an unbind cannot allocate.
        /// </summary>
        [Fact]
        public void Raise_MarksASlotFullFromOutsideARecord_AndIgnoresASlotTheRecordNeverSaw()
        {
            var harness = new D3D11BindFixtures.Harness();
            using D3D11ResourceLayout layout = D3D11BindFixtures.ShadowLayout();
            using D3D11ResourceSet set = D3D11BindFixtures.ShadowSet(layout);
            D3D11NativeTraceEmitter emitter = harness.Emitter;

            emitter.SetPipeline(D3D11BindFixtures.Pipeline(layout));
            emitter.SetGraphicsResourceSet(0, set, 0);
            emitter.Draw(3, 1, 0, 0);
            Assert.Equal(D3D11SlotDirty.Clean, harness.Binds.GraphicsDirty(0));

            harness.Binds.Raise(D3D11PipelineArm.Graphics, 0);
            Assert.Equal(D3D11SlotDirty.Full, harness.Binds.GraphicsDirty(0));

            int capacity = harness.Binds.RecordedSlotCapacity;
            harness.Binds.Raise(D3D11PipelineArm.Compute, 4096);
            Assert.Equal(capacity, harness.Binds.RecordedSlotCapacity);
        }

        // ---- Decision C3: rule 2 is honoured as written, with no barrier member ------------------------------

        /// <summary>
        /// DECISION C3, MADE MECHANICAL. Rule 2 (a dispatch reading an earlier dispatch's writes is separated by
        /// <c>End</c> plus <c>Submit</c> plus <c>WaitForIdle</c>) is a CROSS-BACKEND contract, stated on
        /// <c>IGpuCommandList</c> itself and honoured as written, so this backend adds no barrier of its own. The
        /// assertion is that neither the public seam nor the internal emitter seam grew one: an emitter member
        /// would be a D3D11-only ordering primitive that Vulkan and Metal do not have, which is the divergence
        /// #421's "zero renderer changes by construction" scope exists to prevent.
        /// <para>
        /// The automatic-hazard capability that would let a consumer skip the drain on a hazard-tracked backend is
        /// filed as its own follow-up (F1 in the design doc), because it needs a seam change AND a renderer change.
        /// </para>
        /// </summary>
        [Fact]
        public void NeitherSeamCarriesABarrierMember()
        {
            string[] forbidden = { "barrier", "hazard", "transition", "memorybarrier" };

            foreach (Type seam in new[] { typeof(ID3D11Emitter), typeof(IGpuCommandList) })
            {
                foreach (string name in seam.GetMembers().Select(m => m.Name.ToLowerInvariant()))
                {
                    Assert.DoesNotContain(name, forbidden);
                }
            }
        }

        // ---- Decision C4 and the copies: what the emitter issues ---------------------------------------------

        /// <summary>
        /// DECISION C4: a resolve is ONE <c>ResolveSubresource</c> at subresource 0 on both sides, which is the
        /// whole of what the seam can express. <c>ResolveTexture</c> takes two bare textures with no mip, no layer
        /// and no region, so there is nothing else to name, and the eager view policy leans on exactly that when
        /// it caps a texture at four views.
        /// </summary>
        [Fact]
        public void AResolve_IsOneResolveSubresourceAndNothingElse()
        {
            var harness = new D3D11BindFixtures.Harness();
            FakeTexture multisampled = new(8, 8, 1, 4, GpuPixelFormat.R8G8B8A8UNorm);
            FakeTexture single = D3D11BindFixtures.Texture();
            harness.Log.Reset();

            harness.Emitter.ResolveTexture(multisampled, single);

            Assert.Equal(
                new[] { $"ResolveSubresource({harness.Log.Id(multisampled)},{harness.Log.Id(single)})" },
                harness.Log.Trace);
        }

        /// <summary>Mip generation goes through the full-chain shader resource view the declared usage earned the
        /// texture at creation, so it is one <c>GenerateMips</c> and no view is built on the command path
        /// (decision X1).</summary>
        [Fact]
        public void MipGeneration_IsOneGenerateMipsThroughTheEagerView()
        {
            var harness = new D3D11BindFixtures.Harness();
            FakeTexture texture = D3D11BindFixtures.Texture();
            harness.Log.Reset();

            harness.Emitter.GenerateMipmaps(texture);

            Assert.Equal(new[] { $"GenerateMips({harness.Log.Id(texture)})" }, harness.Log.Trace);
        }

        /// <summary>A whole-texture copy is one <c>CopyResource</c>, every mip and every layer, and a buffer copy
        /// is the partial-region form both offsets make it.</summary>
        [Fact]
        public void TheCopies_AreTheRegionFormsTheSeamAsksFor()
        {
            var harness = new D3D11BindFixtures.Harness();
            FakeTexture source = D3D11BindFixtures.Texture();
            FakeTexture destination = D3D11BindFixtures.Texture();
            var from = new FakeBuffer(256);
            var to = new FakeBuffer(256);
            harness.Log.Reset();

            harness.Emitter.CopyTexture(source, destination);
            harness.Emitter.CopyBuffer(from, 64, to, 128, 32);

            Assert.Equal(
                new[]
                {
                    $"CopyResource({harness.Log.Id(source)},{harness.Log.Id(destination)})",
                    $"CopySubresourceRegion({harness.Log.Id(from)},64,{harness.Log.Id(to)},128,32)",
                },
                harness.Log.Trace);
        }

        /// <summary>
        /// THE SHORT <c>CopyTextureSubresource</c> OVERLOAD ARRIVES AT THE EMITTER WITH A DESTINATION MIP AND
        /// LAYER OF ZERO, which is where the seam's two overloads become one op. Driven through the RECORDER
        /// rather than the emitter, because the collapse happens there and an emitter-level test would be asserting
        /// its own arguments.
        /// </summary>
        [Fact]
        public void TheShortSubresourceCopy_LandsAtDestinationMipZeroLayerZero()
        {
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            using var recorder = new D3D11CommandRecorder<D3D11NativeTraceEmitter>(emitter);
            FakeTexture source = D3D11BindFixtures.Texture();
            FakeTexture destination = D3D11BindFixtures.Texture();

            recorder.Begin();
            log.Reset();
            recorder.CopyTextureSubresource(source, 2, 1, destination, 4, 4);

            Assert.Equal(
                new[]
                {
                    $"CopySubresourceRegion({log.Id(source)},2,1,{log.Id(destination)},0,0,4,4)",
                },
                log.Trace);
        }

        // ---- Decision C2: verified where it lives, which is the view policy ----------------------------------

        /// <summary>
        /// DECISION C2, CITED RATHER THAN REBUILT. The RAW byte-address treatment of structured buffers is created
        /// by the resource row in <see cref="D3D11ViewPolicy.ForBuffer"/> and <c>D3D11Buffer</c>
        /// (<c>R32_Typeless</c> plus the raw view flag, counted in 4-byte elements, over a
        /// <c>BufferAllowRawViews</c> resource). This asserts the policy the compute bind path rides on: both
        /// structured kinds carry raw views, a read-write one carries BOTH so a read-write storage block is still
        /// readable, and the stride stays advisory. Keeping it identical to the incumbent is why the ocean's
        /// existing kernels work, because SPIRV-Cross emits a GLSL storage block as a <c>ByteAddressBuffer</c> and
        /// never as a <c>StructuredBuffer&lt;T&gt;</c>.
        /// </summary>
        [Fact]
        public void StructuredBuffers_KeepTheRawByteAddressViews()
        {
            D3D11BufferViewPlan readOnly = D3D11ViewPolicy.ForBuffer(GpuBufferUsage.StructuredBufferReadOnly);
            D3D11BufferViewPlan readWrite = D3D11ViewPolicy.ForBuffer(GpuBufferUsage.StructuredBufferReadWrite);

            Assert.True(readOnly.RawViews);
            Assert.True(readOnly.ShaderResource);
            Assert.False(readOnly.UnorderedAccess);

            Assert.True(readWrite.RawViews);
            Assert.True(readWrite.ShaderResource);
            Assert.True(readWrite.UnorderedAccess);
        }

        // ---- The compute pipeline's own layout array ---------------------------------------------------------

        /// <summary>
        /// BOTH PIPELINE TYPES FLATTEN THE SAME LAYOUT ARRAY THROUGH ONE CHECK, which is what makes the compute
        /// pipeline's refusal testable at all: both constructors are Windows-only, so a guard inside either one is
        /// verified by the WARP leg and by nothing else. The message names WHICH kind of pipeline refused, because
        /// a consumer holding a compute layout and a graphics one wants to know which array it got wrong.
        /// </summary>
        [Fact]
        public void APipelinesLayoutArray_RefusesAForeignLayoutByNameAndSaysWhichKind()
        {
            using D3D11ResourceLayout mine = D3D11BindFixtures.ShadowLayout();
            var foreign = new FakeResourceLayout();

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => D3D11ResourceLayout.RequireAll(new IGpuResourceLayout[] { mine, foreign }, "compute"));

            Assert.Contains("Resource layout 1", ex.Message, StringComparison.Ordinal);
            Assert.Contains("compute pipeline", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>A pipeline that declares no layouts binds no sets, so an empty or absent array is not an error
        /// here. The mismatch a caller actually made in that case is a set bound at a slot the array does not
        /// have, which the register scheme names at the flush in those terms.</summary>
        [Fact]
        public void APipelineWithNoLayouts_IsNotAnError()
        {
            Assert.Empty(D3D11ResourceLayout.RequireAll(null, "compute"));
            Assert.Empty(D3D11ResourceLayout.RequireAll(Array.Empty<IGpuResourceLayout>(), "graphics"));
        }

        // ---- Fixtures ----------------------------------------------------------------------------------------

        sealed class FakeResourceLayout : IGpuResourceLayout
        {
            public void Dispose()
            {
            }
        }

        static GpuResourceLayoutElement Storage(string name)
            => new(name, GpuResourceKind.TextureReadWrite, GpuShaderStages.Compute);

        static D3D11ResourceLayout StorageLayout(params string[] names)
            => D3D11BindFixtures.Layout((names.Length == 0 ? new[] { "Dst" } : names).Select(Storage).ToArray());

        static D3D11ResourceLayout SampledLayout(params string[] names)
            => D3D11BindFixtures.Layout((names.Length == 0 ? new[] { "Src" } : names)
                .Select(n => D3D11BindFixtures.T(n, GpuShaderStages.Fragment)).ToArray());
    }
}
