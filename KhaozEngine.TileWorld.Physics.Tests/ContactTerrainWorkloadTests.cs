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

    [Fact]
    public void RepairedContainerChecksumDoesNotAuthenticateAChangedWorkload()
    {
        using var scene = new ContactTerrainScene(4);
        var options = ContactTerrainOptInTests.Options with { CaptureTerrainDescentEvidence = true };
        using var capture = PhysicsNavBake.Capture(scene.Context, options, _ => 0);
        var profiles = new[] { new NavBakeProfile("player", ContactTerrainScene.Tuning, default) { MaxDropMetres = 6, AllowTerrainDescents = true } };
        var sources = new NavBakeSources().Add("world", new byte[32]);
        byte[] bytes = Bytes(GroundNavigationBake.Create(capture, sources, profiles));
        bytes[^1] ^= 1;
        int payload = GroundNavigationBake.HeaderLength + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
        SHA256.HashData(bytes.AsSpan(payload), bytes.AsSpan(20, 32));
        using var stream = new MemoryStream(bytes);
        Assert.Equal(NavBakeLoadStatus.Corrupt, GroundNavigationBake.Load(stream, new(options, sources, profiles)).Status);
    }

    static byte[] Bytes(GroundNavigationBake bake)
    {
        using var stream = new MemoryStream(); bake.WriteTo(stream); return stream.ToArray();
    }
}
