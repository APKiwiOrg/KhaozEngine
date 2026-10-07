using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class MovementImpactScopeTests
{
    delegate MovementAvailability Contacts(in MovementBodyQuery body, float margin, Span<CapsuleContact> scratch,
        out CapsuleContactResult result);

    [Fact]
    public void RoundedInwardScopeAdditionCannotAuthorizeAnImpactQuery()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        var room = new MovementSpaceKey("world", "room");
        var body = new MovementBodyQuery(new(1, 0.75f, 0), 0.3f, 0.75f, room, null);
        float margin = 0.002f;
        float boundary = body.Centre.X + body.Radius + (margin + 0.001f);
        Assert.True((double)body.Centre.X + body.Radius + margin + 0.001f > boundary);
        var scope = new MovementQueryScope(new(-4), new(boundary, 4, 4), 0, 0, room,
            new("closure", 1, "scope"), new(WorldFrame.Origin, Vector3.Zero, 1));
        var environment = new EnvironmentAcquisitionFixture(view, scope);
        var acquired = environment.Acquire();
        Assert.Equal(MovementAvailability.Known, acquired.Status);
        using var lease = Assert.IsType<MovementQueryLease>(acquired.Lease);
        MethodInfo method = typeof(MovementQueryLease).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "QuerySolidContacts" && method.GetParameters().Length == 4);
        var query = method.CreateDelegate<Contacts>(lease);
        var sentinel = new CapsuleContact(Vector3.UnitX, 91, false, 92, 93, 94);
        CapsuleContact[] buffer = [sentinel];
        Assert.Equal(MovementAvailability.Unresolved, query(body, margin, buffer, out CapsuleContactResult result));
        Assert.Equal(default, result);
        Assert.Equal(sentinel, buffer[0]);
    }
}
