using System.IO;
using KhaozEngine.Collision;
using Xunit;

namespace KhaozEngine.Tests.Collision;

public class PropSurfaceTests
{
    // A 3x3 grid, cell 1.0, origin (-1,-1): a flat top at y=2 over the centre, empty (NaN) corners.
    static PropSurface Sample()
    {
        float n = float.NaN;
        var h = new[] { n, 2f, n, 2f, 2f, 2f, n, 2f, n };
        return new PropSurface(3, 3, 1f, -1f, -1f, h);
    }

    [Fact]
    public void SampleLocal_Centre_ReturnsTop()
    {
        Assert.Equal(2f, Sample().SampleLocal(0f, 0f)!.Value, 3);
    }

    [Fact]
    public void SampleLocal_OutsideGrid_ReturnsNull()
    {
        Assert.Null(Sample().SampleLocal(10f, 0f));
    }

    [Fact]
    public void MaxHeight_IsMaxNonEmpty()
    {
        Assert.Equal(2f, Sample().MaxHeight, 3);
    }

    [Fact]
    public void BinaryRoundTrip_IsIdentical()
    {
        PropSurface a = Sample();
        using var ms = new MemoryStream();
        a.Write(ms);
        ms.Position = 0;
        PropSurface b = PropSurface.Read(ms);
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        Assert.Equal(a.CellSize, b.CellSize, 4);
        Assert.Equal(a.SampleLocal(0f, 0f)!.Value, b.SampleLocal(0f, 0f)!.Value, 3);
        Assert.Null(b.SampleLocal(10f, 0f));
    }
}
