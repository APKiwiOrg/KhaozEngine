using System;
using System.Buffers.Binary;
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
        var hull = new ConvexHullShape(new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1) });
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

    // Malformed shape data is refused at the reader (#1346), in the same InvalidOperationException contract as a bad
    // magic, version, kind or count. Write validates only compound child orientations, so it produces every other
    // malformed stream directly.
    static InvalidOperationException ReadRefusal(PhysicsShape shape) =>
        Assert.Throws<InvalidOperationException>(() => RoundTrip(shape));

    static CompoundShape Wrap(PhysicsShape child, Pose local) => new(new[] { new CompoundChild(child, local) });

    static CompoundShape Nest(int levels)
    {
        CompoundShape shape = Wrap(new BoxShape(Vector3.One), Pose.Identity);
        for (int level = 1; level < levels; level++) shape = Wrap(shape, Pose.Identity);
        return shape;
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Read_NonFiniteBoxHalfExtent_Refuses(float value)
    {
        var ex = ReadRefusal(new BoxShape(new Vector3(1f, value, 1f)));
        Assert.Contains("box half extent", ex.Message);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void Read_NonPositiveBoxHalfExtent_Refuses(float value)
    {
        var ex = ReadRefusal(new BoxShape(new Vector3(1f, 1f, value)));
        Assert.Contains("box half extent", ex.Message);
    }

    [Theory]
    [InlineData(float.NaN, 1f)]
    [InlineData(1f, float.PositiveInfinity)]
    public void Read_NonFiniteCylinder_Refuses(float radius, float length)
    {
        var ex = ReadRefusal(new CylinderShape(radius, length));
        Assert.Contains("cylinder", ex.Message);
    }

    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(1f, -2f)]
    public void Read_NonPositiveCylinder_Refuses(float radius, float length)
    {
        var ex = ReadRefusal(new CylinderShape(radius, length));
        Assert.Contains("cylinder", ex.Message);
    }

    [Fact]
    public void Read_NonFiniteHullPoint_Refuses()
    {
        var ex = ReadRefusal(new ConvexHullShape(new[] { Vector3.Zero, Vector3.UnitX, new Vector3(0f, float.NaN, 0f), Vector3.UnitZ }));
        Assert.Contains("convex hull point", ex.Message);
    }

    [Fact]
    public void Read_NonFiniteMeshVertex_Refuses()
    {
        var ex = ReadRefusal(new TriangleMeshShape(new[] { Vector3.Zero, new Vector3(float.PositiveInfinity, 0f, 0f), Vector3.UnitZ }, new[] { 0, 1, 2 }));
        Assert.Contains("triangle mesh vertex", ex.Message);
    }

    [Fact]
    public void Read_MeshIndexCountNotATriple_Refuses()
    {
        var ex = ReadRefusal(new TriangleMeshShape(new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitZ }, new[] { 0, 1, 2, 0 }));
        Assert.Contains("multiple of 3", ex.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void Read_MeshIndexOutsideTheVertices_Refuses(int index)
    {
        var ex = ReadRefusal(new TriangleMeshShape(new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitZ }, new[] { 0, 1, index }));
        Assert.Contains("triangle mesh index", ex.Message);
    }

    [Fact]
    public void Read_NonFiniteChildPosition_Refuses()
    {
        var ex = ReadRefusal(Wrap(new BoxShape(Vector3.One), new Pose(new Vector3(0f, float.NaN, 0f), Quaternion.Identity)));
        Assert.Contains("compound child pose", ex.Message);
    }

    [Fact]
    public void Read_NonFiniteChildOrientation_Refuses()
    {
        var ex = ReadRefusal(Wrap(new BoxShape(Vector3.One), new Pose(Vector3.Zero, new Quaternion(0f, 0f, 0f, float.NaN))));
        Assert.Contains("compound child pose", ex.Message);
    }

    [Theory]
    [InlineData(0f, 0f, 0f, 0f)]
    [InlineData(0.5f, 0f, 0.5f, 0f)]
    public void Read_NonUnitChildOrientation_Refuses(float x, float y, float z, float w)
    {
        // Write refuses this orientation too, so the stream is written with an identity child and patched.
        byte[] bytes = WithChildOrientation(Wrap(new BoxShape(Vector3.One), Pose.Identity), new Quaternion(x, y, z, w));
        var ex = Assert.Throws<InvalidOperationException>(() => PropCollisionFormat.Read(new MemoryStream(bytes)));
        Assert.Contains("is not a unit quaternion", ex.Message);
    }

    [Theory]
    [InlineData(0f, 0f, 0f, 0f)]
    [InlineData(0.5f, 0f, 0.5f, 0f)]
    [InlineData(0f, 0f, 0f, 1.0006f)]
    public void Write_NonUnitChildOrientation_RefusesWithTheReadersTolerance(float x, float y, float z, float w)
    {
        var nested = Wrap(Wrap(new BoxShape(Vector3.One), new Pose(Vector3.Zero, new Quaternion(x, y, z, w))), Pose.Identity);
        var ex = Assert.Throws<ArgumentException>(() => PropCollisionFormat.Write(nested, new MemoryStream()));
        Assert.Contains("is not a unit quaternion", ex.Message);
    }

    [Fact]
    public void Write_ChildOrientationWithinTheReadersTolerance_RoundTrips()
    {
        // A squared length of 1.0008 is within the 1e-3 tolerance.
        var orientation = new Quaternion(0f, 0f, 0f, 1.0004f);
        var child = Assert.Single(Assert.IsType<CompoundShape>(RoundTrip(Wrap(new BoxShape(Vector3.One),
            new Pose(Vector3.Zero, orientation)))).Children);
        Assert.Equal(orientation, child.Local.Orientation);
    }

    // The bytes of a top-level one-child compound with the child's orientation replaced. The orientation follows the
    // magic, version, kind, child count and child position: 4 + 1 + 1 + 4 + 12 bytes.
    static byte[] WithChildOrientation(CompoundShape shape, Quaternion orientation)
    {
        using var stream = new MemoryStream();
        PropCollisionFormat.Write(shape, stream);
        byte[] bytes = stream.ToArray();
        const int offset = 22;
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), orientation.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 4), orientation.Y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 8), orientation.Z);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 12), orientation.W);
        return bytes;
    }

    [Fact]
    public void Read_CompoundWithoutChildren_Refuses()
    {
        var ex = ReadRefusal(new CompoundShape(Array.Empty<CompoundChild>()));
        Assert.Contains("no children", ex.Message);
    }

    [Fact]
    public void Read_CompoundNestedSixteenLevels_Reads()
    {
        PhysicsShape shape = RoundTrip(Nest(16));
        int levels = 0;
        while (shape is CompoundShape compound)
        {
            levels++;
            shape = Assert.Single(compound.Children).Shape;
        }
        Assert.Equal(16, levels);
        Assert.IsType<BoxShape>(shape);
    }

    [Fact]
    public void Read_CompoundNestedDeeperThanSixteenLevels_Refuses()
    {
        var ex = ReadRefusal(Nest(17));
        Assert.Contains("nesting", ex.Message);
    }
}
