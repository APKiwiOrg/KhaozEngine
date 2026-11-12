using System;
using System.Runtime.InteropServices;

namespace KhaozEngine.TileWorld.Physics;

// The same platform fenv facility used by DeterministicFp, without a package dependency or a
// canonical scope. Reading native controls avoids arithmetic probes changing sticky FP flags.
internal static class TileWaterFloatingPointState
{
    enum Platform { None, MacArm, MacX64, LinuxArm, LinuxX64, WindowsX64 }
    static readonly Platform s_platform = Probe();

    // Inline storage accommodates the known fenv_t ABIs without native allocation. Controls key
    // the arithmetic. Sticky status is read separately for the monotone replay eligibility check.
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    struct EnvironmentBuffer
    {
        [FieldOffset(0)] internal uint First;
        [FieldOffset(2)] internal ushort MacX87Status;
        [FieldOffset(4)] internal uint MacMxcsr;
        [FieldOffset(4)] internal uint LinuxFpStatus;
        [FieldOffset(4)] internal ushort LinuxX87Status;
        [FieldOffset(8)] internal ulong MacFpcr;
        [FieldOffset(28)] internal uint LinuxMxcsr;
    }

    internal static bool TryRead(out ulong token) => Read(s_platform, out token, out _);

    internal static bool TryRead(out ulong token, out ulong status)
    {
        // The existing Windows reader exposes controls only. Its separate CRT status API checks
        // pending unmasked exceptions, so it is not a side-effect-free substitute for fegetenv.
        // Keep control-only reads available, but conservatively bypass result replay on Windows.
        if (s_platform == Platform.WindowsX64)
        {
            token = status = 0;
            return false;
        }
        return Read(s_platform, out token, out status);
    }

    static bool Read(Platform platform, out ulong token, out ulong status)
    {
        token = status = 0;
        if (platform == Platform.None) return false;
        if (platform == Platform.WindowsX64)
        {
            // With a zero mask this only reads. The CRT control word distinguishes all four
            // denormal operand/result combinations and all four rounding directions.
            if (ControlFp(out uint controls, 0, 0) != 0) return false;
            token = controls;
            return true;
        }
        EnvironmentBuffer buffer = default;
        int result = platform is Platform.MacArm or Platform.MacX64
            ? MacGetEnv(ref buffer) : LinuxGetEnv(ref buffer);
        if (result != 0) return false;
        token = platform switch
        {
            // Retain all FPCR controls, including RMode, FZ and the newer independent FIZ flag.
            Platform.MacArm => buffer.MacFpcr,
            Platform.LinuxArm => buffer.First,
            // SSE rounding, DAZ and FTZ are distinct bits. Preserve x87 controls too, since
            // platform math implementations may use them. Drop only SSE sticky status bits.
            Platform.MacX64 => ((ulong)(buffer.First & 0xffff) << 32) | (buffer.MacMxcsr & ~0x3fu),
            _ => ((ulong)(buffer.First & 0xffff) << 32) | (buffer.LinuxMxcsr & ~0x3fu),
        };
        status = platform switch
        {
            // ARM's cumulative exception and saturation flags. Condition codes are not sticky.
            Platform.MacArm => buffer.First & 0x0800009fu,
            Platform.LinuxArm => buffer.LinuxFpStatus & 0x0800009fu,
            // Keep x87 and SSE exception flags distinct. Stack position and condition codes
            // are not part of this monotone status set.
            Platform.MacX64 => ((ulong)(buffer.MacX87Status & 0x3f) << 32) | (buffer.MacMxcsr & 0x3fu),
            _ => ((ulong)(buffer.LinuxX87Status & 0x3f) << 32) | (buffer.LinuxMxcsr & 0x3fu),
        };
        return true;
    }

    static Platform Probe()
    {
        Architecture arch = RuntimeInformation.ProcessArchitecture;
        Platform platform = OperatingSystem.IsMacOS()
            ? arch == Architecture.Arm64 ? Platform.MacArm : arch == Architecture.X64 ? Platform.MacX64 : Platform.None
            : OperatingSystem.IsLinux()
                ? arch == Architecture.Arm64 ? Platform.LinuxArm : arch == Architecture.X64 ? Platform.LinuxX64 : Platform.None
                : OperatingSystem.IsWindows() && arch == Architecture.X64 ? Platform.WindowsX64 : Platform.None;
        try { return Read(platform, out _, out _) ? platform : Platform.None; }
        catch (DllNotFoundException) { return Platform.None; }
        catch (EntryPointNotFoundException) { return Platform.None; }
        catch (BadImageFormatException) { return Platform.None; }
    }

    [DllImport("libSystem.dylib", EntryPoint = "fegetenv")]
    static extern int MacGetEnv(ref EnvironmentBuffer buffer);
    [DllImport("libm.so.6", EntryPoint = "fegetenv")]
    static extern int LinuxGetEnv(ref EnvironmentBuffer buffer);
    [DllImport("ucrtbase.dll", EntryPoint = "_controlfp_s")]
    static extern int ControlFp(out uint current, uint value, uint mask);
}
