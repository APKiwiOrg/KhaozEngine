using System;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Render2D;
using KhaozEngine.Render2D.Internal;
using KhaozEngine.WorldMap;
using KhaozEngine.WorldMap.Gui;
using Xunit;

namespace KhaozEngine.Tests.WorldMap.Gui;

// Fixture setup alone uses internal construction. Every view call uses the public adapter.
internal sealed class WorldMapRenderFixture : IDisposable
{
    internal MapGpuDevice Device { get; } = new();
    internal Render2DSurface Surface { get; }
    internal Render2DContext Context { get; }
    internal SpriteBatch Batch => Surface.Batch;

    internal WorldMapRenderFixture()
    {
        var core = new Render2DCore(Device,
            new GpuOutputDescription(null, [GpuPixelFormat.R8G8B8A8UNorm]), ownsDevice: false);
        Surface = Render2DSurface.CreateForTests(core);
        Context = new Render2DContext(core, 64, 64);
    }

    internal static WorldMapView SnapshotView(WorldMapChunkCache? cache, Render2DContext? context)
    {
        ConstructorInfo? constructor = typeof(WorldMapView).GetConstructor([typeof(WorldMapChunkCache), typeof(Render2DContext)]);
        Assert.NotNull(constructor);
        try { return (WorldMapView)constructor.Invoke([cache, context]); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    internal void BeginFrame()
    {
        Batch.NewFrame(Device.CreateCommandList(), 64, 64);
        Batch.Begin();
    }

    internal void EndCapture() => Context.DisposeOwned();

    public void Dispose()
    {
        EndCapture();
        Surface.Dispose();
    }
}
