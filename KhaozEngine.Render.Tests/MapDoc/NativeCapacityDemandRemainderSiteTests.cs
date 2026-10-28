using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using Xunit;
using static KhaozEngine.Tests.CapacityDemandProbe;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>Accepted and refused rows for the native foliage generation and render cache remainder gates.</summary>
[Collection("AllocSensitive")]
public sealed class NativeCapacityDemandRemainderSiteTests
{
    static (decimal Current, decimal Increment, decimal Allowance, bool Refused) Of(object row) =>
        (Read<decimal>(row, "CurrentDemand"), Read<decimal>(row, "ProposedIncrement"), Read<decimal>(row, "Allowance"), Read<bool>(row, "Refused"));

    sealed record Window(ResidencyFixture Fixture, MapScopedScene Scene, MapWindowFoliageContext Context, MapRenderAssets Assets,
        NativeFoliagePolicy Policy, MapPlacementRouting Routing) : IDisposable
    {
        public void Dispose() { Assets?.Dispose(); Fixture.Dispose(); }
    }

    static Window Open(MapSceneLimits limits)
    {
        var fixture = ResidencyFixture.Foliage(4);
        MapSceneSource source;
        using (var document = MapDocumentSource.FromDocument(fixture.Document))
            source = MapSceneSource.Capture(document, fixture.Closure, limits).Source!;
        var proofs = MapRenderExtentValidator.Validate(fixture.Closure, source.RequiredAssetIds, new(new())).Proofs!;
        source = source.WithRenderExtents(proofs).Source!;
        var scope = ResidencyFixture.Window(0, 64) with { Channels = MapSceneChannels.Foliage };
        var scene = source.Acquire(scope).Scene!;
        var context = MapFoliageGenerationContext.CaptureWindow(source, scope).Context!;
        var ids = fixture.Closure.Assets.Select(a => a.Id).ToArray();
        var selected = MapRenderExtentValidator.Validate(fixture.Closure, ids, new(new()));
        Assert.Equal(MapRenderAssetStatus.Complete, selected.Status);
        var loaded = MapRenderAssets.LoadSelected(fixture.Closure, ids, selected.Proofs!, new(new()));
        Assert.Equal(MapRenderAssetStatus.Complete, loaded.Status);
        var assets = loaded.Assets!;
        var routing = MapPlacementRouting.Create(scene, new([], 100), new HashSet<string>()).Routing!;
        return new(fixture, scene, context, assets, fixture.FoliageState().Policy.Foliage, routing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenerationAdmission_ChunkCopyPublishesTheDeclaredAndPreparationCeilings(bool refused)
    {
        const long declared = 50_000_000, held = 745;
        long controls = MapFoliageInventory.ConstructionControls;
        // The scene has the first construction controls left, or one byte fewer, when the chunk begins.
        long prepared = held + controls - (refused ? 1 : 0);
        using var window = Open(new());
        var profile = new MapFoliageLimits(MaxQueryRetainedBytes: declared);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: prepared));
        Assert.True(admission.TryReservePreparation(held, 0));
        using var probe = new CapacityDemandProbe();
        // The native chunk copy and its provenance, exactly as preparation derives them for the window's first chunk.
        var limits = profile with { MaxQueryRetainedBytes = Math.Min(declared, admission.RemainingPreparedBytes) };
        typeof(NativeMapFoliage).GetMethod("DeriveChunk", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[] { limits, profile, profile, admission, 0L });
        var layer = MapFoliageDistribution.Layer(window.Context, "cover");
        Action begin = () => MapFoliageGenerationAdmission.Begin(window.Context, layer, default, limits, 0, declared, admission);
        if (refused) Assert.Throws<MapSceneCapacityException>(begin); else begin();
        var rows = probe.Events.Where(r => Read<string>(r, "Site") == "MapFoliageGenerationAdmission.MaxQueryRetainedBytes.2").ToArray();
        Assert.Equal(new[] { "MaxQueryRetainedBytes", "MaxPreparedBytes" }, rows.Select(r => Read<string>(r, "LimitName")));
        Assert.Equal((0m, (decimal)controls, (decimal)declared, false), Of(rows[0]));
        Assert.Equal(((decimal)held, (decimal)controls, (decimal)prepared, refused), Of(rows[1]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenderCachePublication_StorageResizeAcceptsAndRefusesAgainstTheAggregateAllowance(bool refused)
    {
        using var window = Open(new());
        var cache = MapFoliageRenderCache.For(window.Context);
        cache.Get("cover", default, new MapFoliageLimits(), null, null, long.MaxValue);
        long retained = cache.RetainedBytes;
        // Publication trims one owner's storage arrays to the minimum capacity of three, 48 + 36 * 3 bytes.
        const long storage = 48 + 36 * 3;
        long allowance = retained + storage - (refused ? 1 : 0);
        using var probe = new CapacityDemandProbe();
        Action publish = () => cache.Publish(null, null, allowance);
        if (refused) Assert.Throws<MapSceneCapacityException>(publish); else publish();
        var rows = probe.Events.Where(r => Read<string>(r, "Site") == "MapFoliageRenderCache.MaxQueryRetainedBytes.2").ToArray();
        Assert.NotEmpty(rows);
        Assert.Equal(((decimal)retained, (decimal)storage, (decimal)allowance, refused), Of(rows[0]));
        if (refused) Assert.Single(rows);
    }
}
