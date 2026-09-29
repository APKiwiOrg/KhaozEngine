using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu.Vulkan.Internal;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// FLIPPING THE RESOLVE'S ENTRY POINT AND PRESET KEEPS DEVICE MEMORY FLAT, at a 3456x2234 display where one set of
    /// replaced targets is hundreds of megabytes. Every step changes what the scene holds: the fused and split entry
    /// points, three presets (each its own internal size) and anti-aliasing off, cycle after cycle, the way the cost
    /// measurement rotates its modes. Every target a step replaces must be gone by the same step of the next cycle.
    /// <para>
    /// On the native Vulkan backend it reads the device's live <c>vkAllocateMemory</c> count after each cycle, and no
    /// later cycle may end above the first. It prints the retire list beside it. A disposed Vulkan image keeps its
    /// memory until the device's frame boundary runs the deferred destroy, and a fixture that never reached that
    /// boundary ran a 16 GB
    /// Tesla T4 out of device memory within one cost fact at Native. Other backends free on dispose and report no such
    /// reading, so there it checks the resolve's own holding only: a frame on the fused entry point holds none of the
    /// split's targets.
    /// </para>
    /// </summary>
    public sealed class TemporalEntryFlipMemoryGpuTests(ITestOutputHelper output)
    {
        const int W = 3456, H = 2234, Cycles = 4, FramesPerStep = 2;

        // One cycle, anti-aliasing off last (a null entry), so the next cycle rebuilds the temporal targets as well.
        static readonly (TemporalUpscale Preset, TemporalResolveEntry? Entry)[] Steps =
        [
            (TemporalUpscale.Native, TemporalResolveEntry.Fused),
            (TemporalUpscale.Native, TemporalResolveEntry.Split),
            (TemporalUpscale.Quality, TemporalResolveEntry.Split),
            (TemporalUpscale.Quality, TemporalResolveEntry.Fused),
            (TemporalUpscale.Performance, TemporalResolveEntry.Split),
            (TemporalUpscale.Native, null),
        ];

        [GpuFact]
        public void FlippingTheEntryAndThePresetKeepsDeviceMemoryFlat()
        {
            var stage = new FrontStage(W, H, 4.5f);
            using var fx = new TemporalFixture(W, H, s => stage.Setup(s, AntiAliasing.Temporal));
            Scene3D scene = fx.Scene;
            var vulkan = fx.Device as VulkanGpuDevice;
            output.WriteLine($"{fx.Device.Backend} on {fx.Device.Capabilities.DeviceName}, {W}x{H}, {Cycles} cycles of "
                + $"{Steps.Length} steps, {FramesPerStep} frames a step");

            var readings = new List<(long Live, int Retired)>();
            for (int c = 0; c < Cycles; c++)
            {
                foreach ((TemporalUpscale preset, TemporalResolveEntry? entry) in Steps)
                {
                    scene.Post.Quality.AntiAliasing = entry is null ? AntiAliasing.Off : AntiAliasing.Temporal;
                    scene.Post.Temporal.Upscale = preset;
                    scene.TemporalResolveEntryForTests = entry;
                    fx.Frames(FramesPerStep, Draw(stage));
                    Assert.Equal(entry is not null, scene.ResolvedLastRenderForTests);
                    var renderer = scene.TemporalResolveRendererForTests
                        ?? throw new InvalidOperationException("the resolve never ran");
                    // Only a frame that records the split holds its targets.
                    Assert.Equal(entry == TemporalResolveEntry.Split, renderer.SplitTargetsAllocated);
                    if (entry is { } e) Assert.Equal(e, renderer.LastEntry);
                }
                if (vulkan is null) continue;
                readings.Add((vulkan.Memory.LiveDeviceAllocations, vulkan.Retired.Count));
                output.WriteLine($"  after cycle {c + 1}: {readings[^1].Live} live device allocations, "
                    + $"{readings[^1].Retired} held destroys");
            }
            scene.TemporalResolveEntryForTests = null;

            if (vulkan is null)
            {
                output.WriteLine("  no live allocation reading on this backend, the split's targets checked only");
                return;
            }
            // The live count is the memory itself. The retire list is printed only: the fixture presents every frame,
            // which drains it, so after a cycle it is near empty and bounds nothing the live count does not.
            for (int c = 1; c < readings.Count; c++)
                Assert.True(readings[c].Live <= readings[0].Live,
                    $"cycle {c + 1} ended on {readings[c].Live} live device allocations against {readings[0].Live} "
                    + "after the first");
        }

        static readonly Color BoxTint = new(0.8f, 0.6f, 0.4f, 1f);

        // The flat wall and four keyed boxes drifting right, enough motion for every resolve path to run.
        static Action<Scene3D, int> Draw(FrontStage stage) => (s, n) =>
        {
            stage.Wall(s);
            for (int i = 0; i < 4; i++)
                s.Draw(new RigidInstanceDraw(stage.Box,
                    Matrix4x4.CreateTranslation(-1f + 0.5f * i + 0.01f * n, 0.3f * (i % 2), 0f))
                { Tint = BoxTint, Motion = MotionKey.From((ulong)(100 + i)) });
        };
    }
}
