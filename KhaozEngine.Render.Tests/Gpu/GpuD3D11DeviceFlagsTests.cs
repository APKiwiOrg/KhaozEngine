using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Headless tests for <see cref="GpuD3D11DeviceFlags"/>: the numeric flag value, and the on/off parse of the
    /// environment gate that adds it. Pure, so it runs on any OS with no device.
    /// </summary>
    public sealed class GpuD3D11DeviceFlagsTests
    {
        /// <summary>
        /// The constant is TAKEN from Vortice's enum, so this pins it to the documented Windows SDK number from the
        /// other direction: a future Vortice rename or repoint fails here instead of silently setting a different
        /// flag on every tester's device. Not tautological, and not idle either. The plausible hand-written guess,
        /// 0x800, is VideoSupport.
        /// </summary>
        [Fact]
        public void PreventInternalThreadingOptimizations_IsTheDocumentedD3D11Value()
        {
            Assert.Equal(0x8u, GpuD3D11DeviceFlags.PreventInternalThreadingOptimizations);
        }

        [Fact]
        public void EnvVarName_FollowsTheEngineConvention()
        {
            Assert.StartsWith("KE_", GpuD3D11DeviceFlags.EnvVarName);
            Assert.Equal("KE_D3D11_PREVENT_THREADING_OPTIMIZATIONS", GpuD3D11DeviceFlags.EnvVarName);
        }

        [Theory]
        [InlineData("1")]
        [InlineData("true")]
        [InlineData("TRUE")]
        [InlineData("Yes")]
        [InlineData("on")]
        [InlineData("  on  ")]
        public void Resolve_OnValues_AddTheFlag(string value)
        {
            uint flags = GpuD3D11DeviceFlags.Resolve(value, out string? unrecognized);

            Assert.Equal(GpuD3D11DeviceFlags.PreventInternalThreadingOptimizations, flags);
            Assert.Null(unrecognized);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("0")]
        [InlineData("false")]
        [InlineData("No")]
        [InlineData("OFF")]
        public void Resolve_OffValues_AddNothing(string? value)
        {
            uint flags = GpuD3D11DeviceFlags.Resolve(value, out string? unrecognized);

            Assert.Equal(0u, flags);
            Assert.Null(unrecognized);
        }

        [Fact]
        public void Resolve_UnrecognizedValue_AddsNothingAndReportsItVerbatim()
        {
            // A mistyped gate that silently does nothing is indistinguishable from the default, so a whole test
            // session can be spent proving nothing. The raw text comes back so the caller can say what was typed.
            uint flags = GpuD3D11DeviceFlags.Resolve(" Enabled ", out string? unrecognized);

            Assert.Equal(0u, flags);
            Assert.Equal(" Enabled ", unrecognized);
        }

        [Fact]
        public void UnrecognizedWarning_NamesTheValueAndTheValidOnes()
        {
            string warning = GpuD3D11DeviceFlags.UnrecognizedWarning("Enabled");

            Assert.Contains(GpuD3D11DeviceFlags.EnvVarName, warning);
            Assert.Contains("Enabled", warning);
            Assert.Contains("1/true/yes/on", warning);
        }

        /// <summary>
        /// The INFO line exists so a tester's log PROVES the lever was on. It has to name the D3D11 flag verbatim,
        /// so it can be grepped, and the env var, so the reader knows what turned it on.
        /// </summary>
        [Fact]
        public void ActiveDescription_IsGreppableAndNamesTheGate()
        {
            string text = GpuD3D11DeviceFlags.ActiveDescription;

            Assert.Contains("D3D11_CREATE_DEVICE_PREVENT_INTERNAL_THREADING_OPTIMIZATIONS", text);
            Assert.Contains("ACTIVE", text);
            Assert.Contains(GpuD3D11DeviceFlags.EnvVarName, text);
        }
    }
}
