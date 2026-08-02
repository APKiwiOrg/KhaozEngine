using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The ADOPTED-device half of <see cref="GpuDeviceContext"/>: a context built over a plain
    /// <see cref="IGpuDevice"/> the engine created itself, with no Veldrid device anywhere behind it. Device-free,
    /// so these run under a plain <c>dotnet test</c> on any OS.
    /// <para>
    /// What they pin is the property that made a native backend impossible before it: the context was the only
    /// creation path, it took a Veldrid <c>GraphicsDevice</c>, and its disposal cast the wrapper back to
    /// <c>VeldridGpuDevice</c>. Any other <see cref="IGpuDevice"/> would therefore have thrown an
    /// <see cref="InvalidCastException"/> at teardown even if something had managed to construct a context around
    /// it. See decision P3 and section 4.2 of <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c>.
    /// </para>
    /// <para>
    /// The Veldrid path's own liveness latch is covered by <c>DeviceDisposedLatchTests</c> against a real device.
    /// What is asserted HERE about that path is the one thing removing the cast made silently droppable: that the
    /// Veldrid wrapper still carries the hook at all.
    /// </para>
    /// </summary>
    public sealed class GpuDeviceContextAdoptionTests
    {
        static GpuBackendSelection Selection(GpuBackendKind kind = GpuBackendKind.Direct3D11)
            => new(kind, GpuBackendSource.UserPreference, null);

        public static TheoryData<GpuBackendKind> EveryBackendKind()
        {
            var data = new TheoryData<GpuBackendKind>();
            foreach (GpuBackendKind kind in Enum.GetValues<GpuBackendKind>()) data.Add(kind);
            return data;
        }

        [Fact]
        public void AdoptedDevice_IsHandedBackAsIs_WithItsSelection()
        {
            var device = new RecordingGpuDevice();
            using var ctx = new GpuDeviceContext(device, threadingCaps: null, threadingProbeFailure: null,
                Selection(), ownsDevice: true);

            Assert.Same(device, ctx.GpuDevice);
            Assert.Equal(GpuBackendKind.Direct3D11, ctx.Backend);
            Assert.Equal(GpuBackendSource.UserPreference, ctx.Selection.Source);
        }

        /// <summary>
        /// The device and the selection arrive independently here, unlike the Veldrid path where the wrapper is
        /// built FROM the selection, so the invariant that path gets by construction has to be enforced. A
        /// mismatched pair does not fail the run, it misattributes it: different readers downstream take the kind
        /// from different halves of the pair, so the golden image would be filed under one backend while the
        /// session header names the other.
        /// </summary>
        [Theory]
        [InlineData(GpuBackendKind.Direct3D11, GpuBackendKind.Vulkan)]
        [InlineData(GpuBackendKind.Vulkan, GpuBackendKind.Direct3D11)]
        [InlineData(GpuBackendKind.Metal, GpuBackendKind.OpenGL)]
        public void AdoptedDevice_MustAgreeWithTheSelectionAboutTheBackend(
            GpuBackendKind deviceBackend, GpuBackendKind selectionBackend)
        {
            var device = new RecordingGpuDevice(deviceBackend);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new GpuDeviceContext(device, threadingCaps: null, threadingProbeFailure: null,
                    Selection(selectionBackend), ownsDevice: true));

            Assert.Contains(deviceBackend.ToString(), ex.Message);
            Assert.Contains(selectionBackend.ToString(), ex.Message);
        }

        /// <summary>
        /// Every member, enumerated rather than listed, because "an adopted device is accepted" is true of every
        /// backend by construction and stays true of one appended later. (Contrast
        /// <c>GpuBackendProvidersTests.RequiresProvider_IsFalse_ForEveryBackendTheEngineBuildsItself</c>, which
        /// spells its members out on purpose: an appended kind is correctly NOT in that set, so enumerating there
        /// would turn a right answer into a red build.)
        /// </summary>
        [Theory]
        [MemberData(nameof(EveryBackendKind))]
        public void AdoptedDevice_IsAcceptedOnEveryBackend_WhenThePairAgrees(GpuBackendKind backend)
        {
            using var ctx = new GpuDeviceContext(new RecordingGpuDevice(backend), threadingCaps: null,
                threadingProbeFailure: null, Selection(backend), ownsDevice: true);

            Assert.Equal(backend, ctx.Backend);
            Assert.Equal(backend, ctx.GpuDevice.Backend);
        }

        /// <summary>
        /// The context's capabilities ARE the device's, not a second derivation that can drift from them. The two
        /// copies drifted once already (the device name and the sampler feature flags were populated on one and
        /// dropped on the other), and a device the engine built itself has no shared reader to point a second
        /// derivation at, so reading the device is the only form of that rule that survives the move off Veldrid.
        /// </summary>
        [Fact]
        public void Capabilities_AreReadFromTheDevice_FieldForField()
        {
            var device = new RecordingGpuDevice();
            using var ctx = new GpuDeviceContext(device, threadingCaps: null, threadingProbeFailure: null,
                Selection(), ownsDevice: true);

            GpuCapabilities expected = device.Capabilities;
            GpuCapabilities actual = ctx.Capabilities;

            Assert.Equal(expected.ClipSpaceYInverted, actual.ClipSpaceYInverted);
            Assert.Equal(expected.DepthRangeZeroToOne, actual.DepthRangeZeroToOne);
            Assert.Equal(expected.DeviceName, actual.DeviceName);
            Assert.Equal(expected.SamplerAnisotropy, actual.SamplerAnisotropy);
            Assert.Equal(expected.SamplerLodBias, actual.SamplerLodBias);
            Assert.Equal(expected.MaxMsaaSampleCount, actual.MaxMsaaSampleCount);
            Assert.Equal(expected.SupportsShadowMaps, actual.SupportsShadowMaps);
            Assert.Equal(expected.SupportsCompute, actual.SupportsCompute);
            Assert.Equal(expected.SupportsCompletionFences, actual.SupportsCompletionFences);

            // The adapter line a bug report gets read off the same single source.
            Assert.Equal(expected.DeviceName, ctx.AdapterDescription);
        }

        /// <summary>
        /// A natively created device has no Veldrid device for the threading probe to read a pointer off, so its
        /// caps arrive already probed (through the probe's raw-pointer entry) and the context carries them
        /// through untouched, including the null that means "no answer".
        /// </summary>
        [Fact]
        public void ThreadingCaps_AreWhateverTheCallerProbed()
        {
            var caps = new GpuThreadingCaps(DriverCommandLists: false, DriverConcurrentCreates: true);
            using var probed = new GpuDeviceContext(new RecordingGpuDevice(), caps, threadingProbeFailure: null,
                Selection(), ownsDevice: true);
            using var unprobed = new GpuDeviceContext(new RecordingGpuDevice(), null, threadingProbeFailure: null,
                Selection(), ownsDevice: true);

            Assert.Equal(caps, probed.ThreadingCaps);
            Assert.Null(unprobed.ThreadingCaps);
        }

        /// <summary>
        /// A provider whose raw-pointer threading probe FAULTED has a channel for the reason, and it reaches the
        /// warn decision the threading line is logged from. Without it, null caps plus no reason renders as the
        /// plain "unknown" INFO line, which is what an ordinary non-Direct3D11 session looks like, and the WARN
        /// that says a slow-session report cannot rule out an emulating driver never fires. That warning is the
        /// diagnostic the move off Veldrid is meant to keep, on exactly the backend it was written for.
        /// </summary>
        [Fact]
        public void ThreadingProbeFailure_IsCarried_AndSelectsTheWarning()
        {
            const string failure = "the Direct3D11 threading query threw SharpGenException: HRESULT 0x80004005";
            using var ctx = new GpuDeviceContext(new RecordingGpuDevice(), threadingCaps: null, failure,
                Selection(), ownsDevice: true);

            Assert.Equal(failure, ctx.ThreadingProbeFailure);

            string? warning = GpuThreadingDiagnostics.WarningFor(ctx.ThreadingCaps, ctx.ThreadingProbeFailure);
            Assert.NotNull(warning);
            Assert.Contains("Could not read the Direct3D11 driver threading capabilities", warning);
            Assert.Contains(failure, warning);
        }

        /// <summary>
        /// The other half: a probe that ANSWERED, or one that was never applicable, carries no reason, so the
        /// adopted path stays silent exactly where the Veldrid path does. A warning that fires on a healthy
        /// session is worse than none, because it trains the reader to skip the one that matters.
        /// </summary>
        [Fact]
        public void NoThreadingProbeFailure_MeansNoWarning()
        {
            using var ctx = new GpuDeviceContext(new RecordingGpuDevice(), new GpuThreadingCaps(true, true),
                threadingProbeFailure: null, Selection(), ownsDevice: true);

            Assert.Null(ctx.ThreadingProbeFailure);
            Assert.Null(GpuThreadingDiagnostics.WarningFor(ctx.ThreadingCaps, ctx.ThreadingProbeFailure));
        }

        /// <summary>
        /// The teardown ORDER is the point, not just that both calls happen: the latch has to land before the
        /// device goes away, or a resource wrapper disposed after the device calls into freed driver objects,
        /// which is the crash the latch exists to make impossible.
        /// </summary>
        [Fact]
        public void Dispose_LatchesTheDeviceBeforeDisposingIt()
        {
            var device = new RecordingGpuDevice();
            var ctx = new GpuDeviceContext(device, threadingCaps: null, threadingProbeFailure: null,
                Selection(), ownsDevice: true);

            ctx.Dispose();

            Assert.Equal(new[] { nameof(IGpuDeviceLifecycle.MarkDeviceDisposed), nameof(IDisposable.Dispose) },
                device.Calls);
        }

        [Fact]
        public void Dispose_TouchesNothingWhenTheContextDoesNotOwnTheDevice()
        {
            var device = new RecordingGpuDevice();
            var ctx = new GpuDeviceContext(device, threadingCaps: null, threadingProbeFailure: null,
                Selection(), ownsDevice: false);

            ctx.Dispose();

            Assert.Empty(device.Calls);
        }

        /// <summary>
        /// The regression itself. <see cref="FakeGpuDevice"/> is not the Veldrid wrapper and does not implement
        /// the lifecycle hook, so under the old <c>((VeldridGpuDevice)GpuDevice).MarkDeviceDisposed()</c> disposal
        /// this throws <see cref="InvalidCastException"/>. The hook being optional is deliberate: a device with
        /// nothing that can outlive it has no latch to flip.
        /// </summary>
        [Fact]
        public void Dispose_DoesNotRequireTheDeviceToBeTheVeldridWrapper()
        {
            var device = new FakeGpuDevice(GpuBackendKind.Direct3D11);
            var ctx = new GpuDeviceContext(device, threadingCaps: null, threadingProbeFailure: null,
                Selection(), ownsDevice: true);

            ctx.Dispose();
        }

        /// <summary>
        /// The Veldrid path's half of the same change. Removing the cast removed the compile error that used to
        /// keep <c>MarkDeviceDisposed</c> reachable, so dropping the interface from the wrapper would now silently
        /// stop the latch on every existing backend and no test would fail. This is that test.
        /// </summary>
        [Fact]
        public void TheVeldridDevice_StillCarriesTheDisposalHook()
        {
            Assert.True(typeof(IGpuDeviceLifecycle).IsAssignableFrom(typeof(VeldridGpuDevice)));
        }
    }

    /// <summary>
    /// A <see cref="FakeGpuDevice"/> that also records, in order, the teardown calls
    /// <see cref="GpuDeviceContext.Dispose"/> makes on it. Everything else delegates, so it stays exactly as inert
    /// as the fake it wraps and touches no driver.
    /// </summary>
    internal sealed class RecordingGpuDevice : IGpuDevice, IGpuDeviceLifecycle
    {
        readonly FakeGpuDevice _inner;
        readonly List<string> _calls = new();

        internal RecordingGpuDevice(GpuBackendKind backend = GpuBackendKind.Direct3D11)
            => _inner = new FakeGpuDevice(backend);

        internal IReadOnlyList<string> Calls => _calls;

        public void MarkDeviceDisposed() => _calls.Add(nameof(MarkDeviceDisposed));

        public void Dispose()
        {
            _calls.Add(nameof(Dispose));
            _inner.Dispose();
        }

        public GpuBackendKind Backend => _inner.Backend;
        public GpuCapabilities Capabilities => _inner.Capabilities;
        public IGpuResourceFactory Factory => _inner.Factory;
        public IGpuFramebuffer? SwapchainFramebuffer => _inner.SwapchainFramebuffer;
        public IGpuSampler PointSampler => _inner.PointSampler;
        public IGpuSampler LinearSampler => _inner.LinearSampler;

        public bool SyncToVerticalBlank
        {
            get => _inner.SyncToVerticalBlank;
            set => _inner.SyncToVerticalBlank = value;
        }

        public void Submit(IGpuCommandList cl) => _inner.Submit(cl);
        public void Submit(IGpuCommandList cl, IGpuFence fence) => _inner.Submit(cl, fence);
        public void WaitForIdle() => _inner.WaitForIdle();

        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
            => _inner.UpdateBuffer(b, offsetBytes, data);
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged
            => _inner.UpdateBuffer(b, offsetBytes, data);
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged
            => _inner.UpdateBuffer(b, offsetBytes, in data);

        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height)
            => _inner.UpdateTexture(texture, data, x, y, width, height);
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height,
            uint mipLevel, uint arrayLayer)
            => _inner.UpdateTexture(texture, data, x, y, width, height, mipLevel, arrayLayer);

        public MappedData Map(IGpuTexture staging, GpuMapMode mode) => _inner.Map(staging, mode);
        public void Unmap(IGpuTexture staging) => _inner.Unmap(staging);
        public MappedData Map(IGpuBuffer staging, GpuMapMode mode) => _inner.Map(staging, mode);
        public void Unmap(IGpuBuffer staging) => _inner.Unmap(staging);

        public void ResizeSwapchain(uint w, uint h) => _inner.ResizeSwapchain(w, h);
        public void Present() => _inner.Present();
    }
}
