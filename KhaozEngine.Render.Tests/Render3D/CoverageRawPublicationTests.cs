using System;
using System.Linq;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageRawPublicationTests
{
    [Fact]
    public void Hole_publication_selects_every_uncovered_word_even_when_candidates_are_valid()
    {
        var fixture = new CoverageCarryFailureFixture();
        fixture.FailingGather(fail: false);
        fixture.Frames[6 + 15] = 1;
        fixture.Publish();
        Assert.Equal(0u, fixture.Errors[6]);
        Assert.Equal(fixture.Expected, fixture.Current);
    }

    [Fact]
    public void Hole_runs_the_same_five_kernels_and_publishes_the_complete_current_pose()
    {
        var fixture = new CoverageCarryFailureFixture();
        fixture.RawHole();
        Assert.Equal(fixture.Expected, fixture.Current);
        Assert.DoesNotContain(CoverageCarryFailureFixture.Poison, fixture.Current);
        using var batch = new CoverageCarryGpuFixture();
        Assert.True(batch.Batch.TryAcquireOutput(batch.Actor, out int slot, out _));
        batch.Actor.Request.Generation = 0; // A refused table key has no accepted host preparation.
        Assert.True(batch.Batch.TryStageRaw(batch.Actor, slot, batch.Host.Palette,
            System.Numerics.Matrix4x4.Identity, out _));
        using var commands = new CoverageCarryTestDevice.Commands();
        commands.Begin(); batch.Batch.Record(commands); commands.End();
        Assert.Equal(5, commands.Dispatches.Count);
    }
}
