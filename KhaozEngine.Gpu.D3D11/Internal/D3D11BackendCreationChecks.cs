using System;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.Gpu.D3D11.Internal;

/// <summary>Value-only prerequisites of native D3D11 device creation.</summary>
internal static class D3D11BackendCreationChecks
{
    internal static void RequireWindowHandleForTests(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero) return;
        throw new D3D11BackendCreationArgumentException(D3D11BackendCreationRefusal.WindowHandleMissing,
            "The native Direct3D 11 backend was asked for a windowed device with a null window handle. A "
            + "swapchain needs an HWND to present into, and the windowing layer supplies one through "
            + "GpuWindowHandle.", nameof(hwnd)) { ActualWindowKind = GpuWindowKind.Win32 };
    }

    [DoesNotReturn]
    internal static void RefuseRuntimeContextForTests(string requiredInterface)
        => throw new D3D11BackendCreationUnsupportedException(D3D11BackendCreationRefusal.RuntimeContextUnavailable,
            "The native Direct3D 11 backend needs ID3D11DeviceContext1 and this runtime does not "
            + "offer it. Every constant-buffer bind goes through *SetConstantBuffers1 with an "
            + "explicit first constant and constant count, which exists only on the versioned "
            + "context. There is no second Direct3D 11 path to fall back to on this machine: "
            + "GpuBackendKind.Direct3D11 named the Veldrid backend and was retired in 18.0.0.")
        { RequiredInterface = requiredInterface };

    [DoesNotReturn]
    internal static void RefuseNativeObjectsForTests(bool deviceReturned, bool contextReturned)
        => throw new D3D11BackendCreationStateException(D3D11BackendCreationRefusal.NativeObjectsMissing,
            "D3D11CreateDevice reported success and handed back no device. The native Direct3D 11 "
            + "backend cannot run on this machine, and GpuBackendKind.Direct3D11 is not a fallback: "
            + "it named the Veldrid backend and was retired in 18.0.0.")
        { DeviceReturned = deviceReturned, ContextReturned = contextReturned };
}
