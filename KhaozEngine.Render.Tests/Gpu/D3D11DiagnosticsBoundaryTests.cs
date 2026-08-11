using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE CLAIM DECISION P1 RESTS ON, checked for everything row 16 added: the capability assembly, the adapter
    /// selection policy, both halves of the <c>KE_D3D11_DEBUG</c> lever, the info-queue pump with its rate limit,
    /// and the device-loss latch. The package targets <c>net10.0</c> rather than <c>net10.0-windows</c>, so it is
    /// referenced unconditionally by every consumer and by this test project, and the ONLY thing keeping the
    /// Direct3D interop off the load path on macOS and Linux is that every body naming a Vortice type is
    /// <c>NoInlining</c> behind <see cref="KhaozEngineD3D11.IsPlatformSupported"/>.
    /// <para>
    /// A failure here is not a style point. The JIT resolves a method's types when it compiles that method, so an
    /// inlined or unguarded body means a macOS or Linux run loads a Windows-only native binding, and what the user
    /// sees is a startup crash naming an assembly they never asked for.
    /// </para>
    /// <para>
    /// THE TWO CONSTANTS ARE THE INTERESTING CASE HERE. <see cref="D3D11DebugLayer.CreateDeviceDebug"/> is taken
    /// FROM a Vortice enum, and it stays off the load path only because it is a <c>const uint</c> compile-time
    /// constant expression the compiler folds to a literal. The DXGI result codes in
    /// <see cref="D3D11DeviceLossCodes"/> could NOT be taken that way, because SharpGen exposes them as
    /// <c>static readonly</c> values rather than constants, and reading one would emit a field access against a
    /// Vortice type. Both readings are exercised below, so a future edit that turns either into a real reference
    /// fails here rather than on a user's machine.
    /// </para>
    ///
    /// <para><b>DO NOT ADD A REFLECTION SCAN HERE</b>, for the reason recorded on <see cref="D3D11InteropLoad"/>,
    /// which also carries the assertion itself. Like its siblings, this asserts a PROCESS-WIDE fact, so it holds
    /// only while nothing else in the suite loads the interop.</para>
    /// </summary>
    public sealed class D3D11DiagnosticsBoundaryTests
    {
        /// <summary>
        /// The whole of row 16's engine-facing surface, driven end to end off Windows: assemble a capability set
        /// from probed inputs, parse and resolve an adapter request, read both halves of the debug lever, pump a
        /// fake info queue through the rate limit, and latch a device loss. None of that may put Vortice in the
        /// process.
        /// </summary>
        [Fact]
        public void OffWindows_TheWholeDiagnosticsSurfaceRunsWithoutLoadingTheDirect3DInterop()
        {
            if (KhaozEngineD3D11.IsPlatformSupported) return;   // on Windows it loads, by design

            // Capabilities (G1).
            _ = D3D11CapabilityRead.Assemble(
                D3D11CapabilityRead.TrimAdapterName("WARP\0\0"),
                D3D11CapabilityRead.MinOverFormats(
                    D3D11CapabilityRead.HighestSupportedSampleCount(c => c <= 8 ? 1 : 0), 8, 8),
                supportsShadowMaps: true,
                supportsCompletionFences: true);
            _ = D3D11CapabilityRead.UnsupportedSampleCountMessage(16, 8);

            // Adapter selection (G2).
            var adapters = new List<D3D11AdapterInfo> { new("WARP", isSoftware: true) };
            D3D11AdapterRequest request = D3D11AdapterSelection.Parse("warp");
            D3D11AdapterChoice choice = D3D11AdapterSelection.Choose(request, adapters, out _);
            _ = D3D11AdapterSelection.Describe(choice, adapters);
            _ = D3D11AdapterSelection.IsSoftwareChoice(choice, adapters);
            _ = D3D11AdapterSelection.FromEnvironment();

            // The debug lever (G4), including the folded Vortice constant.
            _ = D3D11DebugLayer.FromEnvironment(out _);
            _ = D3D11DebugLayer.CreateDeviceDebug;
            _ = D3D11DebugLayer.ShouldRetryWithoutDebugLayer(
                D3D11DebugLayer.CreateDeviceDebug, D3D11DebugLayer.SdkComponentMissing);
            _ = D3D11DebugLayer.ActiveDescription;
            _ = D3D11DebugLayer.UnavailableWarning();

            // The info-queue pump and its rate limit (G4).
            var queue = new FakeD3D11InfoQueue();
            queue.Add(D3D11InfoSeverity.Corruption, 1, "off-windows");
            using (var pump = new D3D11InfoQueuePump(queue, new D3D11InfoQueueRateLimit(), new RecordingLogger()))
            {
                pump.Pump();
                _ = pump.Suppressed;
            }

            // The device-loss latch (G3), including the hand-written DXGI codes.
            var liveness = new DeviceLiveness();
            var latch = new D3D11DeviceLossLatch(liveness, new AlwaysHung(), new RecordingLogger());
            latch.Check(D3D11DeviceLossCodes.DeviceRemoved, "off-windows present");
            _ = latch.HeaderValue;
            _ = D3D11DeviceLossCodes.Describe(latch.RemovedReason);
            Assert.True(liveness.IsDead);

            D3D11InteropLoad.AssertNotLoaded();
        }

        sealed class AlwaysHung : ID3D11RemovedReason
        {
            public int GetDeviceRemovedReason() => D3D11DeviceLossCodes.DeviceHung;
        }
    }
}
