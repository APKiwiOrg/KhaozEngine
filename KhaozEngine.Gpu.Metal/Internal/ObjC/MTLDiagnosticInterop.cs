using System;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC;

/// <summary>Cold queries only. NSUInteger is nuint and BOOL is byte in the existing LP64 bindings.</summary>
[SupportedOSPlatform("macos")]
internal static class MTLDiagnosticInterop
{
    internal static bool Responds(IntPtr receiver, string selector)
        => receiver != IntPtr.Zero && ObjCMsgSend.SendBoolPtr(receiver, ObjCRuntime.Sel("respondsToSelector:"),
            ObjCRuntime.Sel(selector)) != 0;

    internal static MetalComputeLimits ReadLimits(IntPtr state)
    {
        using var pool = ObjCAutoreleasePool.Enter();
        if (!Responds(state, "threadExecutionWidth") || !Responds(state, "maxTotalThreadsPerThreadgroup"))
            throw new NotSupportedException("The compute pipeline does not expose execution-width/threadgroup limits.");
        return new(ObjCMsgSend.SendNUInt(state, ObjCRuntime.Sel("threadExecutionWidth")),
            ObjCMsgSend.SendNUInt(state, ObjCRuntime.Sel("maxTotalThreadsPerThreadgroup")));
    }

    internal static MetalNativeMemorySnapshot ReadMemory(IntPtr device)
    {
        using var pool = ObjCAutoreleasePool.Enter();
        return MetalNativeMemoryQuery.Read(new DeviceMemoryNative(device));
    }

    readonly struct DeviceMemoryNative(IntPtr device) : IMetalDeviceMemoryNative
    {
        public bool HasAllocationQuery => Responds(device, "currentAllocatedSize");
        public bool HasUnifiedQuery => Responds(device, "hasUnifiedMemory");
        public nuint CurrentAllocatedSize => ObjCMsgSend.SendNUInt(device, ObjCRuntime.Sel("currentAllocatedSize"));
        public bool HasUnifiedMemory => ObjCMsgSend.SendBool(device, ObjCRuntime.Sel("hasUnifiedMemory")) != 0;
    }
}
