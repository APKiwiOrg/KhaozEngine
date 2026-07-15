using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render2D.Vfx;
using Xunit;

namespace KhaozEngine.Tests.Render2D.Vfx;

/// <summary>Headless coverage for the pooled, deterministic 2D particle system (no GPU).</summary>
public class Particle2DSystemTests
{
    const float Tol = 1e-4f;

    // A config whose only randomness is collapsed (min==max ranges, zero jitter), so a single emitted
    // particle is fully deterministic and its motion is hand-computable.
    static Particle2DEmitterConfig Fixed(float speed, Vector2 dir) => new()
    {
        MinLife = 100f,
        MaxLife = 100f,
        MinSpeed = speed,
        MaxSpeed = speed,
        Emission = Particle2DEmission.Directional,
        Direction = dir,
        SpreadRadians = 0f,
        JitterX = 0f,
        JitterY = 0f,
        StartSize = 1f,
        EndSize = 1f,
    };

    [Fact]
    public void Emit_RaisesActiveCount()
    {
        var sys = new Particle2DSystem(capacity: 16, seed: 1);
        Assert.Equal(0, sys.ActiveCount);
        sys.Emit(Fixed(10f, new Vector2(1, 0)), Vector2.Zero, 5);
        Assert.Equal(5, sys.ActiveCount);
    }

    [Fact]
    public void Update_DepletesLifeAndDeactivates()
    {
        var cfg = Fixed(0f, new Vector2(1, 0)) with { MinLife = 0.1f, MaxLife = 0.1f };
        var sys = new Particle2DSystem(8, 1);
        sys.Emit(cfg, Vector2.Zero, 3);
        Assert.Equal(3, sys.ActiveCount);
        sys.Update(0.2f);
        Assert.Equal(0, sys.ActiveCount);
    }

    [Fact]
    public void Gravity_AddsToVelocityThenIntegratesPosition()
    {
        var cfg = Fixed(0f, new Vector2(1, 0)) with { Acceleration = new Vector2(0, 200f) };
        var sys = new Particle2DSystem(4, 1);
        sys.Emit(cfg, Vector2.Zero, 1);
        sys.Update(0.1f);
        var p = sys.ActiveParticles().Single();
        Assert.Equal(20f, p.Velocity.Y, Tol);   // 0 + 200*0.1
        Assert.Equal(2f, p.Position.Y, Tol);     // moved by the new velocity * dt
    }

    [Fact]
    public void Drag_DampensVelocity()
    {
        var cfg = Fixed(100f, new Vector2(1, 0)) with { Drag = 0.5f };
        var sys = new Particle2DSystem(4, 1);
        sys.Emit(cfg, Vector2.Zero, 1);
        sys.Update(0.1f);
        var p = sys.ActiveParticles().Single();
        Assert.Equal(95f, p.Velocity.X, Tol);    // 100 * (1 - 0.5*0.1)
        Assert.Equal(9.5f, p.Position.X, Tol);   // moved by damped velocity * dt
    }

    [Fact]
    public void Rotation_AdvancesByAngularVelocity()
    {
        var cfg = Fixed(0f, new Vector2(1, 0)) with { MinAngularVelocity = 2f, MaxAngularVelocity = 2f };
        var sys = new Particle2DSystem(4, 1);
        sys.Emit(cfg, Vector2.Zero, 1);
        sys.Update(0.5f);
        var p = sys.ActiveParticles().Single();
        Assert.Equal(1f, p.Rotation, Tol);       // 2 rad/s * 0.5s
    }

    [Fact]
    public void Size_LerpsFromStartToEndOverLife()
    {
        var cfg = Fixed(0f, new Vector2(1, 0)) with { MinLife = 1f, MaxLife = 1f, StartSize = 10f, EndSize = 2f };
        var sys = new Particle2DSystem(4, 1);
        sys.Emit(cfg, Vector2.Zero, 1);
        Assert.Equal(10f, sys.ActiveParticles().Single().Size, Tol);   // spawn -> StartSize
        sys.Update(0.5f);
        Assert.Equal(6f, sys.ActiveParticles().Single().Size, Tol);    // halfway -> lerp(10,2,0.5)
    }

    [Fact]
    public void Color_LerpsFromStartToEndOverLife()
    {
        var cfg = Fixed(0f, new Vector2(1, 0)) with
        {
            MinLife = 1f, MaxLife = 1f,
            StartColor = new Color(1, 0, 0, 1),
            EndColor = new Color(0, 0, 1, 1),
        };
        var sys = new Particle2DSystem(4, 1);
        sys.Emit(cfg, Vector2.Zero, 1);
        Assert.Equal(new Color(1, 0, 0, 1), sys.ActiveParticles().Single().Color);
        sys.Update(0.5f);
        var c = sys.ActiveParticles().Single().Color;
        Assert.Equal(0.5f, c.R, Tol);
        Assert.Equal(0.5f, c.B, Tol);
    }

    [Fact]
    public void Emit_WithTint_MultipliesStartColor()
    {
        var cfg = Fixed(0f, new Vector2(1, 0)) with { StartColor = Color.White, EndColor = Color.White };
        var sys = new Particle2DSystem(4, 1);
        sys.Emit(cfg, Vector2.Zero, new Color(1, 0, 0, 1), 1);
        var c = sys.ActiveParticles().Single().Color;
        Assert.Equal(1f, c.R, Tol);
        Assert.Equal(0f, c.G, Tol);
        Assert.Equal(0f, c.B, Tol);
    }

    [Fact]
    public void Pool_IsRingBuffer_CapsAtCapacity()
    {
        var sys = new Particle2DSystem(capacity: 4, seed: 1);
        sys.Emit(Fixed(10f, new Vector2(1, 0)), Vector2.Zero, 6);
        Assert.Equal(4, sys.ActiveCount);
    }

    [Fact]
    public void Blend_FromConfig_IsExposedOnView()
    {
        var cfg = Fixed(0f, new Vector2(1, 0)) with { Blend = BlendMode.Additive };
        var sys = new Particle2DSystem(4, 1);
        sys.Emit(cfg, Vector2.Zero, 1);
        Assert.Equal(BlendMode.Additive, sys.ActiveParticles().Single().Blend);
    }

    [Fact]
    public void SameSeed_ProducesIdenticalState()
    {
        var cfg = new Particle2DEmitterConfig
        {
            MinLife = 0.5f, MaxLife = 1.5f,
            MinSpeed = 20f, MaxSpeed = 80f,
            Emission = Particle2DEmission.Radial,
            JitterX = 4f, JitterY = 4f,
            StartSize = 3f, EndSize = 0f,
            Acceleration = new Vector2(0, 50f),
            Drag = 0.2f,
            SwayFrequency = 5f, SwayAmplitude = 6f,
            RotationJitter = 3f,
            MinAngularVelocity = -2f, MaxAngularVelocity = 2f,
        };

        var a = new Particle2DSystem(64, 12345);
        var b = new Particle2DSystem(64, 12345);
        a.Emit(cfg, new Vector2(100, 100), 40);
        b.Emit(cfg, new Vector2(100, 100), 40);
        for (int i = 0; i < 10; i++) { a.Update(0.016f); b.Update(0.016f); }

        List<Particle2DView> pa = a.ActiveParticles().ToList();
        List<Particle2DView> pb = b.ActiveParticles().ToList();
        Assert.Equal(pa.Count, pb.Count);
        for (int i = 0; i < pa.Count; i++)
        {
            Assert.Equal(pa[i].Position.X, pb[i].Position.X, Tol);
            Assert.Equal(pa[i].Position.Y, pb[i].Position.Y, Tol);
            Assert.Equal(pa[i].Velocity.X, pb[i].Velocity.X, Tol);
            Assert.Equal(pa[i].Rotation, pb[i].Rotation, Tol);
        }
    }

    [Fact]
    public void DifferentSeed_DivergesState()
    {
        var cfg = Fixed(50f, new Vector2(1, 0)) with { Emission = Particle2DEmission.Radial };
        var a = new Particle2DSystem(64, 1);
        var b = new Particle2DSystem(64, 2);
        a.Emit(cfg, Vector2.Zero, 30);
        b.Emit(cfg, Vector2.Zero, 30);
        a.Update(0.1f); b.Update(0.1f);
        // At least one particle differs between the two seeds.
        var pa = a.ActiveParticles().ToList();
        var pb = b.ActiveParticles().ToList();
        bool anyDiff = pa.Where((t, i) => Vector2.Distance(t.Position, pb[i].Position) > Tol).Any();
        Assert.True(anyDiff, "different seeds should diverge");
    }

    [Fact]
    public void DirectionalEmission_StaysWithinCone()
    {
        var cfg = new Particle2DEmitterConfig
        {
            MinLife = 1f, MaxLife = 1f,
            MinSpeed = 50f, MaxSpeed = 50f,
            Emission = Particle2DEmission.Directional,
            Direction = new Vector2(1, 0),
            SpreadRadians = 0.3f,
        };
        var sys = new Particle2DSystem(64, 7);
        sys.Emit(cfg, Vector2.Zero, 40);
        foreach (var p in sys.ActiveParticles())
            Assert.True(p.Velocity.X > 0f, "directional cone around +X should keep vx positive");
    }

    [Fact]
    public void Clear_RemovesAllParticles()
    {
        var sys = new Particle2DSystem(8, 1);
        sys.Emit(Fixed(10f, new Vector2(1, 0)), Vector2.Zero, 5);
        sys.Clear();
        Assert.Equal(0, sys.ActiveCount);
    }

    // -- Trapezoid alpha envelope (fade in / hold / fade out) --

    // A still, constant-colour particle (no motion, alpha-1 colour that does not lerp) so the only thing
    // touching alpha is the envelope, and its shape over life is hand-computable.
    static Particle2DEmitterConfig Still(float maxLife) => Fixed(0f, new Vector2(1, 0)) with
    {
        MinLife = maxLife,
        MaxLife = maxLife,
        StartColor = Color.White,
        EndColor = Color.White,
    };

    [Fact]
    public void Envelope_FadeIn_RampsAlphaFromZeroToFull()
    {
        var cfg = Still(2f) with { FadeInDuration = 0.5f };
        var sys = new Particle2DSystem(4, 1);
        sys.Emit(cfg, Vector2.Zero, 1);

        Assert.Equal(0f, sys.ActiveParticles().Single().Color.A, Tol);   // spawn: elapsed 0 -> alpha 0
        sys.Update(0.25f);
        Assert.Equal(0.5f, sys.ActiveParticles().Single().Color.A, Tol); // elapsed 0.25 / 0.5 -> 0.5
        sys.Update(0.25f);
        Assert.Equal(1f, sys.ActiveParticles().Single().Color.A, Tol);   // elapsed 0.5 -> full
        sys.Update(0.5f);
        Assert.Equal(1f, sys.ActiveParticles().Single().Color.A, Tol);   // holds at full past the fade-in leg
    }

    [Fact]
    public void Envelope_FadeOut_RampsAlphaToZeroNearDeath()
    {
        var cfg = Still(2f) with { FadeOutDuration = 0.5f };
        var sys = new Particle2DSystem(4, 1);
        sys.Emit(cfg, Vector2.Zero, 1);

        Assert.Equal(1f, sys.ActiveParticles().Single().Color.A, Tol);   // spawn: far from death -> full
        sys.Update(1.5f);
        Assert.Equal(1f, sys.ActiveParticles().Single().Color.A, Tol);   // Life 0.5 == fade-out leg start
        sys.Update(0.25f);
        Assert.Equal(0.5f, sys.ActiveParticles().Single().Color.A, Tol); // Life 0.25 / 0.5 -> 0.5
    }

    [Fact]
    public void Envelope_Trapezoid_HoldsFullBetweenLegs()
    {
        var cfg = Still(2f) with { FadeInDuration = 0.5f, FadeOutDuration = 0.5f };
        var sys = new Particle2DSystem(4, 1);
        sys.Emit(cfg, Vector2.Zero, 1);

        sys.Update(1.0f);   // elapsed 1.0 (>fade-in), Life 1.0 (>fade-out) -> both legs full
        Assert.Equal(1f, sys.ActiveParticles().Single().Color.A, Tol);
    }

    [Fact]
    public void Envelope_Defaults_LeaveColourAlphaUnchanged()
    {
        // No fade-in/out configured: alpha comes purely from the colour, exactly as before the envelope existed.
        var cfg = Still(2f) with
        {
            StartColor = new Color(1, 1, 1, 0.7f),
            EndColor = new Color(1, 1, 1, 0.7f),
        };
        var sys = new Particle2DSystem(4, 1);
        sys.Emit(cfg, Vector2.Zero, 1);
        Assert.Equal(0.7f, sys.ActiveParticles().Single().Color.A, Tol);
        sys.Update(1.0f);
        Assert.Equal(0.7f, sys.ActiveParticles().Single().Color.A, Tol);
    }

    // -- Ambient fields (respawn region + live tint) --

    static Particle2DEmitterConfig FieldCfg(float maxLife, float speed, Vector2 dir) => new()
    {
        MinLife = maxLife,
        MaxLife = maxLife,
        MinSpeed = speed,
        MaxSpeed = speed,
        Emission = Particle2DEmission.Directional,
        Direction = dir,
        SpreadRadians = 0f,
        StartColor = Color.White,
        EndColor = Color.White,
    };

    [Fact]
    public void EmitField_FillsPoolAndReportsField()
    {
        var sys = new Particle2DSystem(capacity: 32, seed: 5);
        int id = sys.EmitField(FieldCfg(4f, 0f, new Vector2(1, 0)), new Rect(0, 0, 100, 100), 32);
        Assert.Equal(0, id);
        Assert.Equal(1, sys.FieldCount);
        Assert.Equal(32, sys.ActiveCount);
    }

    [Fact]
    public void Field_RespawnKeepsPopulationStable()
    {
        // Short lifetimes + drift out of the region force constant deaths/exits; respawns must hold the count.
        var cfg = FieldCfg(0.4f, 30f, new Vector2(0, 1));
        var sys = new Particle2DSystem(capacity: 48, seed: 9);
        sys.EmitField(cfg, new Rect(0, 0, 200, 200), 48);

        for (int i = 0; i < 200; i++)
        {
            sys.Update(0.05f);
            Assert.Equal(48, sys.ActiveCount);   // never drops: a dying/exiting field particle respawns same frame
        }
    }

    [Fact]
    public void Field_RespawnsInsideRegionWhenParticleLeaves()
    {
        var region = new Rect(0, 0, 10, 10);
        // Long life (won't die) + a fast rightward velocity so it clears the region in one step.
        var cfg = FieldCfg(1000f, 1000f, new Vector2(1, 0));
        var sys = new Particle2DSystem(capacity: 1, seed: 3);
        sys.EmitField(cfg, region, 1);

        Particle2DView before = sys.ActiveParticles().Single();
        Assert.InRange(before.Position.X, region.X, region.Right);

        sys.Update(0.1f);   // +100px on X -> well outside the 10px region -> respawn in-region

        Assert.Equal(1, sys.ActiveCount);
        Particle2DView after = sys.ActiveParticles().Single();
        Assert.InRange(after.Position.X, region.X, region.Right);
        Assert.InRange(after.Position.Y, region.Y, region.Bottom);
    }

    [Fact]
    public void Field_ExitMargin_AllowsDriftBeforeRespawn()
    {
        var region = new Rect(0, 0, 10, 10);
        var cfg = FieldCfg(1000f, 50f, new Vector2(1, 0));   // +5px per 0.1s step
        var sys = new Particle2DSystem(capacity: 1, seed: 2);
        sys.EmitField(cfg, region, Color.White, 1, exitMargin: 100f);

        // With a 100px margin the particle drifts past the region edge without respawning.
        for (int i = 0; i < 3; i++) sys.Update(0.1f);
        Assert.True(sys.ActiveParticles().Single().Position.X > region.Right,
            "a particle within the exit margin should keep drifting, not respawn");
    }

    [Fact]
    public void SetFieldTint_RecoloursLiveParticlesImmediately()
    {
        var sys = new Particle2DSystem(capacity: 4, seed: 1);
        int id = sys.EmitField(FieldCfg(10f, 0f, new Vector2(1, 0)), new Rect(0, 0, 50, 50), 4);

        Assert.Equal(1f, sys.ActiveParticles().First().Color.G, Tol);   // white field particle

        sys.SetFieldTint(id, new Color(1, 0, 0, 1));                    // -> red
        Particle2DView p = sys.ActiveParticles().First();
        Assert.Equal(1f, p.Color.R, Tol);
        Assert.Equal(0f, p.Color.G, Tol);
        Assert.Equal(0f, p.Color.B, Tol);
    }

    [Fact]
    public void Clear_AlsoClearsFields()
    {
        var sys = new Particle2DSystem(capacity: 8, seed: 1);
        sys.EmitField(FieldCfg(4f, 0f, new Vector2(1, 0)), new Rect(0, 0, 50, 50), 8);
        Assert.Equal(1, sys.FieldCount);
        sys.Clear();
        Assert.Equal(0, sys.ActiveCount);
        Assert.Equal(0, sys.FieldCount);
    }

    // -- Live-set compaction (swap-remove) correctness --
    //
    // Update/Draw/ActiveCount walk an O(live) sparse-set index instead of scanning the full pool; a burst
    // particle's death swap-removes its slot from that index. These tests pin down that the swap-remove keeps
    // exactly the right particles live (not off-by-one neighbours), correctly re-visits the slot swapped into a
    // just-vacated index within the SAME Update call, and reconciles correctly when the ring buffer overwrites a
    // still-live slot.

    // Distinct, short-lived particles so a single Update() kills several interleaved with survivors in one pass
    // (life expires exactly at dt, so "dies this Update" is deterministic) - and X-origin identifies each one.
    static Particle2DEmitterConfig Life(float life) => Fixed(0f, new Vector2(1, 0)) with { MinLife = life, MaxLife = life };

    [Fact]
    public void Update_SwapRemovesOnlyDeadBurstParticles_SurvivorsUnaffected()
    {
        var sys = new Particle2DSystem(capacity: 5, seed: 1);
        // Slots 0..4, alternating short (dies at dt=0.1) / long (survives) life, identified by origin X.
        float[] lives = { 0.1f, 100f, 0.1f, 100f, 0.1f };
        for (int i = 0; i < lives.Length; i++)
            sys.Emit(Life(lives[i]), new Vector2(i, 0), 1);
        Assert.Equal(5, sys.ActiveCount);

        sys.Update(0.1f);   // kills slots 0, 2, 4 in the same pass; slots 1, 3 must survive untouched

        Assert.Equal(2, sys.ActiveCount);
        var survivorX = sys.ActiveParticles().Select(p => p.Position.X).OrderBy(x => x).ToList();
        Assert.Equal(new[] { 1f, 3f }, survivorX);
    }

    [Fact]
    public void Update_ConsecutiveDeathsInOnePass_AllProcessed_NoneSkipped()
    {
        // Three consecutive deaths followed by one survivor stress-tests the "back the loop index up after a
        // swap-remove" logic: if a swapped-in slot were skipped, a still-short-lived particle would survive
        // this Update uncounted (ActiveCount too high) or get missed on death bookkeeping.
        var sys = new Particle2DSystem(capacity: 4, seed: 1);
        float[] lives = { 0.1f, 0.1f, 0.1f, 100f };
        for (int i = 0; i < lives.Length; i++)
            sys.Emit(Life(lives[i]), new Vector2(i, 0), 1);

        sys.Update(0.1f);

        Assert.Equal(1, sys.ActiveCount);
        Assert.Equal(3f, sys.ActiveParticles().Single().Position.X);
    }

    [Fact]
    public void RingOverwrite_OfStillLiveSlot_RemovesTheOverwrittenParticleFromLiveSet()
    {
        var sys = new Particle2DSystem(capacity: 2, seed: 1);
        sys.Emit(Life(100f), new Vector2(0, 0), 1);   // slot 0
        sys.Emit(Life(100f), new Vector2(1, 0), 1);   // slot 1
        Assert.Equal(2, sys.ActiveCount);

        sys.Emit(Life(100f), new Vector2(9, 0), 1);   // ring wraps: overwrites still-live slot 0

        Assert.Equal(2, sys.ActiveCount);   // count unchanged: one replaced, one untouched
        var xs = sys.ActiveParticles().Select(p => p.Position.X).OrderBy(x => x).ToList();
        Assert.Equal(new[] { 1f, 9f }, xs);   // the original slot-0 particle (X=0) is gone, not double-counted
    }

    [Fact]
    public void EmitAfterAllDead_RepopulatesDeadSlots_LiveSetStaysAccurate()
    {
        var sys = new Particle2DSystem(capacity: 3, seed: 1);
        sys.Emit(Life(0.1f), Vector2.Zero, 3);
        sys.Update(0.1f);
        Assert.Equal(0, sys.ActiveCount);

        sys.Emit(Life(100f), new Vector2(5, 0), 2);

        Assert.Equal(2, sys.ActiveCount);
        Assert.All(sys.ActiveParticles(), p => Assert.Equal(5f, p.Position.X));
    }

    [Fact]
    public void Clear_ThenEmit_LiveSetIsCleanNotStale()
    {
        var sys = new Particle2DSystem(capacity: 4, seed: 1);
        sys.Emit(Life(100f), Vector2.Zero, 4);
        sys.Clear();

        sys.Emit(Life(100f), new Vector2(7, 0), 2);

        Assert.Equal(2, sys.ActiveCount);
        Assert.All(sys.ActiveParticles(), p => Assert.Equal(7f, p.Position.X));
    }
}
