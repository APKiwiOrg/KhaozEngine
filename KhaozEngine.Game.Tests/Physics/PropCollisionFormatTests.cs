using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class PropCollisionFormatTests
{
    static PhysicsShape RoundTrip(PhysicsShape shape)
    {
        using var ms = new MemoryStream();
        PropCollisionFormat.Write(shape, ms);
        ms.Position = 0;
        return PropCollisionFormat.Read(ms);
    }

    [Fact]
    public void Box_RoundTrips()
    {
        var box = new BoxShape(new Vector3(0.5f, 1.5f, 2.5f));
        var loaded = Assert.IsType<BoxShape>(RoundTrip(box));
        Assert.Equal(box.HalfExtents, loaded.HalfExtents);
    }

    [Fact]
    public void Compound_OfHullAndBoxAtNonIdentityPoses_RoundTrips()
    {
        var hull = new ConvexHullShape(new[] { new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(0,1,0), new Vector3(0,0,1) });
        Quaternion rot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f);
        var compound = new CompoundShape(new[]
        {
            new CompoundChild(hull, new Pose(new Vector3(2, 0, 0), Quaternion.Identity)),
            new CompoundChild(new BoxShape(new Vector3(1, 2, 3)), new Pose(new Vector3(0, 5, 0), rot)),
        });

        var loaded = Assert.IsType<CompoundShape>(RoundTrip(compound));
        Assert.Equal(2, loaded.Children.Length);

        var c0 = loaded.Children[0];
        Assert.Equal(new Vector3(2, 0, 0), c0.Local.Position);
        Assert.Equal(4, Assert.IsType<ConvexHullShape>(c0.Shape).Points.Length);

        var c1 = loaded.Children[1];
        Assert.Equal(new Vector3(0, 5, 0), c1.Local.Position);
        Assert.Equal(rot, c1.Local.Orientation);
        Assert.Equal(new Vector3(1, 2, 3), Assert.IsType<BoxShape>(c1.Shape).HalfExtents);
    }

    [Fact]
    public void ByteIdentical_AcrossTwoWrites()
    {
        var compound = new CompoundShape(new[]
        {
            new CompoundChild(new BoxShape(new Vector3(1, 1, 1)), new Pose(new Vector3(1, 2, 3), Quaternion.Identity)),
        });
        using var a = new MemoryStream();
        using var b = new MemoryStream();
        PropCollisionFormat.Write(compound, a);
        PropCollisionFormat.Write(compound, b);
        Assert.Equal(a.ToArray(), b.ToArray());
    }

    // Hand-built streams below use the raw wire kind byte (1 = convex hull, per PropCollisionFormat's internal
    // KindConvexHull, a stable value that is never renumbered) instead of calling Write, so the corrupt count can
    // be injected directly - the same shape a truncated file or a partial download would produce.
    static MemoryStream StreamWithConvexHullCount(int count)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(PropCollisionFormat.Magic);
            w.Write(PropCollisionFormat.Version);
            w.Write((byte)1);   // KindConvexHull
            w.Write(count);
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Read_NegativeArrayCount_ThrowsInvalidOperationException()
    {
        // A corrupted/truncated .coll handing this a negative int32 must not reach `new Vector3[count]`: the CLR
        // treats a negative array length as an unsigned overflow (OverflowException), not the
        // InvalidOperationException this format promises for every other malformed-input case (issue #147).
        using MemoryStream ms = StreamWithConvexHullCount(-1);
        var ex = Assert.Throws<InvalidOperationException>(() => PropCollisionFormat.Read(ms));
        Assert.Contains("negative", ex.Message);
    }

    [Fact]
    public void Read_ArrayCountExceedingRemainingStream_ThrowsInvalidOperationException()
    {
        // A huge bogus positive count (garbage bits from corruption) must fail cleanly instead of attempting a
        // multi-gigabyte allocation or crawling past the end of the stream (issue #147).
        using MemoryStream ms = StreamWithConvexHullCount(int.MaxValue);
        var ex = Assert.Throws<InvalidOperationException>(() => PropCollisionFormat.Read(ms));
        Assert.Contains("remain", ex.Message);
    }
}
