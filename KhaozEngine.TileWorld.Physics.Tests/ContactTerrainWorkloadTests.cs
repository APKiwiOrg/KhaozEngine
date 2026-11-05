using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using KhaozEngine.Movement;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class ContactTerrainWorkloadTests
{
    [Fact]
    public void EveryWorkerAndEnabledProfileProducesTheSameCanonicalFile()
    {
        using var scene = new ContactTerrainScene(4);
        var options = ContactTerrainOptInTests.Options with { CaptureTerrainDescentEvidence = true };
        using var capture = PhysicsNavBake.Capture(scene.Context, options, _ => 0);
        var profiles = new[] {
            new NavBakeProfile("player", ContactTerrainScene.Tuning, default) { MaxDropMetres = 6, AllowTerrainDescents = true },
            new NavBakeProfile("small", ContactTerrainScene.Tuning with { CapsuleRadius = 0.2f }, default) { MaxDropMetres = 6, AllowTerrainDescents = true } };
        var sources = new NavBakeSources().Add("world", new byte[32]);
        var serial = GroundNavigationBake.Create(capture, sources, profiles, new(1));
        var parallel = GroundNavigationBake.Create(capture, sources, profiles.Reverse().ToArray(), new(2));
        byte[] expected = Bytes(serial); byte[] actual = Bytes(parallel);
        Assert.Equal(expected, actual);
        using var stream = new MemoryStream(actual);
        var loaded = GroundNavigationBake.Load(stream, new(options, sources, profiles));
        Assert.Equal(NavBakeLoadStatus.Loaded, loaded.Status);
        Assert.Equal(actual, Bytes(loaded.Bake!));
        Assert.Same(loaded.Bake!.GetProfile("player").Terrain!.Evidence, loaded.Bake.GetProfile("small").Terrain!.Evidence);
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("acceptance")]
    [InlineData("candidates")]
    [InlineData("routes")]
    [InlineData("slope")]
    [InlineData("footprint")]
    public void RepairedContainerChecksumDoesNotAuthenticateAChangedWorkload(string mutation)
    {
        using var scene = new ContactTerrainScene(4);
        var options = ContactTerrainOptInTests.Options with { CaptureTerrainDescentEvidence = true };
        using var capture = PhysicsNavBake.Capture(scene.Context, options, _ => 0);
        var profiles = new[] { new NavBakeProfile("player", ContactTerrainScene.Tuning, default) { MaxDropMetres = 6, AllowTerrainDescents = true } };
        var sources = new NavBakeSources().Add("world", new byte[32]);
        byte[] bytes = Bytes(GroundNavigationBake.Create(capture, sources, profiles));
        int payload = GroundNavigationBake.HeaderLength + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
        var offsets = WorkloadOffsets(bytes, payload);
        int changed = mutation switch
        {
            "acceptance" => offsets.DataEnd - 1,
            "candidates" => offsets.Facts + 16,
            "routes" => offsets.FactsEnd - 1,
            "slope" => offsets.Facts,
            "footprint" => offsets.Facts + 4,
            _ => bytes.Length - 1
        };
        bytes[changed] ^= 1;
        SHA256.HashData(bytes.AsSpan(payload), bytes.AsSpan(20, 32));
        using var stream = new MemoryStream(bytes);
        Assert.Equal(NavBakeLoadStatus.Corrupt, GroundNavigationBake.Load(stream, new(options, sources, profiles)).Status);
    }

    static (int DataEnd, int Facts, int FactsEnd) WorkloadOffsets(byte[] bytes, int payload)
    {
        var reader = new NavBakeReader(bytes.AsSpan(payload));
        Assert.True(reader.TryReadInt32(out int width)); Assert.True(reader.TryReadInt32(out int height));
        Assert.True(reader.TryReadBytes(12, out _)); Assert.True(reader.TryReadInt32(out int surfaces));
        Assert.True(reader.TryReadBytes(width * height + surfaces * 12, out _));
        Assert.True(reader.TryReadInt32(out int water)); Assert.True(reader.TryReadBytes(water * 12, out _));
        Assert.True(reader.TryReadInt32(out int evidence)); Assert.True(reader.TryReadBytes(evidence, out _));
        Assert.True(reader.TryReadInt32(out int data)); Assert.True(reader.TryReadBytes(data, out _));
        int dataEnd = payload + reader.Position;
        Assert.True(reader.TryReadInt32(out int facts)); int start = payload + reader.Position;
        return (dataEnd, start, start + facts);
    }

    static byte[] Bytes(GroundNavigationBake bake)
    {
        using var stream = new MemoryStream(); bake.WriteTo(stream); return stream.ToArray();
    }
}
