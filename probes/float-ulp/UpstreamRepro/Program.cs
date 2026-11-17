using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

// Minimal repro: an optimized compile on a thread whose floating point controls are changed keeps the
// changed result of constant folding in the machine code, which every thread then runs.
// macOS and Linux (glibc), arm64 and x64.
static class Repro
{
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    struct Env
    {
        [FieldOffset(0)] public uint LinuxArm64Fpcr;
        [FieldOffset(8)] public ulong MacArm64Fpcr;
        [FieldOffset(28)] public uint LinuxX64Mxcsr;
    }

    [DllImport("libSystem.dylib", EntryPoint = "fegetenv")] static extern int MacGet(ref Env e);
    [DllImport("libSystem.dylib", EntryPoint = "fesetenv")] static extern int MacSet(ref Env e);
    [DllImport("libm.so.6", EntryPoint = "fegetenv")] static extern int LinuxGet(ref Env e);
    [DllImport("libm.so.6", EntryPoint = "fesetenv")] static extern int LinuxSet(ref Env e);

    static readonly bool Mac = OperatingSystem.IsMacOS();
    static readonly bool Arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    static Env Get() { Env e = default; _ = Mac ? MacGet(ref e) : LinuxGet(ref e); return e; }
    static void Set(Env e) { _ = Mac ? MacSet(ref e) : LinuxSet(ref e); }

    static Env With(Env e, uint arm64Bits, uint x64Bits)
    {
        if (Arm && Mac) e.MacArm64Fpcr |= arm64Bits;
        else if (Arm) e.LinuxArm64Fpcr |= arm64Bits;
        else e.LinuxX64Mxcsr |= x64Bits;
        return e;
    }

    // Each pair is identical code. The first of each pair is compiled with a changed control, the second at the default.
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)] static float EpsilonUnderFlush() => float.Epsilon;
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)] static float EpsilonAtDefault() => float.Epsilon;
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)] static float BitDecrementUnderFlush(float x) => MathF.BitDecrement(x);
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)] static float BitDecrementAtDefault(float x) => MathF.BitDecrement(x);
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)] static float ThirdUnderRoundDown() => Divide(1f, 3f);
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)] static float ThirdAtDefault() => Divide(1f, 3f);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static float Divide(float a, float b) => a / b;

    static float s_zero = 0f;
    static string Bits(float f) => "0x" + BitConverter.SingleToUInt32Bits(f).ToString("X8");

    static void Main()
    {
        Env saved = Get();

        Set(With(saved, arm64Bits: 0x1000000, x64Bits: 0x8040));   // arm64 FZ, x64 FTZ and DAZ
        EpsilonUnderFlush();
        BitDecrementUnderFlush(s_zero);
        Set(saved);

        Set(With(saved, arm64Bits: 0x800000, x64Bits: 0x2000));    // round toward minus infinity
        ThirdUnderRoundDown();
        Set(saved);

        EpsilonAtDefault();
        BitDecrementAtDefault(s_zero);
        ThirdAtDefault();

        // Read every result on a new thread that never changed its controls.
        var reader = new Thread(() =>
        {
            Console.WriteLine($"{RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture} {RuntimeInformation.FrameworkDescription}");
            Console.WriteLine($"float.Epsilon           compiled under flush: {Bits(EpsilonUnderFlush())}   at default: {Bits(EpsilonAtDefault())}");
            Console.WriteLine($"MathF.BitDecrement(0f)  compiled under flush: {Bits(BitDecrementUnderFlush(s_zero))}   at default: {Bits(BitDecrementAtDefault(s_zero))}");
            Console.WriteLine($"1f / 3f (inlined)       compiled rounding down: {Bits(ThirdUnderRoundDown())} at default: {Bits(ThirdAtDefault())}");
        });
        reader.Start();
        reader.Join();
    }
}
