using System.Linq;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.MapDoc.Physics;

internal static partial class NativeWorldFixtures
{
    internal static PhysicsShape NestedCompoundShape(int levels)
    {
        float[] offsets = new[] { -524288f }.Concat(Enumerable.Repeat(-0.03125f, levels - 2)).Append(524288f).ToArray();
        PhysicsShape shape = new BoxShape(new(0.515625f, 0.5f, 0.5f));
        foreach (float offset in offsets.Reverse())
            shape = new CompoundShape(new[] { new CompoundChild(shape, Pose.At(new(offset, 0, 0))) });
        return shape;
    }
}
