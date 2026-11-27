using System;
using KhaozEngine.Gpu.Core.Interop;

namespace KhaozEngine.Tests.GpuCore;

internal static class NativeImportProbe
{
    internal const string StaleAbiArgument = "--check-stale-native-abi";
    private const uint StaleAbi = 999;

    private static int Main(string[] args)
    {
        if (args is not [StaleAbiArgument])
            return 2;

        try
        {
            uint actualAbi = KhaozNative.khaoz_native_abi_version();
            Console.Error.WriteLine($"The first generated import reached the native library and returned ABI {actualAbi}.");
            return 1;
        }
        catch (InvalidOperationException)
        {
            // Check the fixture after the rejected import so the import runs without an earlier probe.
            // A missing or unloadable fixture must fail instead of passing as a stale ABI.
            NativeLoadStatus status = NativeLibraryGate.Probe(expectedAbi: StaleAbi);
            if (status == NativeLoadStatus.Ready)
                return 0;

            Console.Error.WriteLine($"Could not load the stale ABI fixture with ABI {StaleAbi}: {status}.");
            return 1;
        }
    }
}
