using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Storage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Identity;

public sealed class SchemeTwoIdentityTests
{
    static readonly MapResolveOptions V2 = new("headless", 1, "options", ResolverVersion: 2);
    static string Token(MapDocument doc) => MapAuthoredIdentityV2.Compute(doc, SurfaceStorageFixtures.Assets(), V2);

    [Fact]
    public void SchemeTwo_MonolithicAndTiledCopiesShareOneToken()
    {
        MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
        string expected = Token(doc);
        string dir = SurfaceStorageFixtures.SaveToTemp(doc);
        try
        {
            Assert.Equal(expected, MapAuthoredIdentityV2.Compute(MapStoredSurfaceSource.Open(dir), SurfaceStorageFixtures.Assets(), V2));
            foreach (MapResolveOptions options in new[]
            {
                V2 with { BuilderId = "other-headless" },
                V2 with { BuilderVersion = 2 },
                V2 with { OptionsHash = "other-options" },
            })
                Assert.NotEqual(expected, MapAuthoredIdentityV2.Compute(doc, SurfaceStorageFixtures.Assets(), options));
            Assert.Throws<MapDocumentException>(() => MapAuthoredIdentityV2.Compute(doc, SurfaceStorageFixtures.Assets(), V2 with { ResolverVersion = 1 }));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("height", true)]
    [InlineData("unit", true)]
    [InlineData("record", true)]
    [InlineData("role", true)]
    [InlineData("displayName", false)]
    public void SchemeTwo_ChangesWithGeometryAndPolicyOnly(string change, bool changes)
    {
        string before = Token(SurfaceStorageFixtures.ThreeSurfaces());
        MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
        SurfaceStorageFixtures.Apply(doc, change);
        Assert.Equal(changes, before != Token(doc));
    }

    [Fact]
    public void SchemeTwo_StaleSemanticDigestIsCorrupt()
    {
        string dir = SurfaceStorageFixtures.SaveToTemp(SurfaceStorageFixtures.ThreeSurfaces());
        try
        {
            SurfaceStorageFixtures.RewriteIndexSemanticDigest(dir, new("ridge", 0, 0), new string('0', 64));
            Assert.Contains("Corrupt", Assert.Throws<MapDocumentException>(() => MapAuthoredIdentityV2.Compute(MapStoredSurfaceSource.Open(dir), SurfaceStorageFixtures.Assets(), V2)).Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SchemeTwo_RefusesAPartialView()
    {
        string dir = SurfaceStorageFixtures.SaveToTemp(SurfaceStorageFixtures.ThreeSurfaces());
        try
        {
            MapDocument window = SurfaceStorageFixtures.LoadSlotWindow(dir, 300);
            Assert.Contains("window", Assert.Throws<MapDocumentException>(() => Token(window)).Message);
            var far = new MapPatchKey("far", 300, 0);
            MapSurfaceScope scope = ScopeFixtures.Around(new WorldFrame(150, 0), 2, 2);
            MapDocumentSurfaceSource beforeSource = MapDocumentSurfaceSource.Capture(window);
            MapScopedSurfaces before = ScopeFixtures.Acquire(beforeSource, scope);
            Assert.Equal(MapAcquireStatus.Complete, before.Status);
            Assert.Contains(new KeyValuePair<MapPatchKey, string>(far, MapSurfaceSemantics.PatchDigest(window.Surfaces.Patches[far])), before.Identity.Patches);

            // The other far page is unread. This root remains pinned, while acquired patch facts change.
            window.Surfaces.Patches[far].Heights[5]++;
            MapDocumentSurfaceSource afterSource = MapDocumentSurfaceSource.Capture(window);
            MapScopedSurfaces after = ScopeFixtures.Acquire(afterSource, scope);
            Assert.Equal(MapAcquireStatus.Complete, after.Status);
            Assert.Equal(beforeSource.RootSha256, afterSource.RootSha256);
            Assert.Equal((beforeSource.RootSha256, afterSource.RootSha256), (before.Identity.RootSha256, after.Identity.RootSha256));
            Assert.NotEqual(beforeSource.SnapshotId, afterSource.SnapshotId);
            Assert.NotEqual(before.Identity.Digest, after.Identity.Digest);
            Assert.Contains(new KeyValuePair<MapPatchKey, string>(far, MapSurfaceSemantics.PatchDigest(window.Surfaces.Patches[far])), after.Identity.Patches);
            Assert.Contains("window", Assert.Throws<MapDocumentException>(() => Token(window)).Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ScopedIdentity_IsFactoryOnlyAndTakesItsFrameAndScopeFromTheAcquisition()
    {
        var src = MapDocumentSurfaceSource.Capture(SurfaceStorageFixtures.ThreeSurfaces());
        MapSurfaceScope scope = ScopeFixtures.Around(WorldFrame.Origin, 62, 2);
        MapScopedSurfaces a = ScopeFixtures.Acquire(src, scope), b = ScopeFixtures.Acquire(src, scope);
        MapScopedSurfaces floorsOnly = ScopeFixtures.Acquire(src, scope with { Roles = new[] { MapSurfaceRole.SupportFloor } });
        Assert.Equal((MapAcquireStatus.Complete, MapAcquireStatus.Complete, MapAcquireStatus.Complete), (a.Status, b.Status, floorsOnly.Status));
        Assert.Equal((a.Identity.Digest, WorldFrame.Origin), (b.Identity.Digest, a.Identity.Frame));
        Assert.Equal((src.RootSha256, scope.Digest, WorldFrame.Origin), (a.Identity.RootSha256, a.Identity.ScopeDigest, a.Witness.Frame));
        Assert.Equal((src.SnapshotId, scope.Digest, true), (a.Witness.SnapshotId, a.Witness.ScopeDigest, a.Witness.Complete));
        Assert.Equal(a.Witness.Present, a.Identity.Patches);
        Assert.Equal(a.Witness.Records, a.Identity.Records);
        Assert.Equal(a.Witness.KnownEmpty, a.Identity.KnownEmpty);
        Assert.NotEmpty(a.Identity.Patches);
        Assert.All(a.Identity.Patches, p => Assert.Equal(MapSurfaceSemantics.PatchDigest(a.Patch(p.Key).Patch!), p.Value));
        Assert.NotEqual(a.Identity.ScopeDigest, floorsOnly.Identity.ScopeDigest);
        Assert.Equal((a.Identity.Digest, src.SnapshotId, true), (a.ReadWitness.ScopedDigest, a.ReadWitness.SnapshotId, a.ReadWitness.Complete));
        Assert.Equal((WorldFrame.Origin, 1, 1, 1, true), (a.ReadWitness.Frame, a.ReadWitness.QueryPolicyVersion, a.Identity.QueryPolicyVersion, a.Identity.BuildPolicyVersion, a.Identity.Complete));
        Assert.NotEqual(Token(SurfaceStorageFixtures.ThreeSurfaces()), a.Identity.Digest);
        a.Identity.RequireComplete();
        Assert.Empty(typeof(MapResolvedDocument).GetConstructors());
        foreach (Type t in new[] { typeof(MapScopedIdentity), typeof(MapReadWitness), typeof(MapCoverageWitness), typeof(MapScopedSurfaces) })
        {
            Assert.True(t.IsClass && t.IsSealed);
            Assert.Empty(t.GetConstructors());
            Assert.All(t.GetProperties(), p => Assert.Null(p.SetMethod));
        }

        Assert.IsType<ReadOnlyCollection<string>>(a.Witness.SurfaceIds);
        Assert.IsType<ReadOnlyCollection<KeyValuePair<MapPatchKey, string>>>(a.Witness.Present);
        Assert.IsType<ReadOnlyCollection<MapCoveredRange>>(a.Witness.KnownEmpty);
        Assert.IsType<ReadOnlyCollection<MapRecordRef>>(a.Witness.Records);
        Assert.IsType<ReadOnlyCollection<MapUnavailable>>(a.Witness.Unavailable);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)a.Witness.SurfaceIds).Add("forged"));
        Assert.Throws<NotSupportedException>(() => ((IList<KeyValuePair<MapPatchKey, string>>)a.Identity.Patches).Add(default));
        Assert.Throws<NotSupportedException>(() => ((IList<MapRecordRef>)a.Identity.Records).Add(new("forged", new("ground", 0, 0))));
        Assert.Throws<NotSupportedException>(() => ((IList<MapCoveredRange>)a.Identity.KnownEmpty).Add(new("ground", new(0, 0, 1, 1))));
        Assert.Throws<NotSupportedException>(() => ((IList<MapUnavailable>)a.Witness.Unavailable).Add(new("forged", new("ground", 0, 0), MapPatchStatus.Missing)));

        string[] assetSha256 = { new string('a', 64), new string('b', 64) };
        MapSurfaceRole[] roles = ScopeFixtures.AllRoles.ToArray();
        MapSurfaceScope mutableScope = scope with { Roles = roles };
        MapScopedSurfaces withAssets = MapScopedSurfaces.Acquire(src, mutableScope, assetSha256);
        Assert.Equal(MapAcquireStatus.Complete, withAssets.Status);
        string digest = withAssets.Identity.Digest;
        assetSha256[0] = new string('c', 64);
        roles[0] = MapSurfaceRole.Ceiling;
        Assert.Equal(new[] { new string('a', 64), new string('b', 64) }, withAssets.Identity.AssetSha256);
        Assert.Equal(scope.Digest, withAssets.Scope.Digest);
        Assert.Equal(digest, withAssets.Identity.Digest);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)withAssets.Identity.AssetSha256).Add(new string('d', 64)));
    }
}
