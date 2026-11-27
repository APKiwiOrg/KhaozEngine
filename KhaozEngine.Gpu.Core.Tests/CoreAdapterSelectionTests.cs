using System;
using System.Runtime.InteropServices;
using System.Text;
using KhaozEngine.Gpu.Core.Internal;
using KhaozEngine.Gpu.Core.Interop;
using Xunit;

namespace KhaozEngine.Tests.GpuCore;

public sealed class CoreAdapterSelectionTests
{
    [Theory]
    [InlineData("\u2003ö\u2003", 1, false)]
    [InlineData("\u2003+1\u2003", 1, false)]
    [InlineData("+-0", 0, true)]
    [InlineData("unknown adapter", 0, true)]
    [InlineData("unnamed device 0x0001020b", 2, false)]
    public void Abi_candidates_use_managed_environment_request_rules(string request, int expected, bool warns)
    {
        khaoz_gpu_adapter_info[] candidates =
            [Candidate("First"), Candidate("GeForce Ö"), Candidate("\u2003 \t", 0x1020b)];

        Assert.Equal(expected, CoreAdapterSelection.Choose(request, candidates, out string? warning));
        Assert.Equal(warns, warning is not null);
    }

    private static khaoz_gpu_adapter_info Candidate(string name, uint deviceId = 0)
    {
        var candidate = new khaoz_gpu_adapter_info { device_id = deviceId };
        Span<sbyte> target = candidate.device_name;
        Encoding.UTF8.GetBytes(name, MemoryMarshal.AsBytes(target));
        return candidate;
    }
}
