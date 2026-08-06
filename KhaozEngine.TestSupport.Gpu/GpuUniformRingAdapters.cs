using System;
using System.Collections.Generic;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE TWO ADAPTERS, BY NAME, so one <c>[Theory]</c> runs section 9.4's seven shared rows against BOTH
    /// backends' rings (V-P5, V-T6).
    /// <para>
    /// THE ROWS TAKE A NAME RATHER THAN AN INSTANCE, because xUnit member data has to be serialisable for a test
    /// case to be discovered, resumed and reported individually, and a live ring holding a pinned array is not.
    /// The name also makes the failure readable: a shared assertion that fires says which backend it fired on,
    /// which is the whole reason <see cref="IGpuUniformRingUnderTest.BackendName"/> exists.
    /// </para>
    /// <para>
    /// BOTH ADAPTERS RUN DEVICE-FREE AND NEITHER SKIPS. The Direct3D 11 half drives a pinned array behind
    /// <c>ID3D11RingMemory</c> and names no Direct3D type, and the Vulkan half drives one behind
    /// <c>IVulkanTimelineSemaphore</c> and names no Silk.NET type, so the shared rows are ordinary
    /// <c>[Theory]</c> cases on macOS, Linux and Windows alike. A shared row that could skip on one side is a
    /// shared row that quietly became one backend's, which is exactly the outcome V-P5 exists to prevent.
    /// </para>
    /// </summary>
    internal static class GpuUniformRingAdapters
    {
        /// <summary>The Direct3D 11 adapter's name, as it appears in a test case's display name.</summary>
        internal const string Direct3D11 = "Direct3D11Native";

        /// <summary>The native Vulkan adapter's name.</summary>
        internal const string Vulkan = "VulkanNative";

        /// <summary>Every adapter, as xUnit member data. One row per backend, and adding a third backend's ring is
        /// one entry here plus one adapter beside the two that exist.</summary>
        internal static IEnumerable<object[]> All()
        {
            yield return new object[] { Direct3D11 };
            yield return new object[] { Vulkan };
        }

        /// <summary>
        /// Build one adapter over a fresh ring.
        /// </summary>
        /// <param name="backend">One of <see cref="Direct3D11"/> or <see cref="Vulkan"/>.</param>
        /// <param name="sizeInBytes">The buffer's logical size.</param>
        /// <param name="framesInFlight">How many segments to cut it into. Three is both backends' default, and
        /// both accept it.</param>
        internal static IGpuUniformRingUnderTest Create(string backend, uint sizeInBytes = 256,
            int framesInFlight = 3)
            => backend switch
            {
                Direct3D11 => new D3D11UniformRingAdapter(sizeInBytes, framesInFlight),
                Vulkan => new VulkanUniformRingAdapter(sizeInBytes, framesInFlight),
                _ => throw new ArgumentOutOfRangeException(nameof(backend), backend,
                    "No uniform-ring adapter is registered under that name. The shared ring rows run against "
                    + $"'{Direct3D11}' and '{Vulkan}'."),
            };
    }
}
