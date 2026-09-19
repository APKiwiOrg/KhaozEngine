using System;
using System.Numerics;
using KhaozEngine.SegmentRig;
using Xunit;

namespace KhaozEngine.Tests.SegmentRig;

/// <summary>
/// THE EXPLICIT PHASE SOURCE, which is what a body with no ground contact drives its cycle from.
/// </summary>
/// <remarks>
/// <see cref="WalkCycle.Advance(Vector3, float)"/> derives phase from HORIZONTAL ground distance and
/// discards y on purpose, because a metre of climb is not a metre of walk. A swimmer, a faller and a body
/// treading water cover no ground at all, so on that path they stand frozen with a leg wherever it was. The
/// second overload takes the phase delta from the caller instead.
/// <para>Two halves are worth pinning separately: that the new path turns the cycle over with zero ground
/// covered, and that the OLD path is byte for byte what it always was, because every existing consumer is on
/// it.</para>
/// </remarks>
public class NoGroundPhaseTests
{
    const float Dt = 1f / 60f;

    [Fact]
    public void ThePhaseAdvancesWithNoGroundCoveredAtAll()
    {
        var cycle = new WalkCycle(BodyRig.Human);
        Assert.Equal(0f, cycle.Phase);

        // Two strokes a second, standing exactly still. One second is two whole cycles, so the phase wraps
        // twice and lands back where it started.
        float swum = 0f;
        for (float t = 0f; t < 1f - (Dt * 0.5f); t += Dt)
        {
            cycle.Advance(phaseDelta: 2f * Dt, Dt, moving: true);
            swum += 2f * Dt;
        }
        Assert.Equal(2f, swum, 3);
        Assert.Equal(0f, cycle.Phase, 2);
        // A quarter of a cycle more puts it a quarter round, which is the claim that it moved at all rather
        // than never leaving zero.
        cycle.Advance(phaseDelta: 0.25f, Dt, moving: true);
        Assert.Equal(0.25f, cycle.Phase, 4);

        // The blend saturated over the same seconds the ground path would take, so the pose is a real one.
        Assert.Equal(1f, cycle.Weight, 3);
        Assert.NotEqual(WalkPose.Rest, cycle.Pose);

        // The phase WRAPS the same way, so a caller handing over more than a whole cycle is fine.
        cycle.Advance(phaseDelta: 3.5f, Dt, moving: true);
        Assert.InRange(cycle.Phase, 0f, 1f);
        Assert.Equal(0.75f, cycle.Phase, 3);

        // And negative runs it backward, which is what a backpedal wants.
        cycle.Advance(phaseDelta: -0.5f, Dt, moving: true);
        Assert.Equal(0.25f, cycle.Phase, 3);
    }

    [Fact]
    public void AFallHoldsItsLegsAndStillEasesTheBlendOut()
    {
        // A body in the air with no cycle of its own: zero phase delta, not moving. The legs stay exactly
        // where the last stride left them while the blend eases out, rather than snapping to rest.
        var cycle = new WalkCycle(BodyRig.Human);
        var at = Vector3.Zero;
        cycle.Advance(at, Dt);
        for (int i = 0; i < 60; i++)
        {
            at += new Vector3(0f, 0f, 2f * Dt);
            cycle.Advance(at, Dt);
        }
        Assert.Equal(1f, cycle.Weight, 3);
        float phase = cycle.Phase;

        cycle.Advance(phaseDelta: 0f, Dt, moving: false);
        Assert.Equal(phase, cycle.Phase);
        Assert.InRange(cycle.Weight, 0.5f, 1f);

        for (float t = 0f; t < WalkCycle.BlendSeconds; t += Dt) cycle.Advance(0f, Dt, moving: false);
        Assert.Equal(0f, cycle.Weight);
        Assert.Equal(phase, cycle.Phase);
        Assert.Equal(WalkPose.Rest, cycle.Pose);
    }

    [Fact]
    public void TheCallerDecidesWhetherItCountsAsRunning()
    {
        // There is no ground speed to threshold against, so the run lean is the caller's to declare. Both
        // blends ease over the same BlendSeconds the ground path uses.
        var cycle = new WalkCycle(BodyRig.Human);
        for (float t = 0f; t < WalkCycle.BlendSeconds * 2f; t += Dt)
            cycle.Advance(0.02f, Dt, moving: true, running: true);
        Assert.Equal(1f, cycle.RunWeight, 3);
        Assert.Equal(WalkCycle.RunLeanRadians, cycle.Pose.Lean, 3);

        for (float t = 0f; t < WalkCycle.BlendSeconds * 2f; t += Dt)
            cycle.Advance(0.02f, Dt, moving: true, running: false);
        Assert.Equal(0f, cycle.RunWeight);
        Assert.Equal(0f, cycle.Pose.Lean);
    }

    /// <summary>
    /// THE SEAM. A body that swam thirty metres and then waded ashore hands the ground overload one frame
    /// carrying the whole swim, which reads as a sprint and spins the phase. The phase overload forgets the
    /// last sampled position for exactly that reason, so alternating the two needs no
    /// <see cref="WalkCycle.Teleport"/> call in between.
    /// </summary>
    [Fact]
    public void ComingBackToGroundAfterAPhaseDrivenStretchIsNotOneFrameOfSprinting()
    {
        var cycle = new WalkCycle(BodyRig.Human);
        cycle.Advance(new Vector3(0f, 0f, 0f), Dt);
        cycle.Advance(new Vector3(0f, 0f, 0.02f), Dt);

        // Thirty metres of swimming, which the ground path never saw.
        for (int i = 0; i < 60; i++) cycle.Advance(0.03f, Dt, moving: true);
        float phase = cycle.Phase;

        // Back on the ground, thirty metres away. The first sample measures nothing.
        cycle.Advance(new Vector3(0f, 0f, 30f), Dt);
        Assert.Equal(phase, cycle.Phase, 5);
        // The frame after that measures a normal step, so the cycle carries on from where the swim left it.
        cycle.Advance(new Vector3(0f, 0f, 30.02f), Dt);
        Assert.Equal(phase + (0.02f / WalkCycle.StrideMetres), cycle.Phase, 5);
    }

    /// <summary>
    /// THE GROUND OVERLOAD IS UNCHANGED, which is the half that matters to everything already written
    /// against it. Its phase, its two blends and its first-sample behaviour are pinned against a hand
    /// computation rather than against itself.
    /// </summary>
    [Fact]
    public void TheDistanceDrivenOverloadStillBehavesExactly()
    {
        var cycle = new WalkCycle(BodyRig.Human);

        // The first call samples only: no phase, no weight, whatever the coordinate.
        cycle.Advance(new Vector3(120f, 0f, -80f), Dt);
        Assert.Equal(0f, cycle.Phase);
        Assert.Equal(0f, cycle.Weight);

        // One step of exactly a tenth of the stride is exactly a tenth of a cycle.
        var at = new Vector3(120f, 0f, -80f);
        at += new Vector3(0f, 0f, WalkCycle.StrideMetres * 0.1f);
        cycle.Advance(at, Dt);
        Assert.Equal(0.1f, cycle.Phase, 5);
        Assert.Equal(Dt / WalkCycle.BlendSeconds, cycle.Weight, 5);

        // The Y is still DISCARDED: a pure climb covers no walk at all.
        float phase = cycle.Phase;
        at += new Vector3(0f, 5f, 0f);
        cycle.Advance(at, Dt);
        Assert.Equal(phase, cycle.Phase, 5);

        // And a horizontal step past the run threshold still drives the run blend off the speed itself.
        var fast = new WalkCycle(BodyRig.Human);
        var here = Vector3.Zero;
        fast.Advance(here, Dt);
        for (int i = 0; i < 60; i++)
        {
            here += new Vector3(0f, 0f, (WalkCycle.RunMetresPerSecond + 1f) * Dt);
            fast.Advance(here, Dt);
        }
        Assert.Equal(1f, fast.RunWeight, 3);
    }
}
