using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEdit.Tools;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc.Water;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

public sealed class WaterServiceTests : IDisposable
{
    readonly List<MapEditSession> _sessions = new();

    [Fact]
    public void WaterApply_MatchesTheGuiCommand()
    {
        var edit = new MapUpsertWaterBody(WaterFixtures.OceanOverCaves().Water!.Bodies.Single(b => b.Id == "cave-lake") with { SurfaceY = -33f });
        MapEditSession session = Open(WaterFixtures.OceanOverCaves());
        MapWaterMutationResult service = new MutationService(session).WaterApply(edit);
        MapDocument gui = WaterFixtures.OceanOverCaves();
        MapNativeEditEffects guiEffects = NativeDocumentTransaction.Run(gui, new WaterEditCommand(edit), false, null, localOnly: true);
        Assert.Equal(guiEffects.Describe(), service.Effects.Describe());
        Assert.Equal(gui.Water!.Digest(), WaterToolFixtures.Document(session).Water!.Digest());
        Assert.Equal("water_apply", service.Mutation.Verb);
        Assert.True(service.Mutation.WorldChanged);
        Assert.True(session.IsDirty);

        MapEditSession refused = Open(WaterFixtures.OceanOverCaves());
        MapWaterBodyDoc ocean = WaterToolFixtures.Document(refused).Water!.Bodies.Single(b => b.Id == "ocean");
        string before = WaterToolFixtures.Document(refused).Water!.Digest();
        string contentBefore = MapDocumentFile.SaveText(WaterToolFixtures.Document(refused));
        var historyBefore = refused.NativeHistoryState;
        var stray = ocean with
        {
            Domain = ocean.Domain with { Rects = new[] { new MapWaterRect(30, 0, 34, 4) } },
        };
        var error = Assert.Throws<MapDocumentException>(() => new MutationService(refused).WaterApply(new MapUpsertWaterBody(stray)));
        Assert.Equal(MapDocumentRefusal.WaterNoSpaceFootprint, error.Reason);
        Assert.Equal("ocean", error.EntityId);
        Assert.Equal(ocean.Covers.Select(c => c.Id), error.SourceIds);
        Assert.Contains(error.Findings!, f => f.Reason == MapDocumentRefusal.WaterNoSpaceFootprint && f.EntityId == "ocean");
        Assert.Equal(before, WaterToolFixtures.Document(refused).Water!.Digest());
        Assert.Equal(contentBefore, MapDocumentFile.SaveText(WaterToolFixtures.Document(refused)));
        Assert.Equal(historyBefore, refused.NativeHistoryState);
        Assert.False(refused.IsDirty);
        var repaired = stray with { Domain = ocean.Domain };
        new MutationService(refused).WaterApply(new MapUpsertWaterBody(repaired));
        Assert.Equal(before, WaterToolFixtures.Document(refused).Water!.Digest());
    }

    [Fact]
    public void Sample_ReportsTheSameFactsAsTheQuery()
    {
        MapEditSession session = Open(WaterFixtures.OceanOverCaves());
        var water = new WaterService(session);
        MapWaterSampleInfo s = water.Sample(5f, -35f, -2f);
        Assert.Equal((MapWaterStatus.Wet, "cave-lake", -34f, true), (s.Status, s.BodyId, s.UpperY, s.UpperIsFreeSurface));
        Assert.Equal(("lake-cave", -40f, -34f, null), (s.SpaceId, s.LowerY, s.NominalSurfaceY, s.Detail));
        MapWaterSampleInfo closed = water.Sample(7f, -30f, -2f);
        Assert.Equal((MapWaterStatus.Wet, "flooded", -40f, -25f, -20f, false),
            (closed.Status, closed.BodyId, closed.LowerY, closed.UpperY, closed.NominalSurfaceY, closed.UpperIsFreeSurface));
        Assert.Equal(MapWaterStatus.Dry, water.Sample(5f, -55f, -2f).Status);
        Assert.False(session.IsDirty);

        var absent = new WaterService(Open(WaterToolFixtures.LegacyAnalytic()));
        Assert.Empty(absent.List());
        Assert.Equal(MapWaterStatus.Dry, absent.Sample(5f, -35f, -2f).Status);
        MapDocument empty = WaterFixtures.OceanOverCaves();
        empty.Water = new MapWaterSet();
        var bounded = new WaterService(Open(empty));
        Assert.Empty(bounded.List());
        Assert.Equal(MapWaterStatus.Dry, bounded.Sample(5f, -35f, -2f).Status);
    }

    [Fact]
    public void ListAndGet_ReturnTheStoredBodies()
    {
        MapEditSession session = Open(WaterFixtures.OceanOverCaves());
        var water = new WaterService(session);
        Assert.Equal(new[] { "cave-lake", "flooded", "ocean" }, water.List().Select(b => b.Id));
        Assert.Equal(-20f, water.Get("flooded").SurfaceY);
        string before = MapDocumentFile.SaveText(WaterToolFixtures.Document(session));
        foreach (string id in new[] { "Flooded", "missing" })
        {
            var error = Assert.Throws<MapDocumentException>(() => water.Get(id));
            Assert.Equal(MapDocumentRefusal.ReferenceMissing, error.Reason);
            Assert.Equal(id, error.BodyId);
            Assert.Equal(WaterToolFixtures.Document(session).Id, error.DocumentId);
            Assert.Equal(before, MapDocumentFile.SaveText(WaterToolFixtures.Document(session)));
            Assert.False(session.IsDirty);
        }
        Assert.Equal("flooded", water.Get("flooded").Id);

        using ServiceProvider services = new ServiceCollection().AddMapEditServices().BuildServiceProvider();
        Assert.Same(services.GetRequiredService<WaterService>(), services.GetRequiredService<WaterService>());
    }

    [Fact]
    public void LegacyDocument_RefusesWaterEdits()
    {
        MapDocument analyticDocument = WaterFixtures.Document(new MapSurfaceSet(), null);
        analyticDocument.ResolverIdentity = null;
        analyticDocument.SupportRecipe = MapSupportRecipe.LegacyXzCallbackV1;
        MapEditSession analytic = Open(analyticDocument);
        Refuses(analytic);

        MapDocument legacy = WaterFixtures.Document(new MapSurfaceSet(), null);
        legacy.ResolverIdentity = new(1, 1);
        legacy.SupportRecipe = MapSupportRecipe.LegacyXzCallbackV1;
        Refuses(Open(legacy));

        void Refuses(MapEditSession session)
        {
            var mutation = new MutationService(session);
            var error = Assert.Throws<MapDocumentException>(() => mutation.WaterApply(new MapSetBoundedWater(true)));
            Assert.Equal(MapDocumentRefusal.ResolverContractMismatch, error.Reason);
            Assert.Equal(2, error.ExpectedResolverVersion);
            Assert.Equal(MapSupportRecipe.AuthoredBindingsV2, error.ExpectedSupportRecipe);
            session.WithDocument((doc, _) =>
            {
                Assert.Equal(doc.ResolverIdentity?.PayloadVersion, error.PayloadVersion);
                Assert.Equal(doc.ResolverIdentity?.ResolverVersion, error.ResolverVersion);
                Assert.Equal(doc.SupportRecipe, error.SupportRecipe);
                return 0;
            });
            Assert.False(session.IsDirty);

            MapDocument corrected = MapDocumentFile.LoadText(MapDocumentFile.SaveText(WaterToolFixtures.Document(session)));
            corrected.ResolverIdentity = new(1, 2);
            corrected.SupportRecipe = MapSupportRecipe.AuthoredBindingsV2;
            MapEditSession accepted = Open(corrected);
            MapWaterMutationResult applied = new MutationService(accepted).WaterApply(new MapSetBoundedWater(true));
            Assert.True(applied.Mutation.WorldChanged);
            Assert.NotNull(WaterToolFixtures.Document(accepted).Water);
            Assert.True(accepted.IsDirty);
        }
    }

    MapEditSession Open(MapDocument document)
    {
        MapEditSession session = WaterToolFixtures.Session(document);
        _sessions.Add(session);
        return session;
    }

    public void Dispose()
    {
        foreach (MapEditSession session in _sessions) WaterToolFixtures.Cleanup(session);
    }
}
