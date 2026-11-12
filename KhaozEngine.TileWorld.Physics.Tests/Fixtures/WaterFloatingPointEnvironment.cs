using System;
using System.Runtime.InteropServices;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

// Test-owned native scope. Production never sets a caller's FP state.
internal sealed class WaterFloatingPointEnvironment : IDisposable
{
    readonly Buffer _saved;
    readonly bool _mac, _windows, _arm;

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    struct Buffer
    {
        [FieldOffset(0)] internal uint First;
        [FieldOffset(4)] internal uint MacMxcsr;
        [FieldOffset(8)] internal ulong MacFpcr;
        [FieldOffset(28)] internal uint LinuxMxcsr;
    }

    WaterFloatingPointEnvironment(Buffer saved)
    {
        _saved = saved;
        _mac = OperatingSystem.IsMacOS();
        _windows = OperatingSystem.IsWindows();
        _arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
    }

    internal static WaterFloatingPointEnvironment? Capture()
    {
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.Arm64 or Architecture.X64)) return null;
        try
        {
            Buffer buffer = default;
            int status = OperatingSystem.IsMacOS() ? MacGet(ref buffer) :
                OperatingSystem.IsLinux() ? LinuxGet(ref buffer) : OperatingSystem.IsWindows() ? WindowsGet(ref buffer) : -1;
            return status == 0 ? new(buffer) : null;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    internal bool Set(string mode)
    {
        if (_windows)
        {
            uint value = mode switch
            {
                "up" => 0x200,
                "down" => 0x100,
                "zero" => 0x300,
                "flush-output" => 0x03000000,
                "flush-input" => 0x02000000,
                _ => 0,
            };
            return WindowsControl(out _, value, 0x03000300) == 0;
        }
        Buffer buffer = _saved;
        if (_arm)
        {
            // ARM's FZ controls both operands and results. Independent FIZ is optional on newer
            // cores, so the input-only case is exercised on x64 where DAZ is independent of FTZ.
            if (mode == "flush-input") return false;
            ulong controls = (_mac ? buffer.MacFpcr : buffer.First) & ~0x01c00000ul;
            controls |= mode switch
            {
                "up" => 0x400000ul,
                "down" => 0x800000ul,
                "zero" => 0xc00000ul,
                "flush-output" => 0x1000000ul,
                _ => 0ul,
            };
            if (_mac) buffer.MacFpcr = controls;
            else buffer.First = (uint)controls;
        }
        else
        {
            uint controls = (_mac ? buffer.MacMxcsr : buffer.LinuxMxcsr) & ~0xe040u;
            controls |= mode switch
            {
                "up" => 0x4000u,
                "down" => 0x2000u,
                "zero" => 0x6000u,
                "flush-output" => 0x8000u,
                "flush-input" => 0x40u,
                _ => 0u,
            };
            if (_mac) buffer.MacMxcsr = controls;
            else buffer.LinuxMxcsr = controls;
            buffer.First = (buffer.First & ~0xc00u) | (mode switch
            {
                "up" => 0x800u,
                "down" => 0x400u,
                "zero" => 0xc00u,
                _ => 0u,
            });
        }
        return (_mac ? MacSet(ref buffer) : LinuxSet(ref buffer)) == 0;
    }

    public void Dispose()
    {
        Buffer buffer = _saved;
        int status = _mac ? MacSet(ref buffer) : _windows ? WindowsSet(ref buffer) : LinuxSet(ref buffer);
        if (status != 0) throw new InvalidOperationException("Could not restore the test floating point environment.");
    }

    [DllImport("libSystem.dylib", EntryPoint = "fegetenv")]
    static extern int MacGet(ref Buffer buffer);
    [DllImport("libSystem.dylib", EntryPoint = "fesetenv")]
    static extern int MacSet(ref Buffer buffer);
    [DllImport("libm.so.6", EntryPoint = "fegetenv")]
    static extern int LinuxGet(ref Buffer buffer);
    [DllImport("libm.so.6", EntryPoint = "fesetenv")]
    static extern int LinuxSet(ref Buffer buffer);
    [DllImport("ucrtbase.dll", EntryPoint = "fegetenv")]
    static extern int WindowsGet(ref Buffer buffer);
    [DllImport("ucrtbase.dll", EntryPoint = "fesetenv")]
    static extern int WindowsSet(ref Buffer buffer);
    [DllImport("ucrtbase.dll", EntryPoint = "_controlfp_s")]
    static extern int WindowsControl(out uint current, uint value, uint mask);
}
