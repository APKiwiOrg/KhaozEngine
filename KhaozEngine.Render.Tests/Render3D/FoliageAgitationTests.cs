using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class FoliageAgitationTests
{
    [Fact]
    public void AgitationDefaultsAndRanges()
    {
        var agitation = new FoliageAgitation();
        Assert.Equal((2f, .6f, 5f), (agitation.ReferenceSpeed, agitation.SettleTime, agitation.TeleportResetDistance));
        FoliageAgitation zero = default;
        Assert.Equal((2f, .6f, 5f), (zero.ReferenceSpeed, zero.SettleTime, zero.TeleportResetDistance));
        Assert.Equal(0f, zero.Update(Vector3.Zero, .1f));
        Assert.Equal(1f, zero.Update(new Vector3(.2f, 0f, 0f), .1f));
        Assert.Equal(0f, new FoliageInteractor(Vector3.Zero, 1f).Agitation);
        var actor = new FoliageInteractor(Vector3.One, 2f, .5f, 1f);
        actor.Validate();
        (Vector3 position, float radius, float strength) = actor;
        Assert.Equal((Vector3.One, 2f, .5f), (position, radius, strength));
        foreach (float invalid in new[] { -1f, 0f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Assert.Equal("referenceSpeed", Assert.Throws<ArgumentOutOfRangeException>(() => new FoliageAgitation(referenceSpeed: invalid)).ParamName);
            Assert.Equal("settleTime", Assert.Throws<ArgumentOutOfRangeException>(() => new FoliageAgitation(settleTime: invalid)).ParamName);
            Assert.Equal("teleportResetDistance", Assert.Throws<ArgumentOutOfRangeException>(() => new FoliageAgitation(teleportResetDistance: invalid)).ParamName);
        }
        foreach (float invalid in new[] { -.01f, 1.01f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Assert.Equal("Agitation", Assert.Throws<ArgumentOutOfRangeException>(() =>
                new FoliageInteractor(Vector3.Zero, 1f, 1f, invalid).Validate()).ParamName);
    }

    [Fact]
    public void RiseSettleAndTeleportReset()
    {
        var agitation = new FoliageAgitation();
        Assert.Equal(0f, agitation.Update(Vector3.Zero, .1f));
        Assert.Equal(1f, agitation.Update(new Vector3(.2f, 0f, 0f), .1f));
        Assert.Equal(.5f, agitation.Update(new Vector3(.2f, 0f, 0f), .3f), 1e-6f);
        Assert.Equal(0f, agitation.Update(new Vector3(.2f, 0f, 0f), .3f));
        Assert.Equal(0f, agitation.Update(new Vector3(6f, 0f, 0f), .1f));
        Assert.Equal(1f, agitation.Update(new Vector3(6.2f, 0f, 0f), .1f), 1e-5f);
        agitation.Reset();
        Assert.Equal(0f, agitation.Value);
        Assert.Equal(0f, agitation.Update(Vector3.Zero, .1f));
        Assert.Equal(.25f, agitation.Update(new Vector3(.05f, 0f, 0f), .1f), 1e-6f);
        var custom = new FoliageAgitation(4f, 2f, 10f);
        custom.Update(Vector3.Zero, .1f);
        Assert.Equal(.5f, custom.Update(Vector3.UnitX * .2f, .1f));
        Assert.Equal(.25f, custom.Update(Vector3.UnitX * .2f, .5f));
        custom.Reset();
        Assert.Equal((4f, 2f, 10f), (custom.ReferenceSpeed, custom.SettleTime, custom.TeleportResetDistance));
        agitation.Reset();
        agitation.Update(Vector3.Zero, .1f);
        Assert.Equal(1f, agitation.Update(Vector3.UnitX * 5f, .1f));
        Assert.Equal(0f, agitation.Update(new Vector3(-float.MaxValue, float.MaxValue, float.MaxValue), float.Epsilon));
        Assert.Equal(0f, agitation.Update(new Vector3(float.MaxValue, -float.MaxValue, -float.MaxValue), float.Epsilon));
    }

    [Fact]
    public void ZeroTimeRebasesWithoutAgitatingAndInvalidInputPreservesState()
    {
        var agitation = new FoliageAgitation();
        Assert.Equal(0f, agitation.Update(Vector3.Zero, 0f));
        Assert.Equal(0f, agitation.Update(new Vector3(2f, 0f, 0f), 0f));
        Assert.Equal(0f, agitation.Update(new Vector3(2f, 0f, 0f), .1f));
        Assert.Equal(1f, agitation.Update(new Vector3(2.2f, 0f, 0f), .1f), 1e-5f);
        Assert.Equal(agitation.Value, agitation.Update(Vector3.UnitX * 100f, 0f));
        FoliageAgitation unchanged = agitation;
        foreach (float invalid in new[] { -.1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Assert.Equal("deltaTime", Assert.Throws<ArgumentOutOfRangeException>(() => agitation.Update(Vector3.Zero, invalid)).ParamName);
        foreach (Vector3 invalid in new[] { new Vector3(float.NaN, 0f, 0f), new Vector3(0f, float.PositiveInfinity, 0f), new Vector3(0f, 0f, float.NegativeInfinity) })
            Assert.Equal("position", Assert.Throws<ArgumentException>(() => agitation.Update(invalid, .1f)).ParamName);
        Assert.Equal(unchanged.Value, agitation.Value);
        Assert.Equal(unchanged.Update(Vector3.UnitX * 100f, .3f), agitation.Update(Vector3.UnitX * 100f, .3f));
    }

    [Fact]
    public void VerticalMotionDoesNotAgitate()
    {
        var agitation = new FoliageAgitation();
        agitation.Update(Vector3.Zero, .1f);
        Assert.Equal(0f, agitation.Update(Vector3.UnitY, .1f));
        Assert.Equal(1f, agitation.Update(new Vector3(.2f, 1f, 0f), .1f));
        Assert.Equal(0f, agitation.Update(new Vector3(.2f, 7f, 0f), .1f));
    }

    [Fact]
    public void UpdateAllocatesNothing()
    {
        var agitation = new FoliageAgitation();
        agitation.Update(Vector3.Zero, .1f);
        long before = GC.GetAllocatedBytesForCurrentThread();
        float value = agitation.Update(Vector3.UnitX * .2f, .1f);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(1f, value);
        Assert.Equal(0, allocated);
    }
}
