using System;
using System.Runtime.CompilerServices;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Numerics;

// #1832. The JIT folds a float constant through the flush mode of the thread that first optimizes the method, and
// that machine code serves every thread afterwards. The BCL steps hold -float.Epsilon and float.Epsilon in their
// zero branch, so a step first optimized under flush to zero returned a signed zero for every later caller. FloatUlp
// reads those bits from mutable statics. Each row compiles its own steps, fully optimized, while the thread has the
// control, then reads them back at the default controls. Compiling under a changed control outlives the test, which
// is why this lives in a floating point test assembly.
public sealed class FloatUlpBakeTests
{
    [Theory]
    [InlineData("flush-output")]
    [InlineData("flush-input")]
    public void StepsFirstOptimizedUnderAFlushModeKeepTheirSubnormalBits(string mode)
    {
        using var environment = WaterFloatingPointEnvironment.Capture();
        Assert.NotNull(environment);
        Assert.True(environment.SetNearest());
        if (!environment.Set(mode)) return;
        // Only the first call of each step happens under the control. It compiles the step.
        if (mode == "flush-output") _ = Compile<FlushOutput>();
        else _ = Compile<FlushInput>();
        Assert.True(environment.SetNearest());

        var steps = mode == "flush-output" ? Compile<FlushOutput>() : Compile<FlushInput>();
        Assert.Equal(0x80000001u, steps.DownOfZero);
        Assert.Equal(0x00000001u, steps.UpOfNegativeZero);
        Assert.Equal(0x80000001u, steps.DownOfZeroLiteral);
        Assert.Equal(0x00000001u, steps.UpOfNegativeZeroLiteral);
        Assert.Equal(0x00000001u, steps.UpOfZeroLiteral);
        Assert.Equal(0x80000001u, steps.DownOfNegativeZeroLiteral);
    }

    private static (uint DownOfZero, uint UpOfNegativeZero, uint DownOfZeroLiteral, uint UpOfNegativeZeroLiteral,
        uint UpOfZeroLiteral, uint DownOfNegativeZeroLiteral) Compile<TMode>() where TMode : struct =>
        (Bits(Down<TMode>(0f)), Bits(Up<TMode>(-0f)), Bits(DownOfZero<TMode>()), Bits(UpOfNegativeZero<TMode>()),
            Bits(UpOfZero<TMode>()), Bits(DownOfNegativeZero<TMode>()));

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    // A value-type argument gives each mode its own compiled copy of every step.
    private struct FlushOutput;
    private struct FlushInput;

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static float Down<TMode>(float x) where TMode : struct => FloatUlp.Down(x);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static float Up<TMode>(float x) where TMode : struct => FloatUlp.Up(x);

    // The JIT knows these arguments. Both zero signs matter: a step that took one of them through its integer path
    // folded that path into a subnormal constant, and Up(0f) came back as zero.
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static float DownOfZero<TMode>() where TMode : struct => FloatUlp.Down(0f);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static float UpOfNegativeZero<TMode>() where TMode : struct => FloatUlp.Up(-0f);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static float UpOfZero<TMode>() where TMode : struct => FloatUlp.Up(0f);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static float DownOfNegativeZero<TMode>() where TMode : struct => FloatUlp.Down(-0f);
}
