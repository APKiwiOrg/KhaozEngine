using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// VERIFICATION TASK THREE of row 1 of <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>: what a
    /// default-constructed <c>MTLCompileOptions</c> reports on <c>macos-26</c>, measured so that decision M-S6's
    /// pin is a no-op on the day row 9 writes it down.
    /// <para>
    /// WHAT A RED RUN MEANS, and it is not a bug. These numbers are the OS's defaults, so a failure here says
    /// the machine or the runner image moved them, which is exactly the drift M-S6 exists to catch. The two that
    /// matter are <c>fastMathEnabled</c>, which changes floating-point results and therefore every pixel of the
    /// committed metal goldens, and <c>languageVersion</c>, which defaults to the newest the OS supports and
    /// therefore follows an image promotion. The workflow already pins <c>macos-26</c> by number rather than to
    /// <c>macos-latest</c> so an image promotion cannot move the GPU under a golden gate, and this is the same
    /// hazard one level up. On a red run, read M-S6 and decide deliberately: either the pin follows the new
    /// default, or the compile options stop being defaults and start being stated values.
    /// </para>
    /// <para>
    /// WHICH RED RUN ARE YOU LOOKING AT, and they want opposite responses. These values are pinned to what
    /// <c>macos-26</c> reports, and that is the only run that gates anything: the golden leg is pinned to
    /// <c>macos-26</c> by number, so a failure THERE means the runner image moved the defaults out from under
    /// the committed metal goldens and M-S6 has to be re-decided. A failure on a DEVELOPER'S Mac running an
    /// OLDER macOS is expected and is not a regression. An older OS caps <c>languageVersion</c> lower and may
    /// not carry <c>mathMode</c> at all, so it fails here while the build it produces is fine. If that is you,
    /// read the reported values, confirm they are the older OS's ceiling rather than a moved default, and carry
    /// on. Do not "fix" the pin to match your machine, because the pin's whole job is to describe the image the
    /// goldens were baked on.
    /// </para>
    /// <para>
    /// It is prose rather than an OS-version gate on purpose. Gating would need a version predicate, this repo
    /// has no precedent for one (every platform check in it is a plain <c>OperatingSystem.IsMacOS()</c>), and a
    /// gate that silently passes on an older OS would remove the signal on the one machine that matters the day
    /// the pinned image is bumped and someone reads a green run as agreement.
    /// </para>
    /// <para>
    /// No device is created, so this is a plain fact rather than a <c>[GpuFact]</c>: <c>MTLCompileOptions</c> is
    /// an ordinary object and what is being measured is the operating system. It goes dormant off macOS rather
    /// than skipping, for the same reason every row in this phase does.
    /// </para>
    /// </summary>
    public sealed class MetalCompileOptionsPinTests
    {
        readonly ITestOutputHelper _output;

        public MetalCompileOptionsPinTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void DefaultCompileOptionsAreTheOnesTheDesignPins()
        {
            if (!KhaozEngineMetal.IsPlatformSupported)
            {
                _output.WriteLine("dormant: not macOS, so there is no MTLCompileOptions to read.");
                return;
            }

            MetalCompileOptionsReading reading = MetalCompileOptionsProbe.Read();
            string report = reading.Report();
            _output.WriteLine(report);

            Assert.True(reading.Read, "MTLCompileOptions could not be constructed:\n" + report);

            // Measured on macOS 26 (Darwin 25.6) on an Apple M2 Max, 2026-08-10. The raw value is 196610, which
            // is the packed (3 << 16) | 2 the MTLLanguageVersion enum uses, so this OS defaults a
            // default-constructed MTLCompileOptions to Metal Shading Language 3.2.
            Assert.Equal("3.2", reading.LanguageVersion);
            Assert.Equal((nuint)196610, reading.LanguageVersionRaw);
            Assert.True(reading.FastMathEnabled,
                "fastMathEnabled is no longer on by default. Pinning it to on was a no-op guard against exactly "
                + "this, and it moving means the committed metal goldens were baked under a different "
                + "floating-point regime from the one a build would use now:\n" + report);

            // The design leaves this at its default (off) and says so, which is only a decision if the default
            // was checked. It forces position computation to be invariant across pipelines, and turning it on is
            // a follow-up with a trigger (Z-fighting between the depth prepass and a later pass) rather than a
            // speculative change to a golden-bearing knob.
            Assert.False(reading.PreserveInvariance,
                "preserveInvariance is on by default now, which the design assumed it was not:\n" + report);

            // The newer spelling of the same knob, and it AGREES with the older one on this OS: mathMode reads 2
            // (MTLMathModeFast) while fastMathEnabled reads true. That agreement is the useful part, because it
            // says pinning fastMathEnabled is still pinning the real setting rather than a shim that has stopped
            // being read. The day these two disagree is the day M-S6's pin has to move to mathMode.
            Assert.True(reading.RespondsToMathMode,
                "this OS has no mathMode property, so the newer spelling of the fast-math knob cannot be "
                + "cross-checked against fastMathEnabled here:\n" + report);
            Assert.Equal(2, reading.MathModeRaw);
        }
    }
}
