using System.Numerics;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// A step disagreement near a chase arrival is corrected by the body WALKING onto the right tile at roughly its own
/// step speed, not by cutting there (#873). The rules already hold the corrected tile, so the drawn body lags and
/// catches up, which is how OSRS reads. A true teleport still cuts, and a correct prediction still reconciles to
/// zero, both untouched.
/// </summary>
public class TileArrivalCorrectionTests
{
    const float Tick = TileCombatHarness.Tick;
    const float Frame = TileCombatHarness.Frame;
    // A metre tile, so tiles and world units coincide. The chase runs, so a drawn-speed multiple is measured
    // against the RUN step speed: the body's own motion is then 1x and a cut spikes far above it.
    static float RunTilesPerSecond => 1f / (2 * Tick);

    [Fact]
    public void A_predicted_chase_step_that_diverges_is_walked_off_not_cut()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        using var h = new TileCombatHarness(doc, new TileCoord(10, 10, 0), clientPhase: 0.13f);
        h.Frames(6);
        Assert.True(h.Client.IsJoined);

        // A wandering target the client chases: the server moves it a step out from under the client's prediction as
        // the attacker arrives, which is the one-tile disagreement the correction must walk off.
        long actor = h.Server.SpawnActor(new TileCoord(10, 14, 0), new TileActorSpawn(200, 16, TileDirection.S));
        h.Server.Actors.Command(actor, TileCommand.WalkTo(new TileCoord(14, 14, 0), TileMoveMode.Walk));
        h.Client.Queue(TileCommand.Attack(actor, TileMoveMode.Run));

        float fastest = 0f;
        Vector2 last = Planar(h.Client.LocalPose.Position);
        for (int i = 0; i < 120; i++)
        {
            h.Frames(1);
            Vector2 now = Planar(h.Client.LocalPose.Position);
            float mult = Vector2.Distance(now, last) / (Frame * RunTilesPerSecond);
            fastest = System.MathF.Max(fastest, mult);
            last = now;
        }

        // No frame moves the body more than about one walk step's worth of ground: a cut would be many times this.
        // Run motion is 1x; the walk-speed correction cap adds at most half a run step, so a settling frame
        // peaks near 1.5x. A cut would be many times this.
        Assert.True(fastest <= 1.7f, $"body moved {fastest:F2} run-steps in one frame, which is a cut not a walk");
        Assert.Equal(0, h.Client.SnapCount);
        // The rules agreed all along: the client's predicted lock is the server's.
        Assert.True(h.Server.TryGetActorState(actor, out _));
        Assert.Equal(actor, h.Client.Prediction.PredictedState.CombatTarget);
    }

    [Fact]
    public void A_teleport_still_cuts()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        using var h = new TileCombatHarness(doc, new TileCoord(10, 10, 0), clientPhase: 0.13f);
        h.Frames(6);
        long player = h.Client.LocalNetId;
        Assert.NotEqual(0L, player);

        bool teleported = false;
        h.Client.Teleported += () => teleported = true;
        h.Server.SetPlayerState(0, TileMoveState.At(new TileCoord(30, 30, 0), TileDirection.S), teleport: true);
        h.Frames(4);

        Assert.True(teleported, "an authoritative teleport must report a discontinuity");
        Assert.Equal(new TileCoord(30, 30, 0), h.Client.Prediction.PredictedState.Tile);
    }

    static Vector2 Planar(Vector3 p) => new(p.X, p.Z);
}
