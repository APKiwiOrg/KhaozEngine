using System;
using System.Text.Json;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE ROUTE DECISIONS G2 AND G3 TAKE OUT OF THE DEVICE AND INTO A CAPTURE: the software-adapter flag and the
    /// device-loss reason, from <see cref="IGpuDevice.Diagnostics"/> through
    /// <see cref="GpuDeviceContext.Diagnostics"/> and <see cref="GpuTelemetry.WithGpu(TelemetrySessionInfo, GpuDeviceContext)"/>
    /// into the telemetry session header. Device-free end to end, over a fake device.
    /// <para>
    /// THE PROPERTY THAT MATTERS IS THAT IT IS READ LIVE. A device loss happens at an arbitrary moment long after
    /// creation, so a value captured when the device was made would always report a device that had not been lost
    /// yet, which is precisely the case the field exists for. Everything else the header carries about the GPU is
    /// fixed at creation and travels as data, and these two do not, which is why they are a seam member.
    /// </para>
    /// </summary>
    public sealed class GpuDeviceDiagnosticsTests
    {
        // A device that reports whatever the test sets on it, right now. Everything else is inherited from the
        // counting fake, so this file adds one behaviour rather than a second device.
        sealed class DiagnosticGpuDevice : IGpuDevice
        {
            readonly FakeGpuDevice _inner = new(GpuBackendKind.Direct3D11Native);

            internal GpuDeviceDiagnostics Reported { get; set; }

            public GpuDeviceDiagnostics Diagnostics => Reported;

            public GpuBackendKind Backend => GpuBackendKind.Direct3D11Native;
            public GpuCapabilities Capabilities => _inner.Capabilities;
            public IGpuResourceFactory Factory => _inner.Factory;
            public IGpuFramebuffer? SwapchainFramebuffer => null;
            public IGpuSampler PointSampler => _inner.PointSampler;
            public IGpuSampler LinearSampler => _inner.LinearSampler;

            public void Submit(IGpuCommandList cl) { }
            public void Submit(IGpuCommandList cl, IGpuFence fence) { }
            public void WaitForIdle() { }
            public void UpdateBuffer<T>(IGpuBuffer b, uint o, ReadOnlySpan<T> d) where T : unmanaged { }
            public void UpdateBuffer<T>(IGpuBuffer b, uint o, T[] d) where T : unmanaged { }
            public void UpdateBuffer<T>(IGpuBuffer b, uint o, in T d) where T : unmanaged { }
            public void UpdateTexture(IGpuTexture t, byte[] d, uint x, uint y, uint w, uint h) { }
            public void UpdateTexture(IGpuTexture t, byte[] d, uint x, uint y, uint w, uint h, uint m, uint l) { }
            public MappedData Map(IGpuTexture s, GpuMapMode m) => throw new NotSupportedException();
            public void Unmap(IGpuTexture s) { }
            public MappedData Map(IGpuBuffer s, GpuMapMode m) => throw new NotSupportedException();
            public void Unmap(IGpuBuffer s) { }
            public void ResizeSwapchain(uint width, uint height) { }
            public void Present() { }
            public bool SyncToVerticalBlank { get; set; }
            public void Dispose() { }
        }

        static GpuBackendSelection Selection()
            => new(GpuBackendKind.Direct3D11Native, GpuBackendSource.UserPreference, null);

        /// <summary>NULL IS "NOBODY ANSWERED", not "no". A backend that does not report the software-adapter flag
        /// is a different fact from one that reports false, and a capture that cannot tell those apart cannot say
        /// whether its performance numbers are comparable with another capture's.</summary>
        [Fact]
        public void ADefaultDiagnosticsSnapshotAnswersNothing()
        {
            var diagnostics = default(GpuDeviceDiagnostics);

            Assert.Null(diagnostics.SoftwareAdapter);
            Assert.Null(diagnostics.DeviceLossReason);
            Assert.False(diagnostics.IsDeviceLost);
        }

        /// <summary>The member was APPENDED with a default implementation, so every existing
        /// <see cref="IGpuDevice"/> kept compiling, and the default is the honest one: no answers. The Veldrid
        /// path took it, which was correct rather than a gap, since Veldrid exposes neither the DXGI adapter flag
        /// nor a device-removal reason.</summary>
        [Fact]
        public void ADeviceThatDoesNotOverrideTheMemberReportsNoAnswers()
        {
            IGpuDevice device = new FakeGpuDevice();

            Assert.Null(device.Diagnostics.SoftwareAdapter);
            Assert.Null(device.Diagnostics.DeviceLossReason);
        }

        /// <summary>The context reads THROUGH to the device on every access rather than capturing at
        /// construction, which is the whole reason this is a seam member.</summary>
        [Fact]
        public void TheContextReadsTheDeviceLive()
        {
            var device = new DiagnosticGpuDevice { Reported = new GpuDeviceDiagnostics(softwareAdapter: true) };
            using var ctx = new GpuDeviceContext(device, threadingCaps: null, threadingProbeFailure: null,
                Selection(), ownsDevice: true);

            Assert.True(ctx.Diagnostics.SoftwareAdapter);
            Assert.Null(ctx.Diagnostics.DeviceLossReason);

            device.Reported = new GpuDeviceDiagnostics(true, "DXGI_ERROR_DEVICE_HUNG at present");

            Assert.Equal("DXGI_ERROR_DEVICE_HUNG at present", ctx.Diagnostics.DeviceLossReason);
            Assert.True(ctx.Diagnostics.IsDeviceLost);
        }

        [Fact]
        public void WithGpu_CarriesBothFactsOntoTheHeaderOptions()
        {
            var device = new DiagnosticGpuDevice
            {
                Reported = new GpuDeviceDiagnostics(false, "DXGI_ERROR_DEVICE_RESET at staging map"),
            };
            using var ctx = new GpuDeviceContext(device, threadingCaps: null, threadingProbeFailure: null,
                Selection(), ownsDevice: true);

            var info = new TelemetrySessionInfo().WithGpu(ctx);

            Assert.False(info.SoftwareAdapter);
            Assert.Equal("DXGI_ERROR_DEVICE_RESET at staging map", info.DeviceLossReason);
        }

        /// <summary>The four-argument overload is unchanged and leaves both fields at "nobody answered", which is
        /// what keeps an already-compiled consumer binding to the method it was compiled against without silently
        /// claiming anything about its device.</summary>
        [Fact]
        public void TheOlderOverloadLeavesBothFieldsUnanswered()
        {
            var info = new TelemetrySessionInfo()
                .WithGpu(Selection(), "WARP", null, null);

            Assert.Null(info.SoftwareAdapter);
            Assert.Null(info.DeviceLossReason);
        }

        /// <summary>Both fields appear in the header's <c>gpu</c> object, three-valued for the flag and null for
        /// the ordinary session that lost no device. Appended fields, so the schema version does not move.</summary>
        [Fact]
        public void TheSessionHeaderCarriesBothFields()
        {
            var info = new TelemetrySessionInfo { AdapterDescription = "Microsoft Basic Render Driver" };
            info.SoftwareAdapter = true;
            info.DeviceLossReason = "DXGI_ERROR_DEVICE_HUNG at replay";

            using JsonDocument document = JsonDocument.Parse(
                TelemetrySessionHeader.Build(info, Array.Empty<TelemetryHeaderValue>()));
            JsonElement gpu = document.RootElement.GetProperty("session").GetProperty("gpu");

            Assert.True(gpu.GetProperty("softwareAdapter").GetBoolean());
            Assert.Equal("DXGI_ERROR_DEVICE_HUNG at replay", gpu.GetProperty("deviceLossReason").GetString());
            Assert.Equal(1, document.RootElement.GetProperty("session").GetProperty("v").GetInt32());
        }

        [Fact]
        public void TheSessionHeaderWritesNullForBothWhenNothingAnswered()
        {
            using JsonDocument document = JsonDocument.Parse(
                TelemetrySessionHeader.Build(new TelemetrySessionInfo(), Array.Empty<TelemetryHeaderValue>()));
            JsonElement gpu = document.RootElement.GetProperty("session").GetProperty("gpu");

            Assert.Equal(JsonValueKind.Null, gpu.GetProperty("softwareAdapter").ValueKind);
            Assert.Equal(JsonValueKind.Null, gpu.GetProperty("deviceLossReason").ValueKind);
        }

        /// <summary>False and null are OPPOSITE facts here, exactly as they are for the injected-module scan, so
        /// the header keeps them apart rather than folding both to null.</summary>
        [Fact]
        public void TheSoftwareAdapterFlagIsThreeValued()
        {
            var hardware = new TelemetrySessionInfo { SoftwareAdapter = false };

            using JsonDocument document = JsonDocument.Parse(
                TelemetrySessionHeader.Build(hardware, Array.Empty<TelemetryHeaderValue>()));
            JsonElement flag = document.RootElement.GetProperty("session").GetProperty("gpu")
                .GetProperty("softwareAdapter");

            Assert.Equal(JsonValueKind.False, flag.ValueKind);
        }
    }
}
