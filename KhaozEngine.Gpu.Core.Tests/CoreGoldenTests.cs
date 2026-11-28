using System;
using System.Threading;
using KhaozEngine.Gpu.Core.Internal;
using KhaozEngine.Imaging;
using KhaozEngine.Render2D;
using KhaozEngine.Tests.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.GpuCore;

[Collection("CoreBackend")]
public sealed class CoreGoldenTests(ITestOutputHelper output)
{
    private const string BackendVariable = "KE_GRAPHICS_BACKEND";

    [NativeGpuFact]
    public void Scene2d_primitives_matches_the_vulkan_native_golden()
    {
        long devicesBefore = CoreGpuDevice.CreatedDevices;
        var comparison = RenderScene2dPrimitives();
        // Provenance: the frame came from a device the C++ backend created, not a fallback backend.
        Assert.True(CoreGpuDevice.CreatedDevices > devicesBefore);
        Assert.Equal(0u, CoreGpuDevice.LastDisposedValidationErrors);
        Assert.Empty(comparison.Offenders);
    }

    [NativeGpuFact]
    public void Scene2d_primitives_matches_with_slang_sprite_shaders()
    {
        int substitutions = 0;
        CoreShaderOverrides.Override = (vertex, fragment) =>
        {
            if (vertex != SpriteBatch.VertSrc || fragment != SpriteBatch.FragSrc)
                return null;
            Interlocked.Increment(ref substitutions);
            return CoreShaderOverrides.LoadSlangSprite();
        };
        GoldenGridComparison comparison;
        try
        {
            comparison = RenderScene2dPrimitives();
        }
        finally
        {
            CoreShaderOverrides.Override = null;
        }

        Assert.True(substitutions >= 1);
        Assert.Equal(0u, CoreGpuDevice.LastDisposedValidationErrors);
        Assert.Empty(comparison.Offenders);
    }

    private GoldenGridComparison RenderScene2dPrimitives()
    {
        CoreBackendRegistration.SeatAsVulkanNative();
        string? previous = Environment.GetEnvironmentVariable(BackendVariable);
        byte[] rgba;
        try
        {
            // Pinned in the environment, so the headless capture refuses to fall back to another backend.
            Environment.SetEnvironmentVariable(BackendVariable, "vulkan-native");
            rgba = Render2DSnapshot.Capture(Scene2DPrimitivesScene.Width, Scene2DPrimitivesScene.Height,
                Scene2DPrimitivesScene.Clear, Scene2DPrimitivesScene.Draw);
        }
        finally
        {
            Environment.SetEnvironmentVariable(BackendVariable, previous);
        }

        var comparison = CoreGoldenCheck.Compare("scene2d_primitives", rgba,
            Scene2DPrimitivesScene.Width, Scene2DPrimitivesScene.Height);
        output.WriteLine($"worst cell difference {comparison.WorstDiff:F4}, offenders {comparison.Offenders.Count}");
        return comparison;
    }
}
