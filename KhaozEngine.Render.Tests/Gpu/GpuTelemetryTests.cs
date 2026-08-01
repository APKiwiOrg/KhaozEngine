using System;
using System.Collections.Generic;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Headless tests for <see cref="GpuTelemetry"/>, the bridge that fills a telemetry session header's GPU
    /// fields from what the engine resolved at device creation. The value overload is what a consumer holding an
    /// <c>AppWindow</c> calls, and it is pure, so the whole mapping is testable with no device on any OS. The
    /// device overload is a two-line forward onto it and needs a live <see cref="GpuDeviceContext"/>, so it is
    /// covered by the fields it reads rather than duplicated here.
    /// </summary>
    public sealed class GpuTelemetryTests
    {
        [Fact]
        public void WithGpu_MapsBackendAndProvenanceOntoTheirEnumNames()
        {
            var selection = new GpuBackendSelection(
                GpuBackendKind.Direct3D11, GpuBackendSource.FallbackAfterFailure, "vulkan", GpuBackendKind.Vulkan);

            var info = new TelemetrySessionInfo().WithGpu(selection, "NVIDIA GeForce RTX 4070", null, null);

            Assert.Equal("Direct3D11", info.GpuBackend);
            Assert.Equal("FallbackAfterFailure", info.GpuBackendSource);
            Assert.Equal("NVIDIA GeForce RTX 4070", info.AdapterDescription);
        }

        [Fact]
        public void WithGpu_CarriesTheThreadingCapsApart()
        {
            var selection = new GpuBackendSelection(GpuBackendKind.Direct3D11, GpuBackendSource.OsProbe, null);

            var withCaps = new TelemetrySessionInfo()
                .WithGpu(selection, "", null, new GpuThreadingCaps(DriverCommandLists: false, DriverConcurrentCreates: true));
            Assert.True(withCaps.HasThreadingCaps);
            Assert.False(withCaps.DriverCommandLists);
            Assert.True(withCaps.DriverConcurrentCreates);

            var withoutCaps = new TelemetrySessionInfo().WithGpu(selection, "", null, null);
            Assert.False(withoutCaps.HasThreadingCaps);
            Assert.Null(withoutCaps.DriverCommandLists);
            Assert.Null(withoutCaps.DriverConcurrentCreates);
        }

        [Fact]
        public void WithGpu_KeepsAnUnscannedModuleListApartFromACleanScan()
        {
            var selection = new GpuBackendSelection(GpuBackendKind.Metal, GpuBackendSource.OsProbe, null);

            Assert.Null(new TelemetrySessionInfo().WithGpu(selection, "Apple M2", null, null).InjectedModules);

            IReadOnlyList<string>? clean =
                new TelemetrySessionInfo().WithGpu(selection, "Apple M2", Array.Empty<string>(), null).InjectedModules;
            Assert.NotNull(clean);
            Assert.Empty(clean);

            IReadOnlyList<string>? hooked = new TelemetrySessionInfo()
                .WithGpu(selection, "Apple M2", new[] { "RTSSHooks64.dll" }, null).InjectedModules;
            Assert.Equal(new[] { "RTSSHooks64.dll" }, hooked);
        }

        [Fact]
        public void WithGpu_ReturnsTheSameInstanceSoCallsChain()
        {
            var info = new TelemetrySessionInfo { AppName = "Ruinborne" };
            var selection = new GpuBackendSelection(GpuBackendKind.Vulkan, GpuBackendSource.EnvironmentOverride, "vulkan");

            TelemetrySessionInfo returned = info.WithGpu(selection, "llvmpipe", null, null);

            Assert.Same(info, returned);
            Assert.Equal("Ruinborne", returned.AppName);
        }
    }
}
