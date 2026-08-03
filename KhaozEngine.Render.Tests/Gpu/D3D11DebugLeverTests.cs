using System;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION G4's DEVICE HALF: <c>KE_D3D11_DEBUG</c> as <see cref="D3D11DebugLayer"/> reads it, and the pin
    /// that keeps it saying the same thing as <see cref="D3D11ShaderDebug"/>, which is the same variable's shader
    /// half.
    /// <para>
    /// THE TWO-READER PIN IS THE TEST WORTH HAVING HERE. One variable does two things deliberately, because a
    /// session debugging a Direct3D problem wants both and remembering two names to get one answer is how a
    /// capture ends up taken with half the instrumentation on. The cost of that decision is two independent
    /// parses that could drift, and a drift would produce exactly the half-instrumented capture the single
    /// variable exists to prevent. Driving both off the same value table is what makes drift a red test.
    /// </para>
    /// </summary>
    public sealed class D3D11DebugLeverTests
    {
        [Theory]
        [InlineData("1")]
        [InlineData("true")]
        [InlineData("TRUE")]
        [InlineData("yes")]
        [InlineData("on")]
        [InlineData("  On  ")]
        public void Resolve_TurnsTheDebugLayerOnForEveryRecognizedOnValue(string value)
        {
            Assert.Equal(D3D11DebugLayer.CreateDeviceDebug, D3D11DebugLayer.Resolve(value, out string? unrecognized));
            Assert.Null(unrecognized);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("0")]
        [InlineData("false")]
        [InlineData("no")]
        [InlineData("off")]
        public void Resolve_LeavesItOffForUnsetAndForEveryRecognizedOffValue(string? value)
        {
            Assert.Equal(0u, D3D11DebugLayer.Resolve(value, out string? unrecognized));
            Assert.Null(unrecognized);
        }

        /// <summary>A mistyped debug gate that silently did nothing is indistinguishable from a correct default
        /// run, so a whole capture session can be spent looking at output that was never going to be there. The
        /// value comes back VERBATIM, original case and surrounding whitespace included, because a stray quote is
        /// what the warning exists to make visible.</summary>
        [Fact]
        public void Resolve_ReportsAnUnrecognizedValueVerbatim()
        {
            Assert.Equal(0u, D3D11DebugLayer.Resolve(" Yess ", out string? unrecognized));
            Assert.Equal(" Yess ", unrecognized);
            Assert.Contains(" Yess ", D3D11DebugLayer.UnrecognizedWarning(" Yess "), StringComparison.Ordinal);
        }

        /// <summary>
        /// THE PIN. The device half and the shader half read the same variable, so they must agree on every value
        /// in the recognized sets and on what counts as unrecognized. Anything else produces a session with debug
        /// shaders and no debug layer, or the reverse, and nothing anywhere would say so.
        /// </summary>
        [Theory]
        [InlineData("1", true)]
        [InlineData("true", true)]
        [InlineData("yes", true)]
        [InlineData("on", true)]
        [InlineData("0", false)]
        [InlineData("false", false)]
        [InlineData("no", false)]
        [InlineData("off", false)]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("nonsense", false)]
        public void TheDeviceHalfAndTheShaderHalfNeverDisagree(string? value, bool expectedOn)
        {
            bool deviceOn = D3D11DebugLayer.Resolve(value, out string? deviceUnrecognized) != 0u;
            bool shaderOn = D3D11ShaderDebug.Resolve(value, out string? shaderUnrecognized)
                == D3D11ShaderDebug.DebugBuild;

            Assert.Equal(expectedOn, deviceOn);
            Assert.Equal(expectedOn, shaderOn);
            Assert.Equal(deviceUnrecognized, shaderUnrecognized);
        }

        /// <summary>They also name the same variable, through the same constant rather than two spellings.</summary>
        [Fact]
        public void BothHalvesNameTheSameEnvironmentVariable()
        {
            Assert.Equal("KE_D3D11_DEBUG", D3D11DebugLayer.EnvVarName);
            Assert.Equal(D3D11ShaderDebug.EnvVarName, D3D11DebugLayer.EnvVarName);
        }

        /// <summary><c>D3D11_CREATE_DEVICE_DEBUG</c>, pinned to the documented Windows SDK value so a Vortice
        /// rename or repoint fails a test rather than silently changing which flag the engine sets. The plausible
        /// hand-written guess is wrong often enough here to be worth the assertion.</summary>
        [Fact]
        public void TheDebugFlagIsTheDocumentedWindowsSdkValue()
        {
            Assert.Equal(0x2u, D3D11DebugLayer.CreateDeviceDebug);
        }

        /// <summary>
        /// A DEBUG-LAYER REQUEST THIS MACHINE CANNOT SATISFY IS RETRIED WITHOUT THE FLAG, not failed. The layer is
        /// a separately installed Windows component, so the alternative is that setting the variable stops the app
        /// starting, and the person who set it is by definition mid-diagnosis. The retry is narrow: only that one
        /// HRESULT, and only when the flag was actually asked for.
        /// </summary>
        [Fact]
        public void ShouldRetryWithoutDebugLayer_OnlyForTheMissingSdkComponent()
        {
            Assert.True(D3D11DebugLayer.ShouldRetryWithoutDebugLayer(
                D3D11DebugLayer.CreateDeviceDebug, D3D11DebugLayer.SdkComponentMissing));

            // Not asked for: an ordinary creation failure must not be retried into a second, more confusing one.
            Assert.False(D3D11DebugLayer.ShouldRetryWithoutDebugLayer(0u, D3D11DebugLayer.SdkComponentMissing));

            // Asked for, but the failure is something else entirely.
            Assert.False(D3D11DebugLayer.ShouldRetryWithoutDebugLayer(
                D3D11DebugLayer.CreateDeviceDebug, D3D11DeviceLossCodes.InvalidCall));
            Assert.False(D3D11DebugLayer.ShouldRetryWithoutDebugLayer(
                D3D11DebugLayer.CreateDeviceDebug, D3D11DeviceLossCodes.Ok));
        }

        /// <summary>The unavailable warning names the fix, because a message that says only what went wrong sends
        /// the reader to search for it.</summary>
        [Fact]
        public void TheUnavailableWarningNamesWhatToInstall()
        {
            Assert.Contains("Graphics Tools", D3D11DebugLayer.UnavailableWarning(), StringComparison.Ordinal);
            Assert.Contains(D3D11DebugLayer.EnvVarName, D3D11DebugLayer.UnavailableWarning(), StringComparison.Ordinal);
        }

        /// <summary>The INFO line for an active run has to prove the lever was on, so it names the variable and
        /// says the messages are rate limited and raised to WARN.</summary>
        [Fact]
        public void TheActiveDescriptionProvesTheLeverWasOn()
        {
            Assert.Contains(D3D11DebugLayer.EnvVarName, D3D11DebugLayer.ActiveDescription, StringComparison.Ordinal);
            Assert.Contains("WARN", D3D11DebugLayer.ActiveDescription, StringComparison.Ordinal);
        }

        /// <summary>Reading the live environment is the one impure member. What is asserted is that asking is
        /// legal off Windows, not what the answer was.</summary>
        [Fact]
        public void FromEnvironment_IsReadableOnAnyOperatingSystem()
        {
            _ = D3D11DebugLayer.FromEnvironment(out _);
        }

        /// <summary>
        /// <c>KE_D3D11_PREVENT_THREADING_OPTIMIZATIONS</c> KEEPS ITS EXACT SEMANTICS, which decision G4 requires
        /// in as many words and which nothing in rows 1 to 3 or in this row changes. It lives in
        /// <c>KhaozEngine.Gpu.GpuD3D11DeviceFlags</c>, unmoved and unedited, with its own value parsing and its own
        /// unrecognised-value warning, and <c>GpuD3D11DeviceFlagsTests</c> is its test. It is named here only so a
        /// reader auditing G4 finds the statement in the file that owns the OTHER half of G4 rather than
        /// concluding it was missed.
        /// </summary>
        [Fact]
        public void ThePreventThreadingOptimizationsLeverIsUnchangedAndLivesElsewhere()
        {
            Assert.Equal("KE_D3D11_PREVENT_THREADING_OPTIMIZATIONS", KhaozEngine.Gpu.GpuD3D11DeviceFlags.EnvVarName);
            Assert.Equal(KhaozEngine.Gpu.GpuD3D11DeviceFlags.PreventInternalThreadingOptimizations,
                KhaozEngine.Gpu.GpuD3D11DeviceFlags.Resolve("1", out _));
            Assert.Equal(0u, KhaozEngine.Gpu.GpuD3D11DeviceFlags.Resolve("nonsense", out string? unrecognized));
            Assert.Equal("nonsense", unrecognized);
        }
    }
}
