using KhaozEngine.NetWorld;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.Compatibility;

public class DrainControllerCompatibilityTests
{
    [Fact]
    public void NetWorld_facade_preserves_the_simulation_controller_contract()
    {
        var compatibility = new DrainController();
        var shared = new KhaozEngine.Simulation.Hosting.DrainController();

        compatibility.Begin(0.5f);
        shared.Begin(0.5f);
        compatibility.Advance(0.25f);
        shared.Advance(0.25f);

        Assert.Equal(shared.HasBegun, compatibility.HasBegun);
        Assert.Equal(shared.IsDraining, compatibility.IsDraining);
        Assert.Equal(shared.IsComplete, compatibility.IsComplete);

        compatibility.Advance(0.25f);
        shared.Advance(0.25f);
        Assert.Equal(shared.IsDraining, compatibility.IsDraining);
        Assert.Equal(shared.IsComplete, compatibility.IsComplete);
    }
}
