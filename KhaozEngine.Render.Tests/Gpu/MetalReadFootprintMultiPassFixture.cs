using System;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Scene3D's shared rigid/ground, foliage and prop streams across shadow and colour passes.</summary>
internal sealed class MetalReadFootprintMultiPassFixture : IDisposable
{
    internal const int DrawCount = 2850;
    internal readonly MetalReadFootprintDrawFixture Draws = new(DrawCount, freshSlices: true, multiPass: true);

    // Scene3D.CascadeGeometry walks three cascades. PointGeometry walks six faces, then GeometryPass and
    // Foliage draw the same uploaded windows. Counts approximate the profiled frame, not a scene capture.
    internal static int PassDraws(int pass) => pass < 3 || pass == 9 ? 450 : 150;
    internal static int Pipeline(int pass) => pass < 3 ? 0 : pass < 9 ? 1 : pass == 9 ? 2 : 3;
    internal static (uint Family, uint Window, uint Mesh) Window(int pass, uint draw)
    {
        uint perFamily = (uint)PassDraws(pass) / 3;
        uint family = draw / perFamily;
        uint ordinal = draw % perFamily;
        // Multipliers are coprime to 150 and 50. Later passes walk the same windows in different orders.
        uint window = pass == 0 ? ordinal : (ordinal * 37 + (uint)pass * 13) % perFamily;
        // Ten adjacent windows extend one exact run, followed by two skipped instance elements.
        return (family, window * 4 + window / 10 * 2, (window + family) % 4);
    }

    internal void Draw(int pass, uint draw, uint sequence)
    {
        var (family, window, mesh) = Window(pass, draw);
        var f = Draws;
        f.List.SetPipeline(f.PassPipelines[Pipeline(pass)]);
        f.List.SetVertexBuffer(0, f.Meshes[mesh]);
        f.List.SetVertexBuffer(1, f.InstanceStreams[family]);
        f.List.GraphicsBinds.Record(0, f.Set, sequence * 512);
        f.List.DrawIndexed(1024, 4, 0, 0, window);
    }

    internal void Frame()
    {
        Draws.Begin();
        uint sequence = 0;
        for (int pass = 0; pass < 11; pass++)
            for (uint draw = 0; draw < PassDraws(pass); draw++) Draw(pass, draw, sequence++);
        Draws.List.End();
    }

    public void Dispose() => Draws.Dispose();
}
