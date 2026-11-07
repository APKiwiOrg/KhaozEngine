using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ContactDropPublicationTests
{
    const float Dt = 1f / 30;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CertifiedDropSurvivesAuthorityPublicationAndOwnerReconciliation(bool sharded, bool terrain)
    {
        // #1707: a zero position error hid a changed carried velocity and loss of the exact certificate.
        var tuning = new MoveTuning(2, 5, 0.75f, System.MathF.PI / 4, CapsuleRadius: 0.3f)
        { Controller = MovementControllerKind.Contact, Water = new(WaterTraversalMode.SurfaceSwimmer) };
        using var heads = new OrdinaryNetworkHeads(sharded, tuning: tuning, spawn: new(3.625f, 4.75f, 0.125f),
            dropHeight: 4, terrain: terrain, tickSeconds: Dt);
        var scene = heads.ClientWorld;
        using var context = new GroundMoveContext(scene.Ground, scene.Normal, scene.Physics, null, null, null,
            scene.Field, scene.Registration, scene.Boundary);
        var options = new PhysicsNavBakeOptions(3, -0.75f, 6, 0.75f, 0.25f, 10, 14, tuning.MaxSlopeRadians, 128, 512)
        { CaptureTerrainDescentEvidence = terrain };
        using var capture = PhysicsNavBake.Capture(context, options, _ => 0);
        var navigation = capture.BuildProfile(tuning, default, new GroundProfileOptions { MaxDropMetres = 6, AllowTerrainDescents = terrain });
        var link = navigation.Graph.Links.First(l => l.Kind == NavLinkKind.Drop && l.FromZ == 3 &&
            (terrain ? navigation.Terrain!.Route(l) == ContactDropRoute.Terrain : true));
        var from = ContactDropProof.Feet(navigation.Space.Layers[link.FromLayer], link.FromX, link.FromZ);
        var to = ContactDropProof.Feet(navigation.Space.Layers[link.ToLayer], link.ToX, link.ToZ);
        var basis = heads.State;
        basis.Position = from + Vector3.UnitY * tuning.CapsuleHalfHeight;
        Assert.True(heads.Place(basis));
        heads.Pump(1);
        var follower = new ContactDropFollower(navigation, new(link, from, to));
        bool flew = false, dropping = false, completed = false;
        var input = new PlayerPathInput(0);
        for (int tick = 0; tick < options.MaxEdgeProbeSteps; tick++)
        {
            var body = heads.Client.LocalPredictedState.Move;
            var steering = follower.Tick(body, tuning, true, Dt, context, input, out completed, out bool replan);
            Assert.False(replan);
            Assert.NotEqual(RangeMoveStatus.Suspended, steering.Status);
            Assert.NotEqual(RangeMoveStatus.Blocked, steering.Status);
            dropping |= steering.Status == RangeMoveStatus.Dropping;
            if (completed)
            {
                Assert.Equal(Vector2.Zero, steering.WorldDirection);
                Assert.True(body.Grounded);
            }
            Assert.True(heads.Client.SendInput(input.Command(steering, true)) >= 0);
            var predicted = heads.Client.LocalPredictedState.Move;
            heads.Pump(1);
            AssertBodyBasis(predicted, heads.State.Move);
            AssertBodyBasis(heads.State.Move, heads.Client.LocalPredictedState.Move);
            flew |= !heads.State.Grounded;
            if (completed) break;
        }
        Assert.True(dropping);
        Assert.True(flew);
        Assert.True(completed);
        Assert.True(heads.State.Grounded);
        Assert.True(GroundTraversalProbe.Near(heads.State.Position - Vector3.UnitY * tuning.CapsuleHalfHeight, to));
    }

    internal static void AssertBodyBasis(in MoveState expected, in MoveState actual)
    {
        Assert.Equal(expected.Position, actual.Position);
        Assert.Equal(expected.HorizontalVelocity, actual.HorizontalVelocity);
        Assert.Equal(expected.VerticalVelocity, actual.VerticalVelocity);
        Assert.Equal(expected.Grounded, actual.Grounded);
        Assert.Equal(expected.Swimming, actual.Swimming);
        Assert.Equal(expected.WaterExcursion, actual.WaterExcursion);
        Assert.Equal(expected.Commitment, actual.Commitment);
        Assert.Equal(expected.JumpBufferRemaining, actual.JumpBufferRemaining);
        Assert.Equal(expected.TimeSinceGrounded, actual.TimeSinceGrounded);
        Assert.Equal(expected.SpeedScale, actual.SpeedScale);
    }
}
