using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Render3D.Rendering;
using Vortice.Direct3D11;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION T3: the WARP <c>[GpuFact]</c> that guards the device-free native-call harness against drifting
    /// from the REAL replay path, by driving the shipped <see cref="D3D11NativeEmitter"/> on a live device and
    /// reading the answer back out of the <c>ID3D11DeviceContext</c> with the <c>Get*</c> counterparts.
    ///
    /// <para><b>WHY THERE IS NO CALL-SEQUENCE COMPARISON HERE, and this is the finding rather than an omission.</b>
    /// The obvious shape for a parity test is "replay one stream through both emitters and diff the two
    /// sequences", and against this backend's structure that assertion is VACUOUS. The real emitter and
    /// <see cref="D3D11NativeTraceEmitter"/> do not each implement the schedule: the dirty tracking, the slot
    /// order, the pipeline-switch drain, the register arithmetic and the array batching all live in
    /// <see cref="D3D11DeviceState"/>, <see cref="D3D11BindFlush"/> and <see cref="D3D11SetActivation"/>, which
    /// both emitters use UNCHANGED through one generic <see cref="ID3D11BindSink"/> constraint, and even the
    /// stage-to-method-name mapping is the single <see cref="D3D11NativeCallName"/> both ask. Diffing the two
    /// call sequences therefore diffs shared code against itself: it can only go red if the shared code is
    /// non-deterministic, and it stays green through every defect this test is meant to catch. So the SEMANTIC
    /// checks below ARE the drift guard, and the residue they close is precisely the residue
    /// <see cref="D3D11NativeEmitter"/> and <c>D3D11NativeEmitter.Binds</c> name in their own remarks: whether
    /// the arm that resolved to <c>PSSetSamplers</c> really calls <c>PSSetSamplers</c>, and what the Vortice
    /// array overload does with the count it was handed.</para>
    ///
    /// <para><b>THE TWO RECORDED FIRST-CHECKS</b>, from the #454 review and carried on
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/460:</para>
    /// <list type="number">
    ///   <item><description><b>Vortice 2.3.0 array-overload count semantics.</b> Every array call this backend
    ///   makes passes an explicit count alongside a SCRATCH array that is usually longer than it
    ///   (<see cref="D3D11EmitterContext"/> grows geometrically to the widest bind the process has ever seen and
    ///   reuses it). The shipped Vortice XML does not settle whether the generated overloads marshal that count
    ///   or <c>array.Length</c>. If they marshal the length, every bind in the engine silently writes the stale
    ///   trailing slots, which on a real frame means the previous pass's textures staying bound past the set that
    ///   replaced them. Settled empirically below by POISONING the trailing slots with a live resource and
    ///   reading the device back, so length-marshalling is observable rather than an all-null result that proves
    ///   nothing.</description></item>
    ///   <item><description><b><c>OMSetRenderTargets</c> with a count of zero over a non-empty array,</b> which
    ///   is the depth-only shadow pass's exact shape: <see cref="D3D11NativeEmitter.SetFramebuffer"/> asks
    ///   <see cref="D3D11EmitterContext.RenderTargets"/> for a scratch of length zero, gets the full-width array
    ///   back, and passes zero. A colour target is bound first so "no colour target is bound afterwards" is a
    ///   thing that had to HAPPEN rather than a context nobody ever wrote to.</description></item>
    /// </list>
    ///
    /// <para><b>NOT NAMED "Golden", ON PURPOSE.</b> <c>cross-platform-gpu.yml</c> selects the push path with
    /// <c>--filter FullyQualifiedName~Golden</c>, and this must ride the full-suite schedule and dispatch runs
    /// instead, exactly like <c>D3D11NativeCallBudgetTests</c>. Do not rename it into the filter.</para>
    ///
    /// <para><b>DORMANT, NEVER SKIPPED, ON A CAPABLE WINDOWS BOX.</b> Two early returns, both facts about the
    /// machine: not Windows, or a Windows box whose device fails the package's own capability probe
    /// (<c>ConstantBufferOffsetting</c> and <c>MapNoOverwriteOnDynamicConstantBuffer</c>). Anywhere else it runs
    /// or it fails. The <c>[GpuFact]</c> gate on <c>KE_GPU_TESTS</c> is the separate, ordinary one.</para>
    ///
    /// <para><b>THE VORTICE BODIES ARE ALL <c>NoInlining</c> BEHIND THE PLATFORM GUARD</b>, which is decision P1's
    /// load-path rule applying to a test file for the same reason it applies to the package: the JIT resolves a
    /// method's types when it compiles that method, and this assembly's device-free D3D11 tests assert
    /// process-wide that the Direct3D interop was never loaded (<see cref="D3D11InteropLoad"/>). A Vortice type
    /// named in a body the macOS run compiles would take all of them down at once.</para>
    ///
    /// <para><b>EVERY DEVICE HERE COMES THROUGH <see cref="GpuDeviceContext.CreateHeadless(GpuBackendKind)"/>,</b>
    /// naming the native kind, rather than out of <c>GpuBackendProviders.Require(...).CreateHeadless()</c>. The
    /// provider call reaches around the process-wide creation gate that <see cref="GpuDeviceContext"/> owns, and
    /// every provider is written on the promise that the engine serializes creation for it, so a device made
    /// outside the gate races every device made through it. These tests bring a second device up beside the
    /// suite's own, which is precisely the shape that promise is about. The context also owns disposal, so the
    /// <c>using</c> on it replaces the one that was on the raw device.</para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class D3D11NativeCallParityGpuTests
    {
        // The scratch is grown to this, every bind below passes a count well under it, and the gap is where the
        // poison lives. Sixteen because it is past the eight D3D11EmitterContext starts at, so the growth path is
        // exercised too rather than only the initial allocation.
        const int ScratchWidth = 16;

        // What every bind here actually binds. One register, so a length-marshalling overload would be writing
        // fifteen slots it was never asked to touch.
        const int BoundCount = 1;

        // WHAT THE READ-BACK MAY ASK FOR, per register file, and why it is not the poison width.
        //
        // The SET side stays sixteen wide at every arm below and that is legal everywhere it appears: the emitter
        // passes its OWN count (one here, zero for the depth-only render-target bind) beside a scratch array that
        // is longer, which is exactly the shape under test, so no Set here is ever handed a count past a limit. A
        // call that reaches the runtime carrying sixteen IS the defect, not the test's own doing.
        //
        // A Get* has no such freedom. Reading past a register file's documented slot count is undefined: a runtime
        // that writes nothing would read as a clean pass on a correct emitter, and the entries past the limit
        // would be asserting C# array zero-init rather than device state either way. So each read is clamped to
        // its own file's limit and the trailing-null assertion runs to the clamp.
        const int ConstantBufferSlots = 14;   // D3D11_COMMONSHADER_CONSTANT_BUFFER_API_SLOT_COUNT
        const int RenderTargetSlots = 8;      // D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT

        // D3D11_PS_CS_UAV_REGISTER_COUNT, the feature level 11.0 count. Level 11.1 raises the compute stage to
        // D3D11_1_UAV_SLOT_COUNT (64), so eight is the value that is legal on either one.
        const int UnorderedAccessSlots = 8;

        const string NotWindows =
            "dormant: not Windows, so there is no native Direct3D 11 device to read state back off.";

        const string NotCapable =
            "dormant: this Windows machine cannot run the native Direct3D 11 backend (the feature probe refused "
            + "it), so there is no device to settle the call semantics on.";

        readonly ITestOutputHelper _out;

        public D3D11NativeCallParityGpuTests(ITestOutputHelper o) => _out = o;

        /// <summary>
        /// FIRST-CHECK 1: the generated array overloads marshal the COUNT, not the array length. Driven through
        /// the five array calls the real emitter makes (the <c>b</c>, <c>t</c>, <c>s</c> and <c>u</c> bind arms
        /// and the batched vertex-stream flush), each one over a scratch array poisoned past the count with a
        /// LIVE resource of the right kind, so a length-marshalling overload leaves that resource visible in the
        /// trailing slots instead of leaving them null.
        /// <para>
        /// Both halves are asserted at every arm. That the bound slots ARE bound is not a formality: a
        /// length-marshalled call carrying an unusable trailing entry would be rejected wholesale by the runtime
        /// and bind nothing at all, which would look exactly like a clean count-marshalled result if only the
        /// trailing slots were checked.
        /// </para>
        /// </summary>
        [GpuFact]
        public void TheArrayOverloadsBindTheCountRatherThanTheScratchLength()
        {
            if (!KhaozEngineD3D11.IsPlatformSupported)
            {
                _out.WriteLine(NotWindows);
                return;
            }

            if (!GpuBackendSelector.IsBackendSupported(GpuBackendKind.Direct3D11Native))
            {
                _out.WriteLine(NotCapable);
                return;
            }

            CountSemanticsWindows(_out);
        }

        /// <summary>
        /// FIRST-CHECK 2: the depth-only shadow-pass bind. A framebuffer with a depth attachment and no colour
        /// attachments reaches <c>OMSetRenderTargets</c> as a count of zero over the full-width render-target
        /// scratch, and what must come out of it is every colour target UNBOUND and the depth-stencil view bound.
        /// <para>
        /// The order is the assertion. A colour framebuffer is bound first and checked, then the scratch is
        /// poisoned across its whole width, then the depth-only framebuffer is bound. Without that first bind the
        /// closing "no colour target" would be true of a context nothing had ever written to, and without the
        /// poison it would be true of an array that happened to hold nulls.
        /// </para>
        /// </summary>
        [GpuFact]
        public void ADepthOnlyPassBindsNoColourTargetThroughANonEmptyScratch()
        {
            if (!KhaozEngineD3D11.IsPlatformSupported)
            {
                _out.WriteLine(NotWindows);
                return;
            }

            if (!GpuBackendSelector.IsBackendSupported(GpuBackendKind.Direct3D11Native))
            {
                _out.WriteLine(NotCapable);
                return;
            }

            DepthOnlyTargetsWindows(_out);
        }

        /// <summary>
        /// THE ONE SEQUENCE-FREE PARITY FACT WORTH ASSERTING, and the residue the class remarks describe: the arm
        /// a resolved call name selects reaches the STAGE it named. A device-free budget can prove that
        /// <see cref="D3D11NativeCallName"/> answered <c>PSSetSamplers</c> for the fragment stage, and it cannot
        /// prove that the switch in <c>D3D11NativeEmitter.Binds</c> then called <c>PSSetSamplers</c> rather than
        /// <c>VSSetSamplers</c>. A mistake there compiles, runs, issues exactly the number of calls the budget
        /// expects, and renders a frame reading nothing.
        /// <para>
        /// So each arm is bound to ONE stage and read back at BOTH: the named stage holds the resource, and the
        /// other stage holds nothing. The <c>u</c> arm's "other" is the output merger rather than a sibling
        /// stage, because Direct3D 11 has no per-stage unordered-access setter outside compute and a wrong arm
        /// there could only have landed in <c>OMSetRenderTargetsAndUnorderedAccessViews</c>.
        /// </para>
        /// </summary>
        [GpuFact]
        public void EachBindArmReachesTheStageItsNameResolvedTo()
        {
            if (!KhaozEngineD3D11.IsPlatformSupported)
            {
                _out.WriteLine(NotWindows);
                return;
            }

            if (!GpuBackendSelector.IsBackendSupported(GpuBackendKind.Direct3D11Native))
            {
                _out.WriteLine(NotCapable);
                return;
            }

            StageArmsWindows(_out);
        }

        /// <summary>
        /// THE <c>ConstantCount</c> DEFECT, PINNED ON A DEVICE. A real 1008-byte uniform buffer
        /// (<see cref="ModelRenderer.UboBytes"/>, the shipped size the runtime was dropping) goes through the
        /// PRODUCTION path end to end: a real layout, a real resource set, and <see cref="D3D11SetActivation"/>
        /// computing the window itself, so the count that reaches <c>VSSetConstantBuffers1</c> is the one a frame
        /// would send and not one this test chose. Then the slot is read back and must hold the buffer.
        /// <para>
        /// UNDER THE PRE-<c>fba3117e</c> ARITHMETIC THIS FAILS, and that is the whole point of it. The count was
        /// rounded to 16 BYTES where <c>*SetConstantBuffers1</c> wants a multiple of 16 CONSTANTS, so 1008 bytes
        /// bound as 63 constants rather than 64. The setter returns void, the runtime drops the entire call, the
        /// slot stays empty behind the replay's <c>ClearState</c> and every shader reading that block reads zeros
        /// with nothing logged and nothing thrown. That is the bulk of the 113 failures on the first
        /// direct3d11-native leg (run 30955744945).
        /// </para>
        /// <para>
        /// THE DEVICE-FREE TESTS PIN THE SAME RULE ARITHMETICALLY and this one pins it EMPIRICALLY, which is a
        /// different claim.
        /// <c>D3D11ResourceModelTests.EveryShippedUniformWindow_BindsACountDirect3D11WillAccept</c> asserts that
        /// the engine's own idea of the rule holds over every shipped window, and it would stay green against a
        /// rule that was correctly applied and wrongly stated. What it cannot do is ask Direct3D 11, which is
        /// exactly what settled the count-versus-length question above and is what settles this: the bind either
        /// survives to the slot or it does not.
        /// </para>
        /// <para>
        /// The read is not vacuous in either direction. <c>Begin</c> issues decision R3's one <c>ClearState</c>
        /// before anything here binds, so a non-null slot afterwards was put there by this activation, and the
        /// buffer bound is the one asserted rather than merely something.
        /// </para>
        /// </summary>
        [GpuFact]
        public void AShippedConstantWindowSurvivesTheProductionArithmeticOnTheDevice()
        {
            if (!KhaozEngineD3D11.IsPlatformSupported)
            {
                _out.WriteLine(NotWindows);
                return;
            }

            if (!GpuBackendSelector.IsBackendSupported(GpuBackendKind.Direct3D11Native))
            {
                _out.WriteLine(NotCapable);
                return;
            }

            ShippedConstantWindowWindows(_out);
        }

        // ---- the Windows bodies -----------------------------------------------------------------------------

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        static void CountSemanticsWindows(ITestOutputHelper output)
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless(GpuBackendKind.Direct3D11Native);
            IGpuDevice device = gpu.GpuDevice;
            var backend = (D3D11GpuDevice)device;
            D3D11EmitterContext context = backend.EmitterContext;
            var emitter = new D3D11NativeEmitter(backend.State, context);

            // Decision R3's one ClearState, so everything observed below was bound by the calls below.
            emitter.Begin();

            using IGpuBuffer uniform = Uniform(device);
            using IGpuBuffer uniformPoison = Uniform(device);
            using IGpuBuffer stream = device.Factory.CreateBuffer(
                new GpuBufferDescription(1024, GpuBufferUsage.VertexBuffer));
            using IGpuBuffer streamPoison = device.Factory.CreateBuffer(
                new GpuBufferDescription(1024, GpuBufferUsage.VertexBuffer));
            using IGpuTexture texture = Sampled(device);
            using IGpuTexture texturePoison = Sampled(device);
            using IGpuBuffer storage = Storage(device);
            using IGpuBuffer storagePoison = Storage(device);

            // ---- the 'b' file: *SetConstantBuffers1 over three parallel scratch arrays -----------------------
            //
            // The windows are poisoned alongside the buffers, and both are VALID for the poison buffer, so a
            // length-marshalling call would be a legal bind that shows up in the read-back rather than an invalid
            // one the runtime drops (which would unbind everything and read as a pass).
            ID3D11Buffer?[] constantScratch = context.ConstantBuffers(ScratchWidth);
            var bufferPoison = (ID3D11Buffer)D3D11BindResolve.ViewOf(
                uniformPoison, D3D11RegisterFile.ConstantBuffer)!;
            for (int i = 0; i < ScratchWidth; i++)
            {
                constantScratch[i] = bufferPoison;
                context.FirstConstants[i] = 0;
                context.ConstantCounts[i] = 16;
            }

            ReadOnlySpan<D3D11ConstantBufferBind> binds = new[] { new D3D11ConstantBufferBind(uniform, 0, 16) };
            emitter.SetConstantBuffers(GpuShaderStages.Vertex, 0, binds);

            // Read CLAMPED to D3D11_COMMONSHADER_CONSTANT_BUFFER_API_SLOT_COUNT. The scratch behind the call is
            // sixteen wide and the constant-buffer file is only fourteen slots deep, so asking the device for
            // sixteen is a read past the API, and what it writes there is undefined.
            var readBuffers = new ID3D11Buffer[ConstantBufferSlots];
            context.Context.VSGetConstantBuffers(0, ConstantBufferSlots, readBuffers);
            AssertOnlyTheCountWasBound(readBuffers, "VSSetConstantBuffers1", output);

            // ---- the 't' file -------------------------------------------------------------------------------
            ID3D11ShaderResourceView?[] viewScratch = context.ShaderResources(ScratchWidth);
            var viewPoison = (ID3D11ShaderResourceView)D3D11BindResolve.ViewOf(
                texturePoison, D3D11RegisterFile.ShaderResource)!;
            for (int i = 0; i < ScratchWidth; i++) viewScratch[i] = viewPoison;

            ReadOnlySpan<IGpuBindableResource?> resources = new IGpuBindableResource?[] { texture };
            emitter.SetShaderResources(GpuShaderStages.Fragment, 0, resources);

            // The full poison width, and legal: sixteen of D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT's 128.
            var readViews = new ID3D11ShaderResourceView[ScratchWidth];
            context.Context.PSGetShaderResources(0, ScratchWidth, readViews);
            AssertOnlyTheCountWasBound(readViews, "PSSetShaderResources", output);

            // ---- the 's' file -------------------------------------------------------------------------------
            ID3D11SamplerState?[] samplerScratch = context.Samplers(ScratchWidth);
            var samplerPoison = (ID3D11SamplerState)D3D11BindResolve.ViewOf(
                device.LinearSampler, D3D11RegisterFile.Sampler)!;
            for (int i = 0; i < ScratchWidth; i++) samplerScratch[i] = samplerPoison;

            ReadOnlySpan<IGpuBindableResource?> samplers = new IGpuBindableResource?[] { device.PointSampler };
            emitter.SetSamplers(GpuShaderStages.Fragment, 0, samplers);

            // The full poison width, with no headroom at all: sixteen IS D3D11_COMMONSHADER_SAMPLER_SLOT_COUNT,
            // the limit itself rather than a number under it.
            var readSamplers = new ID3D11SamplerState[ScratchWidth];
            context.Context.PSGetSamplers(0, ScratchWidth, readSamplers);
            AssertOnlyTheCountWasBound(readSamplers, "PSSetSamplers", output);

            // ---- the 'u' file -------------------------------------------------------------------------------
            //
            // Compute is the only stage that reaches this arm, and by refusal rather than by omission: the name
            // resolution answers CSSetUnorderedAccessViews for compute and throws for every other stage (issue
            // #490), because a graphics-pipeline UAV goes through the output merger instead. Two shipped compute
            // layouts bind here, so it is live code with the same count-versus-length question as the other three.
            ID3D11UnorderedAccessView?[] uavScratch = context.UnorderedAccessViews(ScratchWidth);
            var uavPoison = (ID3D11UnorderedAccessView)D3D11BindResolve.ViewOf(
                storagePoison, D3D11RegisterFile.UnorderedAccess)!;
            for (int i = 0; i < ScratchWidth; i++) uavScratch[i] = uavPoison;

            ReadOnlySpan<IGpuBindableResource?> writes = new IGpuBindableResource?[] { storage };
            emitter.SetUnorderedAccessViews(GpuShaderStages.Compute, 0, writes);

            // Read CLAMPED to D3D11_PS_CS_UAV_REGISTER_COUNT, the feature level 11.0 count, which is the smaller
            // of the two feature levels this backend can find itself running on.
            var readUavs = new ID3D11UnorderedAccessView[UnorderedAccessSlots];
            context.Context.CSGetUnorderedAccessViews(0, UnorderedAccessSlots, readUavs);
            AssertOnlyTheCountWasBound(readUavs, "CSSetUnorderedAccessViews", output);

            // ---- the batched vertex flush -------------------------------------------------------------------
            //
            // The strides come from the pipeline in a real frame, so they are adopted here directly: without them
            // the flush skips a slot no input layout references and there would be no call to measure. This is
            // the ONE place the test reaches past the emitter surface it exists to check, and it is state the
            // emitter reads rather than a call it makes.
            backend.State.Vertices.AdoptStrides(new uint[] { 16u });

            ID3D11Buffer?[] streamScratch = context.VertexBuffers(ScratchWidth);
            var streamPoisonBuffer = (ID3D11Buffer)D3D11BindResolve.NativeBuffer(streamPoison);
            for (int i = 0; i < ScratchWidth; i++)
            {
                streamScratch[i] = streamPoisonBuffer;
                context.VertexStrides[i] = 16;
                context.VertexOffsets[i] = 0;
            }

            emitter.SetVertexBuffer(0, stream, 0);
            emitter.FlushVertexBuffers();

            // The full poison width, and legal: sixteen of D3D11_IA_VERTEX_INPUT_RESOURCE_SLOT_COUNT's 32.
            var readStreams = new ID3D11Buffer[ScratchWidth];
            context.Context.IAGetVertexBuffers(0, ScratchWidth, readStreams,
                new int[ScratchWidth], new int[ScratchWidth]);
            AssertOnlyTheCountWasBound(readStreams, "IASetVertexBuffers", output);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        static void DepthOnlyTargetsWindows(ITestOutputHelper output)
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless(GpuBackendKind.Direct3D11Native);
            IGpuDevice device = gpu.GpuDevice;
            var backend = (D3D11GpuDevice)device;
            D3D11EmitterContext context = backend.EmitterContext;
            var emitter = new D3D11NativeEmitter(backend.State, context);

            emitter.Begin();

            using IGpuTexture colour = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                64, 64, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
            using IGpuTexture litDepth = Depth(device);
            using IGpuTexture shadowDepth = Depth(device);
            using IGpuFramebuffer lit = device.Factory.CreateFramebuffer(litDepth, colour);
            using IGpuFramebuffer shadow = device.Factory.CreateFramebuffer(shadowDepth);

            // The colour pass first, so the depth-only bind below has something to take away.
            emitter.SetFramebuffer(lit);

            // Read CLAMPED to D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, here and at the depth-only read below. The
            // output merger has eight colour slots, so asking for the sixteen the scratch is wide reads past the
            // API and the eight entries past the limit would be asserting nothing but C# array zero-init.
            var afterLit = new ID3D11RenderTargetView[RenderTargetSlots];
            context.Context.OMGetRenderTargets(RenderTargetSlots, afterLit, out ID3D11DepthStencilView? litView);
            try
            {
                Assert.True(afterLit[0] is not null,
                    "The colour framebuffer bound no render target, so the depth-only assertion below would have "
                    + "been vacuously true of a context nothing ever wrote to.");
                Assert.True(litView is not null, "The lit framebuffer declares a depth attachment and bound none.");
            }
            finally
            {
                Release(afterLit);
                litView?.Dispose();
            }

            // Poison the WHOLE render-target scratch. SetFramebuffer asks for a scratch of length zero for a
            // depth-only pass, gets this array back unchanged, and passes zero as the count.
            ID3D11RenderTargetView?[] scratch = context.RenderTargets(ScratchWidth);
            var poison = (ID3D11RenderTargetView)D3D11BindResolve.RenderTargets(lit).RenderTargetAt(0);
            for (int i = 0; i < ScratchWidth; i++) scratch[i] = poison;

            emitter.SetFramebuffer(shadow);

            var afterShadow = new ID3D11RenderTargetView[RenderTargetSlots];
            context.Context.OMGetRenderTargets(RenderTargetSlots, afterShadow,
                out ID3D11DepthStencilView? shadowView);
            try
            {
                for (int i = 0; i < afterShadow.Length; i++)
                {
                    Assert.True(afterShadow[i] is null,
                        $"A depth-only framebuffer left a render target bound at slot {i}. OMSetRenderTargets was "
                        + "handed a count of zero over a scratch array that is not empty, so this is the array "
                        + "length reaching the runtime instead of the count, and every shadow pass in the engine "
                        + "is rendering colour into the previous pass's target.");
                }

                Assert.True(shadowView is not null,
                    "A depth-only framebuffer bound no depth-stencil view, so the shadow pass writes nowhere.");
                output.WriteLine($"depth-only bind: {RenderTargetSlots} colour slots read back null, depth bound.");
            }
            finally
            {
                Release(afterShadow);
                shadowView?.Dispose();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        static void StageArmsWindows(ITestOutputHelper output)
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless(GpuBackendKind.Direct3D11Native);
            IGpuDevice device = gpu.GpuDevice;
            var backend = (D3D11GpuDevice)device;
            D3D11EmitterContext context = backend.EmitterContext;
            var emitter = new D3D11NativeEmitter(backend.State, context);

            emitter.Begin();

            using IGpuBuffer uniform = Uniform(device);
            using IGpuTexture texture = Sampled(device);
            using IGpuBuffer storage = Storage(device);

            // b file, VERTEX only.
            ReadOnlySpan<D3D11ConstantBufferBind> binds = new[] { new D3D11ConstantBufferBind(uniform, 0, 16) };
            emitter.SetConstantBuffers(GpuShaderStages.Vertex, 0, binds);

            var namedBuffers = new ID3D11Buffer[1];
            var otherBuffers = new ID3D11Buffer[1];
            context.Context.VSGetConstantBuffers(0, 1, namedBuffers);
            context.Context.PSGetConstantBuffers(0, 1, otherBuffers);
            AssertBoundHereAndNotThere(namedBuffers, otherBuffers,
                "VSSetConstantBuffers1", "the pixel stage", output);

            // t file, FRAGMENT only.
            ReadOnlySpan<IGpuBindableResource?> resources = new IGpuBindableResource?[] { texture };
            emitter.SetShaderResources(GpuShaderStages.Fragment, 0, resources);

            var namedViews = new ID3D11ShaderResourceView[1];
            var otherViews = new ID3D11ShaderResourceView[1];
            context.Context.PSGetShaderResources(0, 1, namedViews);
            context.Context.VSGetShaderResources(0, 1, otherViews);
            AssertBoundHereAndNotThere(namedViews, otherViews,
                "PSSetShaderResources", "the vertex stage", output);

            // s file, FRAGMENT only.
            ReadOnlySpan<IGpuBindableResource?> samplers = new IGpuBindableResource?[] { device.PointSampler };
            emitter.SetSamplers(GpuShaderStages.Fragment, 0, samplers);

            var namedSamplers = new ID3D11SamplerState[1];
            var otherSamplers = new ID3D11SamplerState[1];
            context.Context.PSGetSamplers(0, 1, namedSamplers);
            context.Context.VSGetSamplers(0, 1, otherSamplers);
            AssertBoundHereAndNotThere(namedSamplers, otherSamplers,
                "PSSetSamplers", "the vertex stage", output);

            // u file, COMPUTE only, and the place it must NOT have reached is the graphics pipeline's own u file
            // rather than a sibling stage. Direct3D 11 has no per-stage unordered-access setter outside compute: a
            // pixel-stage UAV is bound through OMSetRenderTargetsAndUnorderedAccessViews alongside the render
            // targets, so the output merger is the only other place a wrong arm could have put this one.
            ReadOnlySpan<IGpuBindableResource?> writes = new IGpuBindableResource?[] { storage };
            emitter.SetUnorderedAccessViews(GpuShaderStages.Compute, 0, writes);

            var namedUavs = new ID3D11UnorderedAccessView[1];
            var graphicsUavs = new ID3D11UnorderedAccessView[1];
            var graphicsTargets = new ID3D11RenderTargetView[1];
            context.Context.CSGetUnorderedAccessViews(0, 1, namedUavs);
            context.Context.OMGetRenderTargetsAndUnorderedAccessViews(
                1, graphicsTargets, out ID3D11DepthStencilView? graphicsDepth, 0, 1, graphicsUavs);
            try
            {
                AssertBoundHereAndNotThere(namedUavs, graphicsUavs,
                    "CSSetUnorderedAccessViews", "the output merger's u file", output);
            }
            finally
            {
                // The render target and depth-stencil view come back AddRef'd from the same read, and this arm is
                // the only one whose counter-read hands back more than the slots it asked about.
                Release(graphicsTargets);
                graphicsDepth?.Dispose();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        static void ShippedConstantWindowWindows(ITestOutputHelper output)
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless(GpuBackendKind.Direct3D11Native);
            IGpuDevice device = gpu.GpuDevice;
            var backend = (D3D11GpuDevice)device;
            D3D11EmitterContext context = backend.EmitterContext;
            var emitter = new D3D11NativeEmitter(backend.State, context);

            emitter.Begin();

            // The shipped size, by its own constant. A literal here would keep passing on the day the frame UBO
            // grows into some other unaligned size, which is the case this exists to catch.
            using IGpuBuffer ubo = device.Factory.CreateBuffer(
                new GpuBufferDescription(ModelRenderer.UboBytes, GpuBufferUsage.UniformBuffer));

            // A real layout and a real set, so the window is RESOLVED the way a renderer's is (whole buffer, since
            // a bare IGpuBuffer is bound) rather than handed to the emitter pre-computed.
            using IGpuResourceLayout layout = device.Factory.CreateResourceLayout(
                new GpuResourceLayoutDescription(
                    new GpuResourceLayoutElement("Frame", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)));
            using IGpuResourceSet set = device.Factory.CreateResourceSet(
                new GpuResourceSetDescription(layout, ubo));

            // THE PRODUCTION ARITHMETIC, and the reason this goes through the activation rather than through a
            // hand-built D3D11ConstantBufferBind: D3D11SetActivation.ConstantBind is what reads the ring's frame
            // base and calls D3D11ConstantRange.ConstantCount, and that call is the defect's site. Base counts of
            // zero because this is set 0 of a one-layout pipeline, which is what puts the bind at b0. The R7
            // unset is off, so the span is one call and the read below has one thing to explain it.
            var activation = new D3D11SetActivation();
            activation.Activate(ref emitter, (D3D11ResourceSet)set, default, dynamicOnly: false,
                dynamicOffsetBytes: 0, unsetConstantBuffersBeforeSet: false, D3D11PipelineArm.Graphics, slot: 0);

            uint constants = D3D11ConstantRange.ConstantCount(ModelRenderer.UboBytes);
            var expected = (ID3D11Buffer)D3D11BindResolve.ViewOf(ubo, D3D11RegisterFile.ConstantBuffer)!;

            // Clamped to the constant-buffer file's own depth, for the reason the count-semantics read is.
            var read = new ID3D11Buffer[ConstantBufferSlots];
            context.Context.VSGetConstantBuffers(0, ConstantBufferSlots, read);
            try
            {
                Assert.True(read[0] is not null,
                    $"A {ModelRenderer.UboBytes}-byte uniform buffer bound as {constants} constants left b0 EMPTY. "
                    + "*SetConstantBuffers1 returns void and drops a call whose count is not a multiple of 16 "
                    + "constants, so this is the count arithmetic producing an illegal window and the runtime "
                    + "throwing the whole bind away. Nothing is logged and nothing throws: every shader reading "
                    + "this block reads zeros, which is how the model pass and splat terrain disappeared on the "
                    + "first WARP run of the native leg.");

                Assert.Equal(expected.NativePointer, read[0].NativePointer);

                output.WriteLine(
                    $"VSSetConstantBuffers1: {ModelRenderer.UboBytes} bytes bound as {constants} constants, b0 "
                    + "holds the buffer.");
            }
            finally
            {
                Release(read);
            }
        }

        // ---- assertions and fixtures ------------------------------------------------------------------------

        // Object-typed on purpose: the five scratch kinds are five unrelated Direct3D types, array covariance
        // hands every one of them over as object?[], and a helper that names none of them can live outside the
        // platform guard where the interop-load rule cannot be broken by it.
        //
        // What arrives is the READ WINDOW, not the scratch: its length is the register file's documented slot
        // count, which is narrower than the sixteen-wide poison wherever the file is shallower than that.
        static void AssertOnlyTheCountWasBound(object?[] slots, string call, ITestOutputHelper output)
        {
            try
            {
                for (int i = 0; i < BoundCount; i++)
                {
                    Assert.True(slots[i] is not null,
                        $"{call} bound nothing at slot {i}, which it was explicitly asked to bind. A generated "
                        + "overload marshalling the array LENGTH would carry the poisoned trailing entries into "
                        + "the same call, and a runtime that rejects the call drops every register in it, so a "
                        + "null here is the length-marshalling case failing rather than passing.");
                }

                for (int i = BoundCount; i < slots.Length; i++)
                {
                    Assert.True(slots[i] is null,
                        $"{call} was handed a count of {BoundCount} over a scratch array of {ScratchWidth}, and "
                        + $"slot {i} of the {slots.Length} read back came back holding the poison. Vortice is "
                        + "marshalling array.Length rather than the count, so every array bind this backend "
                        + "makes writes the stale tail of a reused scratch buffer. Every call site would have to "
                        + "pass an exactly sized array.");
                }

                output.WriteLine($"{call}: slot 0 bound, slots {BoundCount}..{slots.Length - 1} untouched.");
            }
            finally
            {
                Release(slots);
            }
        }

        // One arm, already read back at the stage its name chose and at a stage it did not. Object-typed for the
        // reason above, and taking the read RESULTS rather than two reader delegates so nothing here has to name
        // a Direct3D type or cast an array back to one.
        static void AssertBoundHereAndNotThere(object?[] named, object?[] elsewhere,
            string call, string other, ITestOutputHelper output)
        {
            try
            {
                Assert.True(named[0] is not null,
                    $"{call} bound nothing at the stage its own name resolved to.");
                Assert.True(elsewhere[0] is null,
                    $"{call} reached {other} as well as the one it names. The stage-to-method mapping is the one "
                    + "thing a device-free budget cannot check: the wrong arm issues exactly the number of calls "
                    + "the budget expects and renders a frame that reads nothing.");

                output.WriteLine($"{call}: bound at its named stage, not at {other}.");
            }
            finally
            {
                Release(named);
                Release(elsewhere);
            }
        }

        // Every Get* on the device context hands back an AddRef'd wrapper, so a test that reads state back and
        // walks away leaks a reference per slot and can hold a swapchain's backbuffer view past a resize.
        static void Release(object?[] slots)
        {
            foreach (object? slot in slots) (slot as IDisposable)?.Dispose();
        }

        static IGpuBuffer Uniform(IGpuDevice device)
            => device.Factory.CreateBuffer(new GpuBufferDescription(256, GpuBufferUsage.UniformBuffer));

        // A read-write structured buffer is the one buffer usage that earns an unordered-access view on this
        // backend (D3D11ViewPolicy.ForBuffer), and its full-range RAW view is what the 'u' arm binds.
        static IGpuBuffer Storage(IGpuDevice device)
            => device.Factory.CreateBuffer(
                new GpuBufferDescription(1024, GpuBufferUsage.StructuredBufferReadWrite));

        static IGpuTexture Sampled(IGpuDevice device)
            => device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                4, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));

        static IGpuTexture Depth(IGpuDevice device)
            => device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                64, 64, GpuPixelFormat.D32FloatS8UInt, GpuTextureUsage.DepthStencil));
    }
}
