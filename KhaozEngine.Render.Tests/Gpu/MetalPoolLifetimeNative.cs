using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Tests.Gpu;

// A real NSObject subclass with a dealloc witness. No retain-count or freed-pointer reads.
[SupportedOSPlatform("macos")]
internal static unsafe partial class MetalPoolLifetimeNative
{
    [ThreadStatic] internal static int Deallocated;
    static readonly IntPtr Class = Register();
    static readonly IntPtr Superclass = ObjCRuntime.ClassNamed("NSObject");
    static readonly IntPtr DeallocSelector = ObjCRuntime.Sel("dealloc");

    internal static IntPtr Autoreleased()
    {
        IntPtr obj = ObjCMsgSend.Send(Class, ObjCRuntime.Sel("new"));
        if (obj == IntPtr.Zero) throw new InvalidOperationException("NSObject allocation failed.");
        return ObjCMsgSend.Send(obj, ObjCRuntime.Sel("autorelease"));
    }

    static IntPtr Register()
    {
        NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation");
        IntPtr type;
        byte[] name = System.Text.Encoding.ASCII.GetBytes("KEPoolProbe_" + Guid.NewGuid().ToString("N") + "\0");
        fixed (byte* p = name) type = AllocateClass(ObjCRuntime.ClassNamed("NSObject"), p, 0);
        if (type == IntPtr.Zero) throw new InvalidOperationException("Probe class allocation failed.");
        fixed (byte* encoding = "v@:\0"u8)
        {
            if (AddMethod(type, ObjCRuntime.Sel("dealloc"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&Dealloc, encoding) == 0)
                throw new InvalidOperationException("Probe dealloc registration failed.");
        }
        RegisterClass(type);
        return type;
    }

    [UnmanagedCallersOnly]
    static void Dealloc(IntPtr obj, IntPtr selector)
    {
        Deallocated++;
        var parent = new Super { Receiver = obj, Class = Superclass };
        SendSuper(ref parent, DeallocSelector);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Super { internal IntPtr Receiver, Class; }

    [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_allocateClassPair")]
    private static partial IntPtr AllocateClass(IntPtr superclass, byte* name, nuint size);
    [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_registerClassPair")]
    private static partial void RegisterClass(IntPtr type);
    [LibraryImport(ObjCRuntime.Objc, EntryPoint = "class_addMethod")]
    private static partial byte AddMethod(IntPtr type, IntPtr selector, IntPtr implementation, byte* encoding);
    [LibraryImport(ObjCRuntime.Objc, EntryPoint = "objc_msgSendSuper")]
    private static partial void SendSuper(ref Super parent, IntPtr selector);
}
