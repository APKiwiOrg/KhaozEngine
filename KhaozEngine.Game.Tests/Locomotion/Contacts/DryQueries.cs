using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.Locomotion.Contacts;

// Tests borrow their fixture's dry delegates and lease through the production query seam.
internal static class DryQueries
{
    internal static ContactQueryContext For(Func<float, float, float>? height, Func<float, float, Vector3>? normal,
        IPhysicsWorld? world, IPhysicsQueryLease? lease, in MoveTuning tuning) =>
        new DryContactQueryContext(height, normal, world, lease, tuning);
}
