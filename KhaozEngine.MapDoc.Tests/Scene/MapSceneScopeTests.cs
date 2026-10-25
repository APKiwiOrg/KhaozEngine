using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapSceneScopeTests
{
    internal static MapSceneScope Window(double minX = 0, double maxX = 64, double minY = -500, double maxY = 500) => new(
        new(WorldFrame.Origin, new((float)minX, -64), new((float)maxX, 64), null, null,
            Enum.GetValues<MapSurfaceRole>(), null, new()), new MapBox3(minX, minY, -64, maxX, maxY, 64), MapSceneChannels.All);

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void MotionPadding_RefusesInvalidBeforeAcquisition(double padding)
    {
        var source = NativeSceneFixtures.Open(NativeSceneFixtures.Create("spanning"), false);
        var result = source.Acquire(Window() with { MotionPaddingMetres = padding });
        Assert.Equal(MapSceneStatus.Invalid, result.Status);
        Assert.Null(result.Scene);
        Assert.Equal(0, result.Work.PayloadReads);
    }

    [Fact]
    public void MotionPadding_IsExplicitInScopeIdentityAndDiscovery()
    {
        var source = NativeSceneFixtures.Open(NativeSceneFixtures.Create("spanning"), false);
        var plain = source.Acquire(Window(10, 20));
        var padded = source.Acquire(Window(10, 20) with { MotionPaddingMetres = 8 });
        Assert.NotEqual(plain.Witness.ScopeDigest, padded.Witness.ScopeDigest);
        Assert.Empty(plain.Scene!.Placements);
        Assert.Contains(padded.Scene!.Placements, p => p.PlacementId == "spanning-leaf");
    }
    [Fact]
    public void PublishedScope_DetachesCallerOwnedFilters()
    {
        using var fixture = NativeSceneFixtures.Create("spanning");
        var roles = new List<MapSurfaceRole> { MapSurfaceRole.SupportFloor };
        var request = Window();
        request = request with { Surfaces = request.Surfaces with { Roles = roles } };
        var scene = NativeSceneFixtures.Open(fixture, false).Acquire(request).Scene!;
        string digest = scene.Scope.Digest;
        roles.Clear();
        Assert.Single(scene.Scope.Surfaces.Roles);
        Assert.Equal(digest, scene.Scope.Digest);
        Assert.Equal(digest, scene.Witness.ScopeDigest);
    }

    [Fact]
    public void ScopeDigest_ReleasesTheSurfaceHashBufferBeforeHashingTheScene()
    {
        var scope = Window();
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 8192));
        using var active = admission.Enter();
        Assert.Equal(64, scope.Digest.Length);
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(0, admission.RetainedBytes);
    }

}
