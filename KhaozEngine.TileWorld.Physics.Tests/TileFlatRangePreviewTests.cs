using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Tests.Locomotion.Contacts;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class TileFlatRangePreviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlatCertificateCoversAuthoredSeamsAndDeclinesNonuniformBeds(bool nonuniform)
    {
        // #1729. This exercises the installed producer, including its captured analytic/static exclusions.
        using var scene = new ContactTerrainScene(0, document: ContactTerrainScene.Document(0,
            profile: x => nonuniform && x > 1 ? 0.5f : 0.25f));
        var source = Assert.IsAssignableFrom<IMovementFlatAnalyticSupportSource>(scene.Source);
        var status = source.CertifyFlatRegion(new(0.5f, -1.75f), new(2.5f, -0.75f), out float height);
        if (nonuniform)
        {
            Assert.Equal(MovementAvailability.Unresolved, status);
            return;
        }
        Assert.Equal(MovementAvailability.Known, status);
        var random = new Random(172915);
        for (int sample = 0; sample < 128; sample++)
        {
            float x = 0.5f + random.NextSingle() * 2, z = -1.75f + random.NextSingle();
            LocomotionStateBits.AssertEqual(height, scene.Colliders.MovementGroundHeight(x, z),
                LocomotionStateBits.Policy.IncludeContinuityHandle);
            Assert.Equal(Vector3.UnitY, scene.Colliders.MovementGroundNormal(x, z));
        }
        MoveTuning tuning = ContactTerrainScene.Tuning;
        var body = new MoveState { Position = new(1, height + tuning.CapsuleHalfHeight, -1), Grounded = true };
        MoveState predicted = scene.Context.Step(body, Vector2.UnitX, false, 0.1f, tuning);
        using var preview = scene.Context.OpenFlatRangePreview(body, predicted, tuning, false, 0.1f);
        Assert.NotNull(preview);
        LocomotionStateBits.AssertEqual(predicted, preview.Step(Vector2.UnitX), LocomotionStateBits.Policy.IncludeContinuityHandle);
    }
}
