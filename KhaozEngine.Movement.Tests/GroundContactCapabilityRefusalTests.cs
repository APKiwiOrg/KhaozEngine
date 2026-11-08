using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class GroundContactCapabilityRefusalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProducerRestrictionIsCarriedAtMovementAndNavigationAdmission(bool navigation)
    {
        using var scene = new OrdinaryContactScene();
        const string restriction = "producer-owned restriction";
        using var context = new GroundMoveContext(scene.Height, scene.Normal, scene.Physics, null, null, null,
            null, scene.Registration, MovementBoundary.Rectangle(-20, -20, 20, 20), restriction);
        var failure = Assert.Throws<GroundContactCapabilityException>(() =>
        {
            if (navigation) { using var binding = context.CreateNavigationBinding(scene.Tuning); }
            else context.ValidateTuning(scene.Tuning);
        });
        Assert.Equal(GroundContactCapabilityRefusal.ContactProducerRestricted, failure.Reason);
        Assert.Equal(restriction, failure.Restriction);
        Assert.Null(failure.ProducerKind);
        Assert.Null(failure.RequiredCapability);
        using var permitted = ContactNavigationFixture.Context(scene);
        if (navigation)
        {
            using var binding = permitted.CreateNavigationBinding(scene.Tuning);
            Assert.Equal(EffectiveMovementBindingKind.OrdinaryContact, binding.Kind);
        }
        else
        {
            var body = new MoveState { Position = new(0, 0.75f, 0), Grounded = true };
            Assert.True(NpcGroundMovement.Hold(body, 0.1f, scene.Tuning, permitted).Grounded);
        }
    }
}
