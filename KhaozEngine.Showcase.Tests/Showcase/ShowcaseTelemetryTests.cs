using System;
using System.Collections.Generic;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu;
using KhaozEngine.Showcase;
using Xunit;

namespace KhaozEngine.Tests.Showcase
{
    /// <summary>
    /// The showcase's env-gated capture lever (<see cref="ShowcaseTelemetry"/>), headless: the gate that decides
    /// whether anything records at all, and the header mapping a capture is read by. Both halves are pure, which
    /// is why the lever exposes a window-free overload of the header builder.
    /// </summary>
    public class ShowcaseTelemetryTests
    {
        static GpuBackendSelection Selection(GpuBackendKind backend, GpuBackendSource source)
            => new(backend, source, RequestedOverride: null);

        [Fact]
        public void ResolvePath_IsNull_WhenTheLeverIsNotPulled()
        {
            using var _ = new EnvVar(ShowcaseTelemetry.PathVariable, null);
            Assert.Null(ShowcaseTelemetry.ResolvePath());
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void ResolvePath_TreatsBlankAsUnset(string value)
        {
            // An exported-but-empty variable is the common shell accident. It must not create a file.
            using var _ = new EnvVar(ShowcaseTelemetry.PathVariable, value);
            Assert.Null(ShowcaseTelemetry.ResolvePath());
        }

        [Fact]
        public void ResolvePath_ReturnsTheNamedFile()
        {
            using var _ = new EnvVar(ShowcaseTelemetry.PathVariable, "/tmp/ke-capture.jsonl");
            Assert.Equal("/tmp/ke-capture.jsonl", ShowcaseTelemetry.ResolvePath());
        }

        [Fact]
        public void SessionInfo_NamesTheShowcaseAndTheBackendThatRan()
        {
            TelemetrySessionInfo info = ShowcaseTelemetry.SessionInfo(
                Selection(GpuBackendKind.Metal, GpuBackendSource.OsProbe), "Apple M2 Max", null, null);

            Assert.Equal("KhaozEngine Showcase", info.AppName);
            Assert.False(string.IsNullOrWhiteSpace(info.AppVersion));
            Assert.Equal("Metal", info.GpuBackend);
            Assert.Equal("OsProbe", info.GpuBackendSource);
            Assert.Equal("Apple M2 Max", info.AdapterDescription);
        }

        [Fact]
        public void SessionInfo_CarriesTheOverrideSource_SoANativeCaptureIsDistinguishable()
        {
            // The reading that voids a gate capture: a header naming the incumbent when the run asked for the
            // native backend means the override did not take. Both halves have to be legible in the file.
            TelemetrySessionInfo info = ShowcaseTelemetry.SessionInfo(
                Selection(GpuBackendKind.MetalNative, GpuBackendSource.EnvironmentOverride), "Apple M2 Max", null, null);

            Assert.Equal("MetalNative", info.GpuBackend);
            Assert.Equal("EnvironmentOverride", info.GpuBackendSource);
        }

        [Fact]
        public void SessionInfo_CarriesNoGameValues()
        {
            // The showcase has no durables of its own to record. An empty game section is the correct output,
            // not an oversight: everything a capture of the testbed needs is engine identity.
            TelemetrySessionInfo info = ShowcaseTelemetry.SessionInfo(
                Selection(GpuBackendKind.Metal, GpuBackendSource.OsProbe), null, null, null);

            Assert.Empty(info.GameValues);
        }

        [Fact]
        public void AFreshLever_IsUnarmed()
        {
            // Construction alone records nothing, which is what makes the lever free in an ordinary boot: the
            // recorder is only opened by Start, and only when the variable named a file.
            using var telemetry = new ShowcaseTelemetry();
            Assert.False(telemetry.IsRecording);
            Assert.Null(telemetry.CurrentPath);
        }

        /// <summary>Set an environment variable for the scope of one test and put it back afterwards.</summary>
        sealed class EnvVar : IDisposable
        {
            readonly string _name;
            readonly string? _previous;

            public EnvVar(string name, string? value)
            {
                _name = name;
                _previous = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }

            public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
        }
    }
}
