using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class ContactTerrainPhysicalRefusalTests(ITestOutputHelper output)
{
    [Fact]
    public void RealTransientSwimmingBetweenDryTickEndpointsRefusesTheTerrainProof()
    {
        var doc = ContactTerrainScene.Document(4, 0.25f, x => x <= 1 ? 4 : x >= 1.25f ? 0 : 16 * (1.25f - x));
        Vector3 target = new(1.625f, 0, -1.375f);
        using (var control = new ContactTerrainPolicyFixture(4, doc, target: target)) Assert.True(control.Prove().Accepted);
        using var fixture = new ContactTerrainPolicyFixture(4, doc, water: MovementWaterField.Level("world", 0.985f, 0.01f), target: target);
        var refused = fixture.Prove();
        Assert.False(refused.Accepted);
        Assert.Equal(ContactTerrainDropRefusal.Swimming, refused.Reason);
        Assert.False(refused.Steps[0].Swimming);
        var run = fixture.ObserveRunUntilRefusal();
        foreach (var step in run.TakeLast(4)) output.WriteLine($"feet={step.Feet}, grounded={step.Grounded}, swimming={step.Swimming}, entered={step.EnteredSwimming}");
        Assert.Contains(run.Select((step, index) => (step, index)), item => item.index > 0 &&
            !run[item.index - 1].Swimming && item.step.EnteredSwimming && !item.step.Swimming);
    }

    [Theory]
    [InlineData("over-six")]
    [InlineData("shelf")]
    [InlineData("hidden-upper-step")]
    [InlineData("roof")]
    public void OnePhysicalChangeRefusesBesideItsPassingControl(string fault)
    {
        Assert.True(Run(false).Accepted);
        var refused = Run(true);
        Assert.False(refused.Accepted);
        if (fault == "over-six") Assert.Equal(ContactTerrainDropRefusal.DescentAllowance, refused.Reason);
        if (fault is "shelf" or "roof") Assert.Equal(ContactTerrainDropRefusal.Certificate, refused.Reason);
        if (fault == "hidden-upper-step")
        {
            Assert.Contains(refused.Steps, step => step.Grounded && step.Feet.Y > 6);
            Assert.Contains(refused.Steps, step => step.UnwalkableSupport);
            Assert.Contains(refused.Reason, new[] { ContactTerrainDropRefusal.DescentAllowance, ContactTerrainDropRefusal.Movement });
        }

        (bool Accepted, ContactTerrainDropRefusal Reason, System.Collections.Generic.IReadOnlyList<ContactDropStepObservation> Steps) Run(bool changed)
        {
            float height = fault == "over-six" ? changed ? 8 : 6 : fault == "hidden-upper-step" ? 6 : 4;
            var document = ContactTerrainScene.Document(height, fault is "shelf" or "hidden-upper-step" ? 0.25f : 1,
                axis =>
                {
                    if (changed && fault == "shelf" && axis > 1 && axis < 2)
                        return axis <= 1.25f ? 4 - 8 * (axis - 1) : axis <= 1.75f ? 2 : 8 * (2 - axis);
                    if (changed && fault == "hidden-upper-step" && axis == 1) return 6.1f;
                    return axis <= 1 ? height : axis >= 2 ? 0 : height * (2 - axis);
                });
            using var fixture = new ContactTerrainPolicyFixture(height, document, extra: (world, builder) =>
            {
                if (!(changed && fault == "roof")) return;
                var handle = world.AddStatic(new BoxShape(new(0.2f, 0.1f, 0.4f)), Pose.At(new(1.5f, 3, -1.375f)));
                builder.Add(handle, 0, int.MaxValue, new("world", "forbidden-roof"), new(1.3f, 2.9f, -1.775f), new(1.7f, 3.1f, -0.975f));
            });
            return fixture.Prove();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForbiddenLandingAreaRefusesDryAndSupportedWadingPatches(bool water)
    {
        using var fixture = new ContactTerrainPolicyFixture(4, classify: p => p.X >= 2 ? 1u : 0u,
            water: water ? MovementWaterField.Level("world", 0.1f, -1) : null);
        Assert.True(fixture.Prove().Accepted);
        var refused = fixture.Prove(areas: new NavAreaFilter(0, 1));
        Assert.False(refused.Accepted);
        Assert.Equal(ContactTerrainDropRefusal.Certificate, refused.Reason);
    }
}
