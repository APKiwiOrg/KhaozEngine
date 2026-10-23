using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render2D;
using KhaozEngine.WorldMap;
using KhaozEngine.WorldMap.Gui;
using Xunit;

namespace KhaozEngine.Tests.WorldMap.Gui;

// Fixture setup alone uses internal construction. Every view call uses the public adapter.
internal sealed class WorldMapRenderFixture : IDisposable
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal MapGpuDevice Device { get; } = new();
    internal Render2DSurface Surface { get; }
    internal Render2DContext Context { get; }
    internal SpriteBatch Batch => Surface.Batch;

    internal WorldMapRenderFixture()
    {
        Type coreType = typeof(Render2DContext).Assembly.GetType("KhaozEngine.Render2D.Internal.Render2DCore", true)!;
        object core = Activator.CreateInstance(coreType, Device,
            new GpuOutputDescription(null, [GpuPixelFormat.R8G8B8A8UNorm]), false)!;
        Surface = (Render2DSurface)RuntimeHelpers.GetUninitializedObject(typeof(Render2DSurface));
        typeof(Render2DSurface).GetField("_core", Hidden)!.SetValue(Surface, core);
        Context = (Render2DContext)Activator.CreateInstance(typeof(Render2DContext), Hidden, null,
            [core, 64, 64], null)!;
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
        typeof(SpriteBatch).GetMethod("NewFrame", Hidden)!.Invoke(Batch, [Device.CreateCommandList(), 64, 64]);
        Batch.Begin();
    }

    internal void EndCapture() => typeof(Render2DContext).GetMethod("DisposeOwned", Hidden)!.Invoke(Context, null);

    public void Dispose()
    {
        EndCapture();
        Surface.Dispose();
    }
}
