using KhaozEngine.Gpu.Core.Interop;
using Xunit;

namespace KhaozEngine.Tests.GpuCore;

[Collection("CoreBackend")]
public sealed class NativeLibraryGateTests
{
    [NativeFact]
    public void Probe_reports_ready_for_the_built_library()
        => Assert.Equal(NativeLoadStatus.Ready, NativeLibraryGate.Probe());

    [NativeFact]
    public void Probe_reports_not_found_for_a_missing_library()
        => Assert.Equal(NativeLoadStatus.NotFound, NativeLibraryGate.Probe("khaoz_native_absent"));

    [NativeFact]
    public void Probe_reports_abi_mismatch()
        => Assert.Equal(NativeLoadStatus.AbiMismatch, NativeLibraryGate.Probe(expectedAbi: 999));
}
