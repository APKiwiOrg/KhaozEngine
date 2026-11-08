using System;
using System.IO;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.Fixtures.ContactBakeFile;

namespace KhaozEngine.Tests.Movement;

public class ContactDropPayloadBoundTests
{
    [Fact]
    public void FullyPopulatedSingleLayerPayloadIncludesTheNewDropAcceptanceByte()
    {
        var tuning = ContactDropFixture.Tuning with { Water = new(WaterTraversalMode.WadeOnly) };
        var options = new PhysicsNavBakeOptions(0, 0, 2, 2, 1, 5, 6, tuning.MaxSlopeRadians, 4, 4, 1)
        { SampleWater = true };
        var profile = new NavBakeProfile("player", tuning, default) { MaxDropMetres = 6 };
        var expected = new NavBakeExpectation(options, Sources(), [profile]);
        var payload = new NavBakeWriter();
        payload.WriteInt32(2);
        payload.WriteInt32(2);
        for (int i = 0; i < 3; i++) payload.WriteSingle(0);
        payload.WriteInt32(4);
        for (int i = 0; i < 4; i++) payload.WriteUInt8(1);
        float[] heights = [4, 0, 0, 4];
        foreach (float height in heights)
        {
            payload.WriteSingle(height);
            payload.WriteSingle(float.PositiveInfinity);
            payload.WriteUInt32(0);
        }
        payload.WriteInt32(4);
        for (int i = 0; i < 4; i++)
        {
            payload.WriteInt32(i);
            payload.WriteSingle(heights[i] + 0.1f);
            payload.WriteUInt32(0);
        }
        payload.WriteInt32(1);
        payload.WriteInt32(2);
        payload.WriteInt32(2);
        payload.WriteSingle(1);
        for (int i = 0; i < 3; i++) payload.WriteSingle(0);
        payload.WriteSingle(float.NegativeInfinity);
        payload.WriteSingle(float.PositiveInfinity);
        payload.WriteUInt8(0);
        foreach (float height in heights) payload.WriteSingle(height);
        for (int i = 0; i < 4; i++) payload.WriteUInt8(0);
        foreach (float height in heights) payload.WriteSingle(height);
        // Each upper corner has two lower cardinal neighbours. All four candidate verdicts are false.
        payload.WriteInt32(4);
        payload.WriteUInt8(0);
        byte[] bytes = payload.ToArray();
        // The frozen bound for C=4, M=1, Lmax=1, one wet Contact profile is 205 bytes.
        Assert.Equal(206, bytes.Length);
        byte[] file = Container(NavBakeIdentity.Encode(expected, NavBakeIdentity.CurrentEngineVersion), bytes, 3);
        var loaded = GroundNavigationBake.Load(new MemoryStream(file), expected);
        Assert.Equal(NavBakeLoadStatus.Loaded, loaded.Status);
        Assert.Equal(file, Write(loaded.Bake!));
    }
}
