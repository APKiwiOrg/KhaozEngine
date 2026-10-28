using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public sealed class PhysicsRebasePerfTests
{
    /// <summary>
    /// THE COST BUDGET, as an acceptance condition rather than an estimate. One rebase at a resident streaming
    /// scale must stay within a small multiple of one physics step on the same world, because the regression this
    /// guards is an unbounded refit, and that costs a rebase MULTIPLES of a step rather than a few percent.
    /// <para>
    /// WHY IT IS NOT <c>rebase &lt; step</c> ANY MORE (issue 466). That form asserted at the true value of the
    /// quantity it measured: a rebase at this scale costs about what a step costs, so the verdict sat on the
    /// boundary and any noise at all decided it. It reddened the self-hosted macOS leg three times, and that leg
    /// is the primary dev Mac, shared with interactive work. Run 30737392817 failed and then failed AGAIN on an
    /// idle rerun. Run 30795099275 measured a 54.211 ms rebase against a 13.456 ms step, a 4.03x flip, on a
    /// machine concurrently running three test suites and a build. Run 30800593925 measured 3.758 ms against
    /// 2.848 ms, a 1.32x flip in which BOTH numbers are healthy and only the ORDER was wrong.
    /// </para>
    /// <para>
    /// THE MEASUREMENT. The samples are INTERLEAVED, one step and one rebase pair per iteration, so a scheduler
    /// burst lands inside the same iteration as the step it is compared against rather than wholly inside one of
    /// two sequential measurement blocks. Block separation is what produced both flips above: on 30795099275 the
    /// rebase block came out 54x its idle cost while the step block was barely 4x, and on 30800593925 the rebase
    /// block was inflated where the step block was not. And the verdict is the MEDIAN of the five per-iteration
    /// ratios rather than one number divided by another, so a preempted sample cannot decide it. Three of the
    /// five have to go the same way first, and every ratio carries its own correction for machine load, because
    /// both of its halves met that machine within a few milliseconds of each other.
    /// </para>
    /// <para>
    /// WHERE 3x COMES FROM, measured on the same Mac that hosts the self-hosted leg. Unloaded, the ratio is
    /// about 0.32x, a rebase being CHEAPER than a step at this scale (roughly 1.0 ms against 3.1 ms). Under a
    /// continuous solution rebuild holding load average between 42 and 52 on 12 cores, which is heavier than any
    /// of the three incidents, five consecutive runs put the median ratio at 0.34, 0.45, 0.35, 0.57 and 0.33,
    /// and the worst SINGLE iteration out of those 25 reached 0.92x. So the bound sits 5.3x above the worst
    /// median a saturated machine produced and 3.3x above its worst individual sample, and it clears the worst
    /// residual noise the OLD formulation recorded on healthy numbers, 30800593925's 1.32x, by 2.3x. It
    /// deliberately does not try to absorb 30795099275's 4.03x, because interleaving removes that shape instead
    /// of tolerating it, and a bound wide enough to swallow it would assert nothing at all. What 3x still fails
    /// is the regression this test exists for: an unbounded refit, or the <c>Statics.ApplyDescription</c> form
    /// that wakes the whole sleeping population, costs a rebase an ORDER of magnitude and not a third.
    /// </para>
    /// <para>
    /// Deterministic call-count, allocation and unsafe static-API checks gate ordinary Game.Tests. They cannot
    /// observe extra visits inside Bepu's native tree. This hosted-only measurement guards that remaining class.
    /// Issue 1512 recurred even with isolation and interleaving, so timing is diagnostic and never a merge gate.
    /// The five samples and 3x threshold stay fixed. A ratio above 3x fails this separate performance job.
    /// </para>
    /// </summary>
    [Fact]
    public void Rebase_StaysWithinThreeSteps()
    {
        // Refuse accidental local execution. Build locally, measure only in the dedicated hosted workflow.
        Assert.Equal("true", Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));
        Assert.Equal("physics-rebase-perf", Environment.GetEnvironmentVariable("GITHUB_WORKFLOW"));
        string summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") ?? "";
        Assert.False(string.IsNullOrWhiteSpace(summaryPath));
        using BepuPhysicsWorld world = PhysicsRebaseResidentWorld.CreateWarmed();
        var steps = new double[5];
        var rebases = new double[5];
        for (int i = 0; i < steps.Length; i++)
        {
            long beforeStep = Stopwatch.GetTimestamp();
            world.Step(1f / 60f);
            long afterStep = Stopwatch.GetTimestamp();
            world.Rebase(PhysicsRebaseResidentWorld.Shift);
            world.Rebase(Vector3.Zero);
            long afterRebase = Stopwatch.GetTimestamp();
            steps[i] = Stopwatch.GetElapsedTime(beforeStep, afterStep).TotalSeconds;
            rebases[i] = Stopwatch.GetElapsedTime(afterStep, afterRebase).TotalSeconds / 2d;
        }
        PhysicsRebaseTimingResult result = PhysicsRebaseTiming.Summarize(steps, rebases);
        // Publish before the assertion so regressions retain the ratio and each original paired sample.
        File.AppendAllText(summaryPath, result.Summary);
        Assert.True(result.Passed, result.Summary);
    }
}
