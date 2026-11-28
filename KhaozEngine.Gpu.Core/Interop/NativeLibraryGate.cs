using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace KhaozEngine.Gpu.Core.Interop;

/// <summary>Whether the native core can be loaded and speaks the expected ABI.</summary>
public enum NativeLoadStatus
{
    Ready,
    NotFound,
    AbiMismatch,
}

/// <summary>Checks the native core ABI before any generated import can bind.</summary>
public static class NativeLibraryGate
{
    private const string LibraryName = "khaoz_native";
    private const uint ExpectedAbi = KhaozNative.KHAOZ_NATIVE_ABI_VERSION;

    private static readonly Lock ImportLock = new();
    private static nint _importLibrary;

    /// <summary>
    /// Checks a library name or path. An unloadable library reports <see cref="NativeLoadStatus.NotFound"/>.
    /// An absent version export or a different version reports <see cref="NativeLoadStatus.AbiMismatch"/>.
    /// </summary>
    public static NativeLoadStatus Probe(string libraryName = LibraryName, uint expectedAbi = ExpectedAbi)
    {
        NativeLoadStatus status = TryLoad(libraryName, expectedAbi, typeof(NativeLibraryGate).Assembly, null, out nint library);
        if (status == NativeLoadStatus.Ready)
            NativeLibrary.Free(library);

        return status;
    }

    // Register before the first generated P/Invoke, including callers that omit the diagnostic probe.
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void RegisterResolver()
        => NativeLibrary.SetDllImportResolver(typeof(NativeLibraryGate).Assembly, ResolveImport);
#pragma warning restore CA2255

    private static nint ResolveImport(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibraryName)
            return 0;

        lock (ImportLock)
        {
            if (_importLibrary == 0)
            {
                NativeLoadStatus status = TryLoad(libraryName, ExpectedAbi, assembly, searchPath, out _importLibrary);
                if (status != NativeLoadStatus.Ready)
                {
                    // Returning zero would let the runtime bind to the unchecked library.
                    throw new InvalidOperationException($"The native core library cannot bind imports ({status}).");
                }
            }

            // P/Invoke stubs cache their entry points, so keep this verified handle for their lifetime.
            return _importLibrary;
        }
    }

    private static unsafe NativeLoadStatus TryLoad(
        string libraryName, uint expectedAbi, Assembly assembly, DllImportSearchPath? searchPath, out nint library)
    {
        if (!NativeLibrary.TryLoad(libraryName, assembly, searchPath, out library))
            return NativeLoadStatus.NotFound;

        if (NativeLibrary.TryGetExport(library, "khaoz_native_abi_version", out nint version)
            && ((delegate* unmanaged[Cdecl]<uint>)version)() == expectedAbi)
            return NativeLoadStatus.Ready;

        NativeLibrary.Free(library);
        library = 0;
        return NativeLoadStatus.AbiMismatch;
    }
}
