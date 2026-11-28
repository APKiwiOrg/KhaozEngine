using System;
using KhaozEngine.Gpu.TestKit;
using Xunit;

namespace KhaozEngine.Tests.GpuCore;

public sealed class NativeFactAttribute : FactAttribute
{
    public NativeFactAttribute() => Skip = SkipReason();

    internal static string? SkipReason()
        => Environment.GetEnvironmentVariable("KE_NATIVE_TESTS") == "1"
            ? null
            : "Set KE_NATIVE_TESTS=1 to run native core tests.";
}

public sealed class NativeGpuFactAttribute : FactAttribute
{
    public NativeGpuFactAttribute() => Skip = NativeFactAttribute.SkipReason() ?? GpuTestGate.SkipReason();

    public bool RequireManagedVulkan
    {
        get;
        set
        {
            field = value;
            if (value && OperatingSystem.IsMacOS())
                Skip = "The managed Vulkan backend does not run on MoltenVK (issue 539).";
        }
    }
}
