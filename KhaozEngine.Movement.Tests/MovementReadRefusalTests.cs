using KhaozEngine.Locomotion;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class MovementReadRefusalTests
{
    [Fact]
    public void StaleBindingReportsTheComparedIdentityAndFrame()
    {
        using var scene = new OrdinaryContactScene();
        using var lease = scene.Acquire();
        var stale = new MovementQueryLeaseId(lease.Id.SourceWorld, lease.Id.GeometryGeneration + 1,
            lease.Id.EnvironmentGeneration, lease.Id.FrameEpoch);
        var failure = Assert.Throws<MovementReadException>(() => lease.InjectStaleBindingForTests(stale));
        Assert.Equal(MovementReadRefusal.QueryBindingStale, failure.Reason);
        Assert.Equal(stale, failure.LeaseId);
        Assert.Equal(lease.Frame, failure.Frame);
        lease.InjectStaleBindingForTests(lease.Id);
        lease.AssertCurrent();
    }
}
