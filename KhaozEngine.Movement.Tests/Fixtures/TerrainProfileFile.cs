using System;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;

namespace KhaozEngine.Tests.Movement.Fixtures;

/// <summary>Internally sealed format-4 files whose candidate semantics are independently controlled.</summary>
internal static class TerrainProfileFile
{
    internal static byte[] Create(NavBakeProfile profile, float[][] heights, NavLink[] candidates,
        out NavBakeExpectation expected, bool accepted = true)
    {
        var options = new PhysicsNavBakeOptions(0, 0, 2, 1, 1, 10, 14, profile.Tuning.MaxSlopeRadians,
            2, 4, MaxSurfacesPerColumn: 2)
        { CaptureTerrainDescentEvidence = true };
        expected = new(options, ContactBakeFile.Sources(), [profile]);
        byte[] identity = NavBakeIdentity.Encode(expected, NavBakeIdentity.CurrentEngineVersion);
        var data = new NavBakeWriter();
        data.WriteInt32(heights.Length);
        foreach (var layer in heights)
        {
            data.WriteInt32(2); data.WriteInt32(1); data.WriteSingle(1);
            data.WriteSingle(0); data.WriteSingle(0); data.WriteSingle(0);
            data.WriteSingle(float.NegativeInfinity); data.WriteSingle(float.PositiveInfinity);
            data.WriteUInt8(0);
            foreach (float y in layer) data.WriteSingle(y);
            data.WriteUInt8(0); data.WriteUInt8(0);
            foreach (float y in layer) data.WriteSingle(y);
        }
        byte[] bits = new byte[(candidates.Length + 7) / 8];
        for (int i = 0; accepted && i < candidates.Length; i++) bits[i >> 3] |= (byte)(1 << (i & 7));
        data.WriteInt32(candidates.Length); data.WriteBytes(bits);
        var facts = new NavBakeWriter();
        facts.WriteSingle(profile.AllowTerrainDescents ? 0.5f : 0);
        facts.WriteUInt64(BitConverter.DoubleToUInt64Bits(2));
        byte[] endpoints = TerrainCandidateCodec.Encode(candidates, 2, 1);
        facts.WriteInt32(endpoints.Length); facts.WriteBytes(endpoints); facts.WriteBytes(new byte[bits.Length]);
        // No terrain route needs evidence. The four canonical zero counts encode an empty sparse pool.
        byte[] evidence = [0, 0, 0, 0];
        var payload = new NavBakeWriter();
        payload.WriteInt32(2); payload.WriteInt32(1);
        payload.WriteSingle(0); payload.WriteSingle(0); payload.WriteSingle(0);
        payload.WriteInt32(2); payload.WriteUInt8(1); payload.WriteUInt8(1);
        for (int i = 0; i < 2; i++)
        { payload.WriteSingle(heights[0][i]); payload.WriteSingle(float.PositiveInfinity); payload.WriteUInt32(0); }
        payload.WriteInt32(0);
        payload.WriteInt32(evidence.Length); payload.WriteBytes(evidence);
        payload.WriteInt32(data.Length); payload.WriteBytes(data.WrittenSpan);
        payload.WriteInt32(facts.Length); payload.WriteBytes(facts.WrittenSpan);
        payload.WriteBytes(TerrainWorkload.Digest(identity, evidence, profile.Name, data.WrittenSpan, facts.WrittenSpan));
        return ContactBakeFile.Container(identity, payload.ToArray(), 4);
    }
}
