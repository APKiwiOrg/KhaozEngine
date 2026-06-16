using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Headless tests for <see cref="GpuBackendSelector"/>'s pure logic: the env override parsing and the
    /// per-OS default probe, driven through the injectable <see cref="GpuBackendSelector.Select(string?, OSPlatformKind)"/>
    /// overload so no real environment or GPU is touched.
    /// </summary>
    public sealed class GpuBackendSelectorTests
    {
        // --- env override wins, case-insensitive, all four values ---

        [Theory]
        [InlineData("metal", GpuBackendKind.Metal)]
        [InlineData("vulkan", GpuBackendKind.Vulkan)]
        [InlineData("d3d11", GpuBackendKind.Direct3D11)]
        [InlineData("gl", GpuBackendKind.OpenGL)]
        public void Select_EnvOverride_Wins(string env, GpuBackendKind expected)
        {
            // OS would otherwise pick Linux->Vulkan; the override must beat it (except where they coincide).
            Assert.Equal(expected, GpuBackendSelector.Select(env, OSPlatformKind.Linux));
        }

        [Theory]
        [InlineData("METAL", GpuBackendKind.Metal)]
        [InlineData("  Vulkan  ", GpuBackendKind.Vulkan)]
        [InlineData("D3D11", GpuBackendKind.Direct3D11)]
        [InlineData("Gl", GpuBackendKind.OpenGL)]
        public void Select_EnvOverride_IsCaseInsensitiveAndTrimmed(string env, GpuBackendKind expected)
        {
            // macOS would otherwise pick Metal; override must beat it.
            Assert.Equal(expected, GpuBackendSelector.Select(env, OSPlatformKind.Windows));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("nonsense")]
        [InlineData("directx")]
        public void Select_BadOrMissingEnv_FallsThroughToProbe(string? env)
        {
            // Bad/empty/null override is ignored; the OS probe decides (Windows -> D3D11).
            Assert.Equal(GpuBackendKind.Direct3D11, GpuBackendSelector.Select(env, OSPlatformKind.Windows));
        }

        // --- OS probe mapping ---

        [Theory]
        [InlineData(OSPlatformKind.MacOS, GpuBackendKind.Metal)]
        [InlineData(OSPlatformKind.Windows, GpuBackendKind.Direct3D11)]
        [InlineData(OSPlatformKind.Linux, GpuBackendKind.Vulkan)]
        [InlineData(OSPlatformKind.Unknown, GpuBackendKind.Vulkan)]
        public void Probe_MapsOsToDefaultBackend(OSPlatformKind os, GpuBackendKind expected)
        {
            Assert.Equal(expected, GpuBackendSelector.ProbeOS(os));
            // Same result via Select with no override.
            Assert.Equal(expected, GpuBackendSelector.Select(null, os));
        }

        [Theory]
        [InlineData("metal", true, GpuBackendKind.Metal)]
        [InlineData("vulkan", true, GpuBackendKind.Vulkan)]
        [InlineData("d3d11", true, GpuBackendKind.Direct3D11)]
        [InlineData("gl", true, GpuBackendKind.OpenGL)]
        [InlineData("opengl", false, default(GpuBackendKind))]
        [InlineData(null, false, default(GpuBackendKind))]
        public void TryParseBackend_RecognizesKnownValues(string? value, bool ok, GpuBackendKind expected)
        {
            bool parsed = GpuBackendSelector.TryParseBackend(value, out GpuBackendKind backend);
            Assert.Equal(ok, parsed);
            if (ok) Assert.Equal(expected, backend);
        }
    }
}
