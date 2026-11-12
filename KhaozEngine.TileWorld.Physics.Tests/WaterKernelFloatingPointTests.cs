using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;
using P = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Plane;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Collection("AllocSensitive")]
[Trait("Category", "WaterKernelContract")]
public sealed class WaterKernelFloatingPointTests
{
    [Fact]
    public void ConfiguredHostedArchitecturesHaveANativeControlToken()
    {
        if ((OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) &&
            RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.X64)
            Assert.True(TileWaterFloatingPointState.TryRead(out _));
    }

    [Fact]
    public async Task ConcurrentPreparationsObserveCompleteGeometryEntries()
    {
        var shape = new TileWaterConvexShape([P.Point(0.95, -1, 0.17, -0.35), P.Point(1, 0, 1, 1)]);
        var frozen = new FrozenTileWaterConvexShape(shape);
        using var start = new Barrier(2);
        Task first = Task.Factory.StartNew(() => Compare(V.Point(0.375, -0.45606077, 0.79904)),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task second = Task.Factory.StartNew(() => Compare(V.Point(-0.375, 0.5, 0.125)),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await Task.WhenAll(first, second);

        void Compare(V from)
        {
            using var environment = WaterFloatingPointEnvironment.Capture();
            bool set = environment?.Set("nearest") ?? true;
            start.SignalAndWait();
            try
            {
                Assert.True(set);
                V drive = V.Point(0.13, 0.096971154, -0.4);
                var workspace = new TileWaterPreparationWorkspace();
                var prepared = shape.Prepare(workspace, from, drive, I.Point(0), 0.3f);
                foreach (var window in new (double Enter, double Exit)[] { (0, 1), (0.25, 0.75), (0.5, 0.5) })
                    FrozenWaterComparison.Range(frozen.Intersect(from, drive, I.Point(0), 0.3f, window.Enter, window.Exit),
                        prepared.Evaluate(window.Enter, window.Exit));
            }
            finally { environment?.Restore(); }
        }
    }

    [Theory]
    [InlineData("up")]
    [InlineData("down")]
    [InlineData("flush-output")]
    public void APreparationUnderOtherControlsTakesTheDirectEvaluator(string mode)
    {
        using var environment = WaterFloatingPointEnvironment.Capture();
        if (environment is null) return; // Unsupported native ABI retains the direct evaluation contract.
        var shape = new TileWaterConvexShape([P.Point(0.95, -1, 0.17, -0.35), P.Point(1, 0, 1, 1)]);
        var frozen = new FrozenTileWaterConvexShape(shape);
        V start = V.Point(0.375, -0.45606077, 0.79904), drive = V.Point(0.13, 0.096971154, -0.4);
        var workspace = new TileWaterPreparationWorkspace();
        TileWaterQueryWork.Reset();
        try
        {
            Assert.True(environment.Set("nearest"));
            bool supported = TileWaterFloatingPointState.TryRead(out ulong before);
            var prepared = shape.Prepare(workspace, start, drive, I.Point(0), 0.3f);
            FrozenWaterComparison.Range(frozen.Intersect(start, drive, I.Point(0), 0.3f), prepared.Evaluate());
            int fills = TileWaterQueryWork.LazyCoefficientFills;
            if (supported) Assert.Equal(before, shape.GeometryControlTokenForTests(1));
            Assert.True(environment.Set(mode));
            if (supported)
            {
                Assert.True(TileWaterFloatingPointState.TryRead(out ulong after));
                Assert.NotEqual(before, after);
            }
            FrozenWaterComparison.Range(frozen.Intersect(start, drive, I.Point(0), 0.3f, 0.25, 0.75), prepared.Evaluate(0.25, 0.75));
            Assert.Equal(fills, TileWaterQueryWork.LazyCoefficientFills);
            Assert.Equal(1, TileWaterQueryWork.ShapePreparations);
            Assert.Equal(2, TileWaterQueryWork.ConvexIntersections);
            // A new preparation has the changed token. Its geometry must bypass the old shape entry.
            var changed = shape.Prepare(workspace, start, drive, I.Point(0), 0.3f);
            FrozenWaterComparison.Range(frozen.Intersect(start, drive, I.Point(0), 0.3f), changed.Evaluate());
            if (supported)
            {
                Assert.True(TileWaterFloatingPointState.TryRead(out ulong current));
                var expectedGeometry = TileWaterGeometryTerms.Compute(shape, shape.Bases[1]);
                var actualGeometry = shape.GeometryForPreparation(1, true, current);
                Span<TileWaterGeometryTerms.Value> values = stackalloc TileWaterGeometryTerms.Value[] { expectedGeometry, actualGeometry };
                ReadOnlySpan<byte> bits = MemoryMarshal.AsBytes(values);
                Assert.True(bits[..(bits.Length / 2)].SequenceEqual(bits[(bits.Length / 2)..]));
                Assert.Equal(before, shape.GeometryControlTokenForTests(1));
            }
        }
        finally
        {
            environment.Restore();
            TileWaterQueryWork.Enabled = false;
        }
    }

    [Fact]
    public void FlushModeBypassesGeometryWithAnExactUnderflowingProduct()
    {
        using var environment = WaterFloatingPointEnvironment.Capture();
        if (environment is null) return;
        try
        {
            Assert.True(environment.Set("nearest"));
            if (!TileWaterFloatingPointState.TryRead(out ulong before)) return;
            // The input 2^-530 is a normal binary64 value. Its exact square 2^-1060 is subnormal.
            // FZ therefore changes the stored aa bits without a cancellation threshold or subnormal input.
            var shape = new TileWaterConvexShape([P.Point(Math.ScaleB(1, -530), 0, 0, 0)]);
            var initial = shape.GeometryForPreparation(1, true, before);
            Assert.Equal(BitConverter.DoubleToInt64Bits(Math.ScaleB(1, -1060)), BitConverter.DoubleToInt64Bits(initial.Aa.Low));
            Assert.True(environment.Set("flush-output"));
            Assert.True(TileWaterFloatingPointState.TryRead(out ulong changed));
            Assert.NotEqual(before, changed);
            var expected = TileWaterGeometryTerms.Compute(shape, shape.Bases[1]);
            var actual = shape.GeometryForPreparation(1, true, changed);
            Assert.Equal(0L, BitConverter.DoubleToInt64Bits(expected.Aa.Low));
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Aa.Low), BitConverter.DoubleToInt64Bits(actual.Aa.Low));
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Aa.High), BitConverter.DoubleToInt64Bits(actual.Aa.High));
            Assert.Equal(before, shape.GeometryControlTokenForTests(1));
        }
        finally { environment.Restore(); }
    }
}
