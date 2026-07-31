using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Live-device cover for the diagnostics <see cref="GpuDeviceContext"/> reads at creation and hands on to
    /// <c>AppWindow</c>: the adapter description and the injected-module scan. The pure halves are covered headless
    /// in <see cref="GpuInjectedModulesTests"/> and <see cref="GpuThreadingDiagnosticsTests"/>. What only a real
    /// device can check is the WIRING, which is exactly where these two can go wrong without any test noticing.
    /// </summary>
    public sealed class GpuDeviceDiagnosticsGpuTests
    {
        readonly ITestOutputHelper _out;
        public GpuDeviceDiagnosticsGpuTests(ITestOutputHelper o) => _out = o;

        /// <summary>
        /// The accessor is a second name for <c>Capabilities.DeviceName</c>, and a second name is exactly the thing
        /// that can quietly get pointed at the wrong source. On Direct3D11 the value is the DXGI adapter
        /// description, which is why no Vortice interop was needed for it.
        /// </summary>
        [GpuFact]
        public void AdapterDescription_IsTheDeviceNameTheBackendReports()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();

            _out.WriteLine($"backend={ctx.Backend} adapter='{ctx.AdapterDescription}'");
            Assert.Equal(ctx.Capabilities.DeviceName, ctx.AdapterDescription);
            Assert.False(string.IsNullOrEmpty(ctx.AdapterDescription));
        }

        /// <summary>
        /// The null / empty split, checked against the platform actually running. Null means the scan did not run,
        /// which off Windows is correct and everywhere else is a silent loss of the diagnostic. An empty list on
        /// Windows is a clean process and is a perfectly good answer.
        /// </summary>
        [GpuFact]
        public void InjectedModules_AreScannedOnWindowsAndNowhereElse()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IReadOnlyList<string>? modules = ctx.InjectedModules;

            _out.WriteLine($"windows={OperatingSystem.IsWindows()} modules={GpuInjectedModules.Describe(modules)}");
            if (OperatingSystem.IsWindows())
            {
                // Not a count assertion: a CI runner may legitimately have an overlay loaded. What must hold is
                // that the scan produced an answer at all.
                Assert.NotNull(modules);
            }
            else
            {
                Assert.Null(modules);
                Assert.Equal(GpuInjectedModules.UnknownDescription, GpuInjectedModules.Describe(modules));
            }
        }
    }
}
