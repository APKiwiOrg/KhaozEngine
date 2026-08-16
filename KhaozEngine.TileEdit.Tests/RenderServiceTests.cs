using System;
using System.IO;
using System.Numerics;
using KhaozEngine.TileEdit;
using KhaozEngine.TileWorld;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.TileEdit;

/// <summary>Headless render tests for <see cref="RenderService"/>. The GPU rows are gated behind
/// <see cref="GpuFactAttribute"/> (they need a real headless device, KE_GPU_TESTS=1 on the dev Mac's Metal) and
/// assert structurally: a PNG whose header carries the size the framing line claims, overlays that change the
/// bytes, and a file where one was asked for. No goldens, the cross-backend bake is not this task's work. The
/// error rows fire before any GPU work and are plain facts.
/// <para>In <c>NativeDeviceLifecycle</c> because each GPU row builds a whole device, which the call sites do not
/// show. See <see cref="NativeDeviceLifecycleCollection"/>.</para></summary>
[Collection("NativeDeviceLifecycle")]
public class RenderServiceTests
{
    sealed class Fixture : IDisposable
    {
        public TempDir Temp { get; } = new();
        public TileEditSession Session { get; }
        public RenderService Render { get; }

        public Fixture()
        {
            Session = TileEditTestWorld.NewSession(Temp.Sub("world"));
            TileEditTestWorld.Build(new MutationService(Session));
            Render = new RenderService(Session);
        }

        public void Dispose() => Temp.Dispose();
    }

    // The PNG signature plus the IHDR width and height, which sit at fixed offsets in every PNG this engine
    // writes: an 8 byte signature, a 4 byte length, the "IHDR" tag, then the two big-endian dimensions.
    static (int Width, int Height) ReadPngHeader(byte[] png)
    {
        Assert.True(png.Length > 100, $"a render returned only {png.Length} bytes.");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        return (BigEndian(png, 16), BigEndian(png, 20));
    }

    static int BigEndian(byte[] bytes, int at) =>
        (bytes[at] << 24) | (bytes[at + 1] << 16) | (bytes[at + 2] << 8) | bytes[at + 3];

    [GpuFact]
    public void RenderTopDown_ProducesAPngOfExactlyTheRectAtThePixelsPerTile()
    {
        using var f = new Fixture();

        RenderResult result = f.Render.RenderTopDown(new TileRect(0, 0, 8, 8), 0, pxPerTile: 8);

        Assert.Equal(64, result.Width);
        Assert.Equal(64, result.Height);
        Assert.Equal((64, 64), ReadPngHeader(result.Png));
        Assert.Contains("north up", result.Framing, StringComparison.Ordinal);
        Assert.Contains("8 px/tile", result.Framing, StringComparison.Ordinal);
        Assert.Null(result.SavedPath);
    }

    [GpuFact]
    public void RenderTopDown_OverlaysChangeThePixelsAndAreNamedInTheFraming()
    {
        using var f = new Fixture();
        var rect = new TileRect(0, 0, 8, 8);

        byte[] plain = f.Render.RenderTopDown(rect, 0, 8).Png;
        RenderResult painted = f.Render.RenderTopDown(rect, 0, 8, "grid,collision,objects,regions");

        Assert.NotEqual(plain, painted.Png);
        Assert.Contains("overlays grid,collision,objects,regions", painted.Framing, StringComparison.Ordinal);
        // Two renders of the same world with the same overlays are byte identical, which is what makes a
        // before-and-after comparison mean anything.
        Assert.Equal(painted.Png, f.Render.RenderTopDown(rect, 0, 8, "regions,objects,collision,grid").Png);
    }

    [GpuFact]
    public void RenderTopDown_WritesThePngWhenAPathIsGiven()
    {
        using var f = new Fixture();

        RenderResult result = f.Render.RenderTopDown(new TileRect(0, 0, 4, 4), 0, 8, null, "shots/map.png");

        Assert.NotNull(result.SavedPath);
        Assert.Equal(Path.Combine(f.Session.DocumentPath!, "shots", "map.png"), result.SavedPath);
        Assert.Equal(result.Png, File.ReadAllBytes(result.SavedPath!));
    }

    [GpuFact]
    public void RenderView_ProducesAPngOfTheRequestedSize()
    {
        using var f = new Fixture();

        // World z is minus tile z, so the tiles at the origin sit at world z 0 and below. Eye above and to the
        // south looking back at the wall and the tree.
        RenderResult result = f.Render.RenderView(new Vector3(-4f, 12f, 8f), new Vector3(2f, 0f, -2f), 96, 64,
            new TileCoord(2, 2, 0));

        Assert.Equal((96, 64), ReadPngHeader(result.Png));
        Assert.Contains("perspective", result.Framing, StringComparison.Ordinal);
        Assert.Contains("(2, 2, p0)", result.Framing, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderTopDown_RefusesAnUnknownOverlayBeforeAnyGpuWork()
    {
        using var f = new Fixture();

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            f.Render.RenderTopDown(new TileRect(0, 0, 4, 4), 0, 4, "grid,heatmap"));

        Assert.Contains("heatmap", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderView_RefusesAZeroLookDirection()
    {
        using var f = new Fixture();

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            f.Render.RenderView(new Vector3(1f, 2f, 3f), new Vector3(1f, 2f, 3f), 64, 64));

        Assert.Contains("direction", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderWithoutAWorld_ThrowsNamingTheOpeningVerbs()
    {
        var render = new RenderService(new TileEditSession());

        TileWorldException ex = Assert.Throws<TileWorldException>(() =>
            render.RenderTopDown(new TileRect(0, 0, 4, 4), 0, 4));

        Assert.Contains("world_open", ex.Message, StringComparison.Ordinal);
    }
}
