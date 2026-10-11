using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ExplicitPlayerContactSupportTests
{
    [Fact]
    public void CapturedServerFloorBindsItsInstalledTopToTheCanonicalOwner()
    {
        using var environment = new ExplicitPlayerEnvironment();
        using var physics = environment.Physics.AcquireQueryReadLease();
        var scope = environment.Scope(ExplicitPlayerEnvironment.Initial, WorldFrame.Origin);
        Assert.Equal(MovementAvailability.Known, environment.TryPinPrepared(scope, physics, out var pin));
        using (pin)
        {
            var support = Assert.IsAssignableFrom<IMovementContactSupportPin>(pin);
            var room = new MovementSpaceKey("world", "room");
            var body = new MovementBodyQuery(new(0, 0.75f, 0), 0.25f, 0.75f, room, null);
            var request = new MovementContactSupportRequest(body, new(room, body.Feet), Vector2.Zero,
                0, 0, Vector3.UnitY, Vector3.Zero, environment.FloorHandle, 3);
            Assert.Equal(MovementAvailability.Known, support.BindContactSupport(request, out var binding));
            Assert.Equal(new MovementSupportKey("world", "floor"), binding.Owner);
            Assert.Equal(environment.Identity, binding.Identity);
            var unknown = new MovementContactSupportRequest(body, request.Transition, Vector2.Zero,
                0, 0, Vector3.UnitY, Vector3.Zero, environment.FloorHandle, 1000);
            Assert.Equal(MovementAvailability.Unresolved, support.BindContactSupport(unknown, out binding));
            Assert.Equal(default, binding);
        }
    }

    [Fact]
    public void CapturedServerRoomCertifiesAnalyticAbsence()
    {
        using var environment = new ExplicitPlayerEnvironment();
        using var physics = environment.Physics.AcquireQueryReadLease();
        var scope = environment.Scope(ExplicitPlayerEnvironment.Initial, WorldFrame.Origin);
        Assert.Equal(MovementAvailability.Known, environment.TryPinPrepared(scope, physics, out var pin));
        using (pin)
        {
            var support = Assert.IsAssignableFrom<IMovementContactSupportPin>(pin);
            var room = new MovementSpaceKey("world", "room");
            var body = new MovementBodyQuery(new(0, 0.75f, 0), 0.25f, 0.75f, room, null);
            var request = new MovementSupportRequest(body, 0.4f, 0, 0.8f, new(room, body.Feet));
            Assert.Equal(MovementAvailability.Known, support.SampleAnalyticGround(request, out var ground));
            Assert.Null(ground);
        }
    }
}
