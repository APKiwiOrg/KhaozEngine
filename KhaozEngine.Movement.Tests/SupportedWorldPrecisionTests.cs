using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class SupportedWorldPrecisionTests
{
    [Theory]
    [InlineData(-32768f, -0.0010000000474974513f, 0.75f, true)]
    [InlineData(-32768f, -0.0010000001639127731f, 0.75f, false)]
    [InlineData(-32768f, -0.00075f, 0.75f, true)]
    [InlineData(-32768f, -0.00075f, 0.7495f, false)]
    [InlineData(65536f, -0.0010000000474974513f, 0.75f, true)]
    [InlineData(65536f, -0.0010000001639127731f, 0.75f, false)]
    [InlineData(65536f, -0.00075f, 0.75f, true)]
    [InlineData(65536f, -0.00075f, 0.7495f, false)]
    public void AnalyticProducerAccountsForTheWholeWorldToPoseDisplacement(float origin, float height, float half, bool admitted)
    {
        using var physics = new BepuPhysicsWorld();
        physics.Rebase(new(0, origin, 0));
        var builder = OrdinaryMovementRegistration.CreateBuilder("world", physics,
            new(new(-20, -4, -20), new(20, 4, 20), height, height));
        OrdinaryMovementRegistration registration;
        using (var read = physics.AcquireQueryReadLease()) registration = builder.Build(read);
        using var view = physics.CreateQueryViewExcludingStatics([]);
        var tuning = MoveTuning.Default with
        {
            Controller = MovementControllerKind.Contact, CapsuleRadius = 0.25f, CapsuleHalfHeight = half,
            Water = new(WaterTraversalMode.DryOnly),
        };
        var environment = new OrdinaryMovementEnvironment(view, registration, tuning, (_, _) => height, null,
            MovementBoundary.Rectangle(-20, -20, 20, 20));
        var frame = new MovementFrameDescriptor(WorldFrame.Origin, new(0, origin, 0), 1);
        var scope = environment.CreateScope(frame);
        using var lease = physics.AcquireQueryReadLease();
        Assert.Equal(MovementAvailability.Known, environment.TryPinPrepared(scope, lease, out var pin));
        using var owned = Assert.IsAssignableFrom<IMovementEnvironmentPin>(pin);
        var producer = Assert.IsAssignableFrom<IMovementContactSupportPin>(owned);
        var space = new MovementSpaceKey("world", "ordinary");
        var body = new MovementBodyQuery(new(0, -origin + 0.75f, 0), 0.25f, half, space, null);
        var result = producer.SampleAnalyticGround(new(body, 0, 0, 0, new(space, body.Feet)), out var ground);
        Assert.Equal(admitted ? MovementAvailability.Known : MovementAvailability.Unresolved, result);
        if (admitted) Assert.NotNull(ground);
        else Assert.Null(ground);
    }
}
