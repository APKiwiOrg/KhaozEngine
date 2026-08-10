using System;
using System.Collections.Generic;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE THREE ADAPTERS, BY NAME, so one <c>[Theory]</c> runs section 9.4's seven shared rows against EVERY
    /// engine-owned backend's ring (V-P5, V-T6, and M-P5 and M-T5 for the third).
    /// <para>
    /// THE ROWS TAKE A NAME RATHER THAN AN INSTANCE, because xUnit member data has to be serialisable for a test
    /// case to be discovered, resumed and reported individually, and a live ring holding a pinned array is not.
    /// The name also makes the failure readable: a shared assertion that fires says which backend it fired on,
    /// which is the whole reason <see cref="IGpuUniformRingUnderTest.BackendName"/> exists.
    /// </para>
    /// <para>
    /// EVERY ADAPTER RUNS DEVICE-FREE AND NONE SKIPS. The Direct3D 11 half drives a pinned array behind
    /// <c>ID3D11RingMemory</c> and names no Direct3D type, the Vulkan half drives one behind
    /// <c>IVulkanTimelineSemaphore</c> and names no Silk.NET type, and the Metal half drives one behind
    /// <c>IMetalSharedEvent</c> and sends no Objective-C message, so the shared rows are ordinary
    /// <c>[Theory]</c> cases on macOS, Linux and Windows alike. A shared row that could skip on one side is a
    /// shared row that quietly became one backend's, which is exactly the outcome V-P5 exists to prevent.
    /// </para>
    /// <para>
    /// AND THE THIRD ADAPTER IS THE POINT OF M-P5 RATHER THAN AN ADDITION TO IT. The three rings deliberately do
    /// NOT share code, on the rule of three and because the MECHANISM genuinely differs (a map lifecycle, a
    /// persistently mapped chunk, a Shared buffer's <c>contents()</c>). What is shared is the POLICY, and a
    /// policy asserted against two of three implementations is a policy the third can drift away from silently.
    /// </para>
    /// </summary>
    internal static class GpuUniformRingAdapters
    {
        /// <summary>The Direct3D 11 adapter's name, as it appears in a test case's display name.</summary>
        internal const string Direct3D11 = "Direct3D11Native";

        /// <summary>The native Vulkan adapter's name.</summary>
        internal const string Vulkan = "VulkanNative";

        /// <summary>The native Metal adapter's name.</summary>
        internal const string Metal = "MetalNative";

        /// <summary>Every adapter, as xUnit member data. One row per backend, and adding another backend's ring
        /// is one entry here plus one adapter beside the three that exist.</summary>
        internal static IEnumerable<object[]> All()
        {
            yield return new object[] { Direct3D11 };
            yield return new object[] { Vulkan };
            yield return new object[] { Metal };
        }

        /// <summary>
        /// Build one adapter over a fresh ring.
        /// </summary>
        /// <param name="backend">One of <see cref="Direct3D11"/>, <see cref="Vulkan"/> or
        /// <see cref="Metal"/>.</param>
        /// <param name="sizeInBytes">The buffer's logical size.</param>
        /// <param name="framesInFlight">How many segments to cut it into. Three is every backend's default, and
        /// all three accept it.</param>
        internal static IGpuUniformRingUnderTest Create(string backend, uint sizeInBytes = 256,
            int framesInFlight = 3)
            => backend switch
            {
                Direct3D11 => new D3D11UniformRingAdapter(sizeInBytes, framesInFlight),
                Vulkan => new VulkanUniformRingAdapter(sizeInBytes, framesInFlight),
                Metal => new MetalUniformRingAdapter(sizeInBytes, framesInFlight),
                _ => throw new ArgumentOutOfRangeException(nameof(backend), backend,
                    "No uniform-ring adapter is registered under that name. The shared ring rows run against "
                    + $"'{Direct3D11}', '{Vulkan}' and '{Metal}'."),
            };
    }
}
