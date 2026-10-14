using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;

namespace KhaozEngine.Tests.Movement.Fixtures;

internal static class ContactBakeFile
{
    internal static PhysicsNavBakeOptions FlatOptions => new(-0.5f, -0.5f, 0.5f, 0.5f, 1, 5, 6,
        ContactNavigationFixture.Tuning.MaxSlopeRadians, 1, 1, MaxSurfacesPerColumn: 1);

    internal static NavBakeSources Sources() => new NavBakeSources().Add("world", new byte[32]);

    internal static GroundNavigationBake Bake(GroundMoveContext context, PhysicsNavBakeOptions options,
        params NavBakeProfile[] profiles)
    {
        using var capture = PhysicsNavBake.Capture(context, options, _ => 0);
        return GroundNavigationBake.Create(capture, Sources(), profiles);
    }

    internal static byte[] Write(GroundNavigationBake bake) => GroundNavigationBakeRoundTripTests.Write(bake);

    internal static GroundNavigationBake Load(byte[] file, NavBakeExpectation expected)
        => GroundNavigationBakeRoundTripTests.LoadOk(file, expected);

    // Hand-authored format 2 fixture. The legacy encoder supplies only the unchanged identity prefix and tuning block.
    internal static byte[] FlatFile(int width, int height, MoveTuning tuning, float resting,
        out NavBakeExpectation expected, bool sampleWater = false)
    {
        int cells = width * height;
        var options = new PhysicsNavBakeOptions(0, 0, width, height, 1, 5, 6, tuning.MaxSlopeRadians,
            cells, cells, MaxSurfacesPerColumn: 1)
        { SampleWater = sampleWater };
        var profile = new NavBakeProfile("player", tuning, default);
        expected = new(options, Sources(), [profile]);
        byte[] prefix = NavBakeIdentity.Encode(expected with
        { Profiles = [profile with { Tuning = tuning with { Controller = MovementControllerKind.Legacy, FootRadiusFraction = 0.5f, Water = default } }] },
            NavBakeIdentity.CurrentEngineVersion);
        var identity = new NavBakeWriter();
        identity.WriteBytes(prefix);
        identity.WriteUInt8(1);
        identity.WriteSingle(tuning.FootRadiusFraction);
        identity.WriteUInt8((byte)tuning.Water.Mode);
        identity.WriteSingle(tuning.Water.SurfaceJumpSpeed);
        identity.WriteSingle(0);
        var payload = new NavBakeWriter();
        payload.WriteInt32(width);
        payload.WriteInt32(height);
        payload.WriteSingle(0);
        payload.WriteSingle(0);
        payload.WriteSingle(0);
        payload.WriteInt32(cells);
        for (int i = 0; i < cells; i++) payload.WriteUInt8(1);
        for (int i = 0; i < cells; i++)
        {
            payload.WriteSingle(0);
            payload.WriteSingle(float.PositiveInfinity);
            payload.WriteUInt32(0);
        }
        payload.WriteInt32(sampleWater ? cells : 0);
        if (sampleWater)
            for (int i = 0; i < cells; i++)
            {
                payload.WriteInt32(i);
                payload.WriteSingle(0.1f);
                payload.WriteUInt32(0);
            }
        payload.WriteInt32(1);
        payload.WriteInt32(width);
        payload.WriteInt32(height);
        payload.WriteSingle(1);
        payload.WriteSingle(0);
        payload.WriteSingle(0);
        payload.WriteSingle(0);
        payload.WriteSingle(float.NegativeInfinity);
        payload.WriteSingle(float.PositiveInfinity);
        payload.WriteBytes(new byte[(cells + 7) / 8]);
        for (int i = 0; i < cells; i++) payload.WriteSingle(0);
        for (int i = 0; i < cells; i++)
        {
            int x = i % width, z = i / width;
            byte mask = 0;
            (int X, int Z)[] directions = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)];
            for (int d = 0; d < directions.Length; d++)
                if (x + directions[d].X >= 0 && x + directions[d].X < width &&
                    z + directions[d].Z >= 0 && z + directions[d].Z < height) mask |= (byte)(1 << d);
            payload.WriteUInt8(mask);
        }
        for (int i = 0; i < cells; i++) payload.WriteSingle(resting);
        payload.WriteInt32(0);
        return Container(identity.ToArray(), payload.ToArray(), 2);
    }

    internal static byte[] Container(byte[] identity, byte[] payload, ushort version)
    {
        var header = new NavBakeWriter();
        header.WriteBytes("KENB"u8);
        header.WriteUInt16(version);
        header.WriteUInt16(0);
        header.WriteUInt32((uint)identity.Length);
        header.WriteUInt64((ulong)payload.Length);
        header.WriteBytes(SHA256.HashData(payload));
        return [.. header.ToArray(), .. identity, .. payload];
    }

    internal static byte[] Reseal(byte[] file)
    {
        int start = 52 + (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8));
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(12), (ulong)(file.Length - start));
        SHA256.HashData(file.AsSpan(start), file.AsSpan(20, 32));
        return file;
    }
}
