using System;
using System.Runtime.InteropServices;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

// Test-owned native scope. Production never sets a caller's FP state. This half reads the controls, restores the
// captured ones and returns to the default. Every other mode is in WaterFloatingPointEnvironment.Modes.cs, which only
// test assemblies whose tests change controls compile. Compiled code and created threads outlive a changed control,
// so an assembly whose tests assume the default environment cannot set one.
internal sealed partial class WaterFloatingPointEnvironment : IDisposable
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

    /// <summary>The rounding and flush controls the mode half's <c>Set</c> can change on the calling thread. Zero is
    /// the default environment. Null where no native reader exists.</summary>
    internal static ulong? CurrentModes() => Capture()?.Modes();

    ulong? Modes()
    {
        if (_windows) return WindowsControl(out uint controls, 0, 0) == 0 ? controls & 0x03000300u : null;
        if (_arm) return (_mac ? _saved.MacFpcr : _saved.First) & 0x01c00000ul;
        uint mxcsr = _mac ? _saved.MacMxcsr : _saved.LinuxMxcsr;
        return (mxcsr & 0xe040u) | ((ulong)(_saved.First & 0xc00u) << 32);
    }

    /// <summary>Round to nearest with no flush, the default environment.</summary>
    internal bool SetNearest() => Apply(windows: 0, arm: 0, mxcsr: 0, x87: 0);

    /// <summary>The control change a shared test body receives in an assembly whose tests assume the default
    /// environment. It returns to the default controls and refuses every other mode.</summary>
    internal static bool NearestOnly(WaterFloatingPointEnvironment environment, string mode) =>
        mode == "nearest" ? environment.SetNearest() : throw new InvalidOperationException(
            $"Mode {mode} changes a floating point control, so its test runs in KhaozEngine.TileWorld.Physics.FloatingPoint.Tests.");

    // Writes the given rounding and flush bits over the saved environment. Only this half's default and the mode
    // half compiled into the floating point test assemblies pass nonzero bits.
    bool Apply(uint windows, ulong arm, uint mxcsr, uint x87)
    {
        if (_windows) return WindowsControl(out _, windows, 0x03000300) == 0;
        Buffer buffer = _saved;
        if (_arm)
        {
            ulong controls = ((_mac ? buffer.MacFpcr : buffer.First) & ~0x01c00000ul) | arm;
            if (_mac) buffer.MacFpcr = controls;
            else buffer.First = (uint)controls;
        }
        else
        {
            uint controls = ((_mac ? buffer.MacMxcsr : buffer.LinuxMxcsr) & ~0xe040u) | mxcsr;
            if (_mac) buffer.MacMxcsr = controls;
            else buffer.LinuxMxcsr = controls;
            buffer.First = (buffer.First & ~0xc00u) | x87;
        }
        return (_mac ? MacSet(ref buffer) : LinuxSet(ref buffer)) == 0;
    }

    internal void Restore()
    {
        Buffer buffer = _saved;
        int status = _mac ? MacSet(ref buffer) : _windows ? WindowsSet(ref buffer) : LinuxSet(ref buffer);
        if (status != 0) throw new InvalidOperationException("Could not restore the test floating point environment.");
    }

    public void Dispose() => Restore();

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
