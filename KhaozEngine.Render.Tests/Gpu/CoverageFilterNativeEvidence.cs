using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

internal static class CoverageFilterNativeEvidence
{
    internal static void Capabilities(IGpuDevice device, ITestOutputHelper output)
    {
        var c = device.Capabilities;
        output.WriteLine($"FILTER CAPABILITIES backend={device.Backend} adapter={c.DeviceName} signedZeroInfNan32={c.SupportsShaderFloatControls} rte32={c.SupportsFloat32Rte} denormPreserve32={c.SupportsFloat32DenormPreserve} denormFlush32={c.SupportsFloat32DenormFlush}");
    }

    internal static IEnumerable<CoverageGpuFilterPath> Modes(IGpuDevice device, ITestOutputHelper output)
    {
        Capabilities(device, output);
        var c = device.Capabilities;
        if (CoverageGpuFilterPolicy.Resolve(device.Backend, c) == CoverageGpuFilterPath.Exact)
        {
            output.WriteLine("FILTER UNSUPPORTED admitted=false executedModes=0");
            yield break;
        }
        if (c.SupportsFloat32DenormPreserve) yield return CoverageGpuFilterPath.FilteredPreserve;
        if (c.SupportsFloat32DenormFlush) yield return CoverageGpuFilterPath.FilteredFlush;
    }

    internal static void Batch(IGpuDevice nativeDevice, CoverageGpuPipeline pipeline, CoverageGpuBatch batch, string workload, ITestOutputHelper output)
    {
        // Preserve queried bits when a test supplies a downgraded policy view to the pipeline.
        Capabilities(nativeDevice, output);
        output.WriteLine($"FILTER BATCH workload={workload} supported={pipeline.FilterPath} actual={pipeline.PathFor(batch)} capacity={batch.MaximumFilterSupportCount} actors={batch.Count}");
    }
}
