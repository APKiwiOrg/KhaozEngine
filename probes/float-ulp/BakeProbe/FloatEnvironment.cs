using System;
using System.Runtime.InteropServices;

// Thread floating point controls through the platform C library, as the #1830 probes did.
// arm64: FPCR (FZ bit 24, RMode bits 22 and 23). x64: MXCSR (FTZ 0x8000, DAZ 0x40, RC bits 13 and 14).
static class FloatEnvironment
{
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    struct Env
    {
        [FieldOffset(0)] public uint LinuxArmFpcr;
        [FieldOffset(8)] public ulong MacFpcr;
        [FieldOffset(28)] public uint LinuxMxcsr;
    }

    [DllImport("libSystem.dylib", EntryPoint = "fegetenv")] static extern int MacGet(ref Env e);
    [DllImport("libSystem.dylib", EntryPoint = "fesetenv")] static extern int MacSet(ref Env e);
    [DllImport("libm.so.6", EntryPoint = "fegetenv")] static extern int LinuxGet(ref Env e);
    [DllImport("libm.so.6", EntryPoint = "fesetenv")] static extern int LinuxSet(ref Env e);

    public static readonly bool Mac = OperatingSystem.IsMacOS();
    public static readonly bool Arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    public sealed record Control(string Name, ulong Set, ulong Clear);

    // Every control the platform can change separately. Index 0 is the IEEE default.
    public static Control[] FlushControls => Arm
        ? new[] { new Control("default", 0, 0), new Control("FZ", 0x1000000, 0) }
        : new[]
        {
            new Control("default", 0, 0), new Control("FTZ", 0x8000, 0), new Control("DAZ", 0x40, 0),
            new Control("FTZ+DAZ", 0x8040, 0),
        };

    public static Control[] RoundingAndFlushControls => Arm
        ? new[]
        {
            new Control("default", 0, 0), new Control("FZ", 0x1000000, 0), new Control("RP", 0x400000, 0xC00000),
            new Control("RM", 0x800000, 0xC00000), new Control("RZ", 0xC00000, 0xC00000),
        }
        : new[]
        {
            new Control("default", 0, 0), new Control("FTZ", 0x8000, 0), new Control("DAZ", 0x40, 0),
            new Control("FTZ+DAZ", 0x8040, 0), new Control("RD", 0x2000, 0x6000), new Control("RU", 0x4000, 0x6000),
            new Control("RZ", 0x6000, 0x6000),
        };

    static Env Get() { Env e = default; _ = Mac ? MacGet(ref e) : LinuxGet(ref e); return e; }
    static void Put(Env e) { _ = Mac ? MacSet(ref e) : LinuxSet(ref e); }

    public static ulong Current
    {
        get
        {
            Env e = Get();
            return Arm ? (Mac ? e.MacFpcr : e.LinuxArmFpcr) : e.LinuxMxcsr;
        }
    }

    static Env s_saved;

    public static void Save() => s_saved = Get();

    // Applies a control on top of the saved default and returns the control word read back.
    public static ulong Apply(Control control)
    {
        Env e = s_saved;
        if (Arm)
        {
            if (Mac) e.MacFpcr = (e.MacFpcr & ~control.Clear) | control.Set;
            else e.LinuxArmFpcr = (uint)((e.LinuxArmFpcr & ~control.Clear) | control.Set);
        }
        else
        {
            e.LinuxMxcsr = (uint)((e.LinuxMxcsr & ~control.Clear) | control.Set);
        }
        Put(e);
        return Current;
    }

    public static void Restore() => Put(s_saved);
}
