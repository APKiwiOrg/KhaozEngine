using System.Numerics;
using KhaozEngine.Particles;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Particles;

/// <summary>
/// Issue #124: a phase's particle pool is shared by every concurrent instance of an effect, while Play is per
/// instance, so two overlapping plays compete for one pool's room. ParticleSystem.Emit clamped that silently,
/// with no return value and no counter, so a starved burst read only as "the second explosion had fewer
/// particles" with nothing anywhere to explain it. The clamp is now reported.
/// </summary>
public class ParticleStarvationTests
{
    private static EmitterConfig Emitter() => new()
    {
        LifetimeMin = 100f,
        LifetimeMax = 100f,
        SpeedMin = 1f,
        SpeedMax = 1f,
        Direction = Vector3.UnitY,
        SpreadDegrees = 0f,
        Gravity = Vector3.Zero,
        Drag = 0f,
        StartSize = 1f,
        EndSize = 1f,
        StartColor = Color.White,
        EndColor = Color.White,
    };

    [Fact]
    public void Emit_ReportsWhatTheClampCost()
    {
        var sys = new ParticleSystem(capacity: 10, seed: 1);
        EmitterConfig cfg = Emitter();

        sys.Emit(cfg, Vector3.Zero, 4);
        Assert.Equal(4, sys.ActiveCount);
        Assert.Equal(0, sys.DroppedLastEmit);
        Assert.Equal(0, sys.DroppedTotal);

        sys.Emit(cfg, Vector3.Zero, 8);   // only 6 fit
        Assert.Equal(10, sys.ActiveCount);
        Assert.Equal(2, sys.DroppedLastEmit);
        Assert.Equal(2, sys.DroppedTotal);

        sys.Emit(cfg, Vector3.Zero, 5);   // full pool: the whole ask is lost
        Assert.Equal(10, sys.ActiveCount);
        Assert.Equal(5, sys.DroppedLastEmit);
        Assert.Equal(7, sys.DroppedTotal);
    }

    [Fact]
    public void AnEmitThatFits_ClearsTheLastEmitCount_ButKeepsTheTotal()
    {
        var sys = new ParticleSystem(capacity: 4, seed: 1);
        EmitterConfig cfg = Emitter();

        sys.Emit(cfg, Vector3.Zero, 6);   // 2 dropped
        Assert.Equal(2, sys.DroppedLastEmit);

        sys.Clear();
        sys.Emit(cfg, Vector3.Zero, 3);   // fits whole
        Assert.Equal(0, sys.DroppedLastEmit);
        Assert.Equal(2, sys.DroppedTotal);   // lifetime telemetry survives a Clear, like AbsorbedTotal

        sys.Emit(cfg, Vector3.Zero, 0);      // an emit of nothing drops nothing
        Assert.Equal(0, sys.DroppedLastEmit);
        Assert.Equal(2, sys.DroppedTotal);
    }

    // The shape the issue names from the shipped presets: a phase whose burst is comfortable on its own and
    // over budget the moment a second instance of the same effect overlaps it.
    private static ParticleEffect BurstEffect(int burstCount, int poolCapacity) =>
        new(new ParticleEffectPhase
        {
            Config = Emitter(),
            Delay = 0f,
            Duration = 0f,
            BurstCount = burstCount,
            PoolCapacity = poolCapacity,
        });

    [Fact]
    public void TwoOverlappingPlays_StarveTheSharedPhasePool_AndSayHowMuch()
    {
        // VfxPresets' own numbers: BurstCount 24 into a PoolCapacity of 40. One play is fine, two want 48.
        var player = new ParticleEffectPlayer(BurstEffect(burstCount: 24, poolCapacity: 40), maxInstances: 4, seed: 1);
        Assert.True(player.Play(Vector3.Zero, Vector3.UnitY));
        Assert.True(player.Play(new Vector3(5f, 0f, 0f), Vector3.UnitY));

        player.Update(1f / 60f);

        Assert.Equal(40, player.PhaseSystem(0).ActiveCount);   // pool full: the second burst was clipped
        Assert.Equal(8, player.DroppedLastUpdate);             // and by exactly how much
        Assert.Equal(8, player.DroppedTotal);
    }

    [Fact]
    public void OnePlayInsideItsBudget_DropsNothing()
    {
        // Not vacuous: the same effect, one instance, reports a clean run, so the counter above is the overlap
        // and not a constant.
        var player = new ParticleEffectPlayer(BurstEffect(burstCount: 24, poolCapacity: 40), maxInstances: 4, seed: 1);
        Assert.True(player.Play(Vector3.Zero, Vector3.UnitY));

        player.Update(1f / 60f);

        Assert.Equal(24, player.PhaseSystem(0).ActiveCount);
        Assert.Equal(0, player.DroppedLastUpdate);
        Assert.Equal(0, player.DroppedTotal);
    }

    [Fact]
    public void DroppedLastUpdate_IsPerUpdate_WhileTheTotalAccumulates()
    {
        var player = new ParticleEffectPlayer(BurstEffect(burstCount: 24, poolCapacity: 40), maxInstances: 4, seed: 1);
        player.Play(Vector3.Zero, Vector3.UnitY);
        player.Play(new Vector3(5f, 0f, 0f), Vector3.UnitY);
        player.Update(1f / 60f);
        Assert.Equal(8, player.DroppedLastUpdate);

        // Nothing new is scheduled (the burst fires once per instance), so the next frame is clean while the
        // lifetime total holds what the encounter has cost so far.
        player.Update(1f / 60f);
        Assert.Equal(0, player.DroppedLastUpdate);
        Assert.Equal(8, player.DroppedTotal);

        // A third play into the still-full pool loses its whole burst, and says so.
        player.Play(new Vector3(-5f, 0f, 0f), Vector3.UnitY);
        player.Update(1f / 60f);
        Assert.Equal(24, player.DroppedLastUpdate);
        Assert.Equal(32, player.DroppedTotal);
    }
}
