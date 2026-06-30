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
}
