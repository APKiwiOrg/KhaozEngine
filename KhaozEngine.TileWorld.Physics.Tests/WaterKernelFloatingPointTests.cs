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
    // The changed-control tests run in KhaozEngine.TileWorld.Physics.FloatingPoint.Tests.
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
            bool set = environment?.SetNearest() ?? true;
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
}
