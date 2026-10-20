using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

/// <summary>Pure foliage edits: the legacy metre brush and its exact boundaries over sparse rasters, strict detached
/// refusal, direct chunk effects and the before and proposal bound policy differential.</summary>
public sealed class MapFoliageEditTests
{
    const string Id = FoliageFixtures.LayerId;

    [Fact]
    public void MetreBrush_QuantizesPositiveZAndDoesNotAllocateDomain()
    {
        // A zero raster with a negative origin, 33 by 17 samples at half a metre. The brush centre (1, -1) is sample (8, 8).
        MapFoliageLayer zero = Zero(-3, -5, 0.5f, 33, 17);
        MapFoliageLayer painted = MapFoliageBrush.Apply(zero, Paint(1, -1, 2, 128, 0.5f));
        byte fullHardnessCentre = painted.DensityAt(8, 8);
        byte halfLinearRim = painted.DensityAt(11, 8);
        byte softExactRadius = painted.DensityAt(8, 12);
        Assert.Equal((byte)128, fullHardnessCentre);
        Assert.Equal((byte)64, halfLinearRim);
        Assert.Equal((byte)0, softExactRadius);
        // Hardness times radius is still full strength, and the rim is symmetric across rows in positive Z.
        Assert.Equal((byte)128, painted.DensityAt(8, 10));
        Assert.Equal((byte)64, painted.DensityAt(8, 5));
        Assert.Equal((byte)64, painted.DensityAt(5, 8));
        Assert.Same(zero.Policy, painted.Policy);

        // Hardness 0 is full strength only at the centre, then linear to the radius.
        MapFoliageLayer soft = MapFoliageBrush.Apply(zero, Paint(1, -1, 2, 128, 0));
        Assert.Equal(new byte[] { 128, 96, 64, 32, 0 }, Enumerable.Range(8, 5).Select(x => soft.DensityAt(x, 8)).ToArray());
        // Half strength lands on .5 for odd targets. Ties go to even, never away from zero or toward zero.
        Assert.Equal((byte)2, MapFoliageBrush.Apply(zero, Paint(1, -1, 2, 5, 0)).DensityAt(10, 8));
        Assert.Equal((byte)4, MapFoliageBrush.Apply(zero, Paint(1, -1, 2, 7, 0)).DensityAt(10, 8));
        Assert.Equal((byte)0, MapFoliageBrush.Apply(zero, Paint(1, -1, 2, 1, 0)).DensityAt(10, 8));
        // 255 is a legal target and clamps nothing away. A lower target erases toward it.
        MapFoliageLayer full = MapFoliageBrush.Apply(zero, Paint(1, -1, 2, 255, 1));
        Assert.Equal((byte)255, full.DensityAt(8, 12));
        Assert.Equal((byte)128, MapFoliageBrush.Apply(full, Paint(1, -1, 2, 1, 0)).DensityAt(10, 8));

        // The brush quantizes to lattice samples, not cell centres. This disc reaches sample (2, 3) alone, and row 3
        // advances positive world Z from the origin, so the mirrored row 13 stays zero.
        MapFoliageLayer one = MapFoliageBrush.Apply(zero, Paint(-1.9f, -3.45f, 0.2f, 77, 1));
        Assert.Equal((byte)77, one.DensityAt(2, 3));
        Assert.Equal((byte)0, one.DensityAt(2, 13));
        Assert.Equal(1, Samples(one).Count(v => v != 0));

        // Every reached sample equals the released full-domain loop, on negative, non-square and nonzero rasters.
        MapFoliageLayer basic = FoliageFixtures.BasicLayer();
        foreach (MapFoliageLayer layer in new[] { zero, basic, Zero(-7.25f, 3.5f, 0.75f, 21, 40) })
            foreach (MapPaintFoliage brush in new[]
            {
                Paint(0.3f, -3.7f, 5, 200, 0.3f), Paint(60, -1, 7.5f, 0, 0), Paint(-3, -5, 2, 255, 1),
                Paint(20.1f, -4.9f, 3.3f, 17, 0.75f), Paint(1.1f, 10.2f, 4.4f, 99, 0.6f), Paint(500, 500, 2, 10, 1),
            })
                Assert.Equal(Legacy(layer, brush), Samples(MapFoliageBrush.Apply(layer, brush)));
        Assert.Same(basic, MapFoliageBrush.Apply(basic, Paint(500, 500, 2, 10, 1)));

        // A full 1,048,576 sample square writes the one chunk the disc reaches and allocates nothing in proportion to it.
        MapFoliageLayer huge = Zero(-3, -5, 0.5f, 1_048_576, 1_048_576);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        MapFoliageLayer big = MapFoliageBrush.Apply(huge, Paint(1, -1, 2, 128, 0.5f));
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.True(allocated < 1 << 20, $"the brush allocated {allocated} bytes");
        Assert.Equal(new MapFoliageChunkKey(0, 0), Assert.Single(big.Chunks).Key);
        Assert.Equal((byte)64, big.DensityAt(11, 8));
        Assert.Equal((byte)0, big.DensityAt(1_048_575, 1_048_575));

        // The pure edit over that domain expands at most once for finalization and serializes no payload, and its effect
        // names the one new batch with its cosmetic bounds: the chunk rectangle clipped to the sample domain.
        FoliageFixture f = FoliageComparisonFixtures.Set(FoliageFixtures.Create(), huge);
        MapPrefabEditResult result = Pure(f, Paint(1, -1, 2, 128, 0.5f));
        MapFoliageChunkChange change = Assert.Single(result.Effects.FoliageChunks);
        Assert.Equal(new MapFoliageChunkChange(Id, new(0, 0), null, new MapFoliageRect(-3, -5, 13, 11), null, change.AfterDigest), change);
        Assert.Equal(64, change.AfterDigest!.Length);
        Assert.Equal(MapNativeInvalidation.Foliage, result.Effects.Invalidates);
        Assert.Null(f.Document.Foliage!.Layers[0].Chunks.SingleOrDefault());
    }

    [Fact]
    public void HardnessOne_IncludesExactRadiusAndExcludesJustOutside()
    {
        MapFoliageLayer zero = Zero(-3, -5, 0.5f, 33, 17);
        // Sample (12, 8) sits exactly 2 m east of the centre.
        byte hardnessOneExactRadius = MapFoliageBrush.Apply(zero, Paint(1, -1, 2, 128, 1)).DensityAt(12, 8);
        Assert.Equal((byte)128, hardnessOneExactRadius);
        // Moving the centre 0.0001 m west leaves that sample 2.0001 m away, and brings sample (4, 8) to 1.9999 m.
        MapFoliageLayer outside = MapFoliageBrush.Apply(zero, Paint(0.9999f, -1, 2, 128, 1));
        byte hardnessOneJustOutside = outside.DensityAt(12, 8);
        Assert.Equal((byte)0, hardnessOneJustOutside);
        Assert.Equal((byte)128, outside.DensityAt(4, 8));
        // The same exact radius in Z, and below hardness 1 the exact radius has zero weight.
        Assert.Equal((byte)128, MapFoliageBrush.Apply(zero, Paint(1, -1, 2, 128, 1)).DensityAt(8, 4));
        Assert.Equal((byte)0, MapFoliageBrush.Apply(zero, Paint(1, -1, 2, 128, 0.999f)).DensityAt(8, 4));
        Assert.Equal((byte)0, MapFoliageBrush.Apply(zero, Paint(1, -1, 1.9999f, 128, 1)).DensityAt(12, 8));
        // A hard disc wholly between samples reaches nothing and returns the layer unchanged.
        Assert.Same(zero, MapFoliageBrush.Apply(zero, Paint(1.25f, -0.75f, 0.2f, 128, 1)));
    }

    [Fact]
    public void PureFoliageEdits_RefuseAtomicallyAndBindDifferentialIdentity()
    {
        FoliageFixture f = FoliageFixtures.Create();
        MapFoliageLayer layer = f.Document.Foliage!.Layers[0];
        string original = MapDocumentFile.SaveText(f.Document);

        // A byte edit reports one chunk, Foliage alone and the foliage write set, without touching the input document.
        MapPrefabEditResult density = Pure(f, new MapSetFoliageDensity(Id, 1, 2, 2, 1, [7, 9]));
        MapNativeEditEffects densityOnly = density.Effects;
        Assert.Equal(MapNativeInvalidation.Foliage, densityOnly.Invalidates);
        MapFoliageChunkChange chunk = Assert.Single(densityOnly.FoliageChunks);
        Assert.Equal((Id, new MapFoliageChunkKey(0, 0)), (chunk.LayerId, chunk.Chunk));
        Assert.Equal(new MapFoliageRect(-3, -5, 61, -1), chunk.OldBounds);
        Assert.Equal(chunk.OldBounds, chunk.NewBounds);
        Assert.NotEqual(chunk.BeforeDigest, chunk.AfterDigest);
        Assert.Equal((64, 64), (chunk.BeforeDigest!.Length, chunk.AfterDigest!.Length));
        Assert.Contains("\"foliageChunks\":[{\"layerId\":\"cover-a\",\"x\":0,\"z\":0,", densityOnly.Describe());
        Assert.True(density.WriteSet.Foliage);
        Assert.False(density.WriteSet.Placements || density.WriteSet.Prefabs || density.WriteSet.BuildingIntent);
        MapFoliageLayer edited = density.After.Foliage!.Layers[0];
        Assert.Equal(new byte[] { 70, 7, 9, 73 }, new[] { edited.DensityAt(0, 2), edited.DensityAt(1, 2), edited.DensityAt(2, 2), edited.DensityAt(3, 2) });
        Assert.Same(layer.Chunks[1], edited.Chunks[1]);
        Assert.Equal(original, MapDocumentFile.SaveText(f.Document));

        // No-op edits report empty effects and write nothing, in the released description bytes.
        string empty = new MapNativeEditEffects(null, null, [], [], [], [], MapNativeInvalidation.None).Describe();
        Assert.DoesNotContain("foliageChunks", empty);
        foreach (MapFoliageEdit noOp in new MapFoliageEdit[]
        {
            new MapSetFoliageDensity(Id, 1, 2, 2, 1, [71, 72]), new MapSetFoliageLayer(layer), Paint(-2, -4, 0.5f, 200, 1),
            new MapFoliageEditSequence([], null), new MapFoliageEditSequence([new MapSetFoliageDensity(Id, 0, 0, 1, 1, [0])], null),
        })
        {
            MapPrefabEditResult result = Pure(f, noOp);
            Assert.Equal(empty, result.Effects.Describe());
            Assert.Empty(result.Effects.FoliageChunks);
            Assert.False(result.WriteSet.Foliage);
        }

        // Settings, new layers holding chunks and removal are whole-layer changes: one layer entry with preparation-free
        // digests and no batch rows (T9-P4a).
        MapNativeEditEffects seededEffects = Pure(f, new MapSetFoliageLayer(Change(layer, l => l["seed"] = 18))).Effects;
        Assert.Empty(seededEffects.FoliageChunks);
        MapFoliageLayerChange seeded = Assert.Single(seededEffects.FoliageLayers);
        Assert.Equal(Id, seeded.LayerId);
        Assert.NotEqual(seeded.BeforeDigest, seeded.AfterDigest);
        Assert.Equal((64, 64), (seeded.BeforeDigest!.Length, seeded.AfterDigest!.Length));
        Assert.Equal(MapNativeInvalidation.Foliage, seededEffects.Invalidates);
        Assert.Contains("\"foliageLayers\":[{\"layerId\":\"cover-a\",", seededEffects.Describe());
        var added = Pure(f, new MapSetFoliageLayer(Change(layer, l => l["id"] = "cover-b")));
        Assert.Equal(new[] { "cover-a", "cover-b" }, added.After.Foliage!.Layers.Select(l => l.Id));
        Assert.Empty(added.Effects.FoliageChunks);
        MapFoliageLayerChange addedEntry = Assert.Single(added.Effects.FoliageLayers);
        Assert.Equal(("cover-b", (string?)null), (addedEntry.LayerId, addedEntry.BeforeDigest));
        Assert.NotNull(addedEntry.AfterDigest);
        var removed = Pure(f, new MapRemoveFoliage(Id));
        Assert.Null(removed.After.Foliage);
        Assert.Equal(new MapFoliageLayerChange(Id, seeded.BeforeDigest, null), Assert.Single(removed.Effects.FoliageLayers));
        Assert.True(removed.WriteSet.Foliage);

        // Every invalid request refuses for its own reason before any write and leaves the document byte for byte as it was.
        const string range = "must be from 0 to 255", count = "needs 2 values", outside = "lies outside layer", absent = "No foliage layer 'absent'";
        const string radius = "radius must be finite and positive", hardness = "hardness must be from 0 to 1";
        // A layer that advertises a chunk it does not hold cannot become this document's state.
        MapFoliageLayer advertisedOnly = MapFoliageLayer.FromStorage(layer,
            layer.ChunkDigests.Select(d => d.Key.X == 1 ? d with { Sha256 = new string('0', 64) } : d).ToArray(), layer.Chunks.Take(1).ToArray());
        foreach ((MapFoliageEdit bad, string reason) in new (MapFoliageEdit, string)[]
        {
            (new MapSetFoliageDensity(Id, 1, 2, 2, 1, [7, 256]), range), (new MapSetFoliageDensity(Id, 1, 2, 2, 1, [-1, 9]), range),
            (new MapSetFoliageDensity(Id, 1, 2, 2, 1, [7]), count), (new MapSetFoliageDensity(Id, 1, 2, 2, 1, [7, 9, 11]), count),
            (new MapSetFoliageDensity(Id, 34, 0, 2, 1, [1, 1]), outside), (new MapSetFoliageDensity(Id, -1, 0, 1, 1, [1]), outside),
            (new MapSetFoliageDensity(Id, 0, 3, 1, 1, [1]), outside), (new MapSetFoliageDensity(Id, 0, 0, 0, 1, []), "positive width and depth"),
            (new MapSetFoliageDensity(Id, 0, 0, 1, 1, null!), "requires its values"), (new MapSetFoliageDensity("absent", 0, 0, 1, 1, [1]), absent),
            (Paint(1, -1, 0, 9, 1), radius), (Paint(1, -1, -2, 9, 1), radius), (Paint(1, -1, float.NaN, 9, 1), radius),
            (Paint(1, -1, float.PositiveInfinity, 9, 1), radius), (Paint(1, -1, 2, 9, -0.1f), hardness), (Paint(1, -1, 2, 9, 1.1f), hardness),
            (Paint(1, -1, 2, 9, float.NaN), hardness), (Paint(float.NaN, -1, 2, 9, 1), "finite world position"),
            (new MapPaintFoliage("absent", 1, -1, 2, 9, 1), absent), (new MapRemoveFoliage("absent"), absent),
            (new MapSetFoliageLayer(null!), "requires a layer"),
            (new MapSetFoliageLayer(Change(layer, l => l["policy"]!["version"] = 2)), "only policy legacy-tile version 1 is supported"),
            (new MapSetFoliageLayer(Change(layer, l => l["models"] = new JsonArray(new JsonObject { ["assetId"] = "missing", ["weight"] = 1 }))),
                "model asset 'missing' is not in the verified closure"),
            (new MapSetFoliageLayer(advertisedOnly), "advertises chunk MapFoliageChunkKey { X = 1, Z = 0 } without its density bytes"),
            (new MapFoliageEditSequence([new MapSetFoliageDensity(Id, 1, 2, 2, 1, [7, 9]), new MapSetFoliageDensity(Id, 1, 2, 2, 1, [7])], null), count),
            (new MapFoliageEditSequence([new MapFoliageEditSequence([], null)], null), "cannot contain another sequence"),
            (new MapFoliageEditSequence(null!, null), "requires its edit list"),
            (new MapFoliageEditSequence([new MapSetFoliageDensity(Id, 1, 2, 2, 1, [7, 9])], new MapMovePrefab("missing", new(Vector3.Zero, 0, 1))),
                "No live prefab instance 'missing'"),
        })
            Refused(f, bad, reason);
        // 255 is a legal stored density.
        Assert.Equal((byte)255, Pure(f, new MapSetFoliageDensity(Id, 4, 1, 1, 1, [255])).After.Foliage!.Layers[0].DensityAt(4, 1));

        // An edit reaches at most 64 chunks and visits at most 65,536 addresses, refused before any byte is read.
        FoliageFixture wide = FoliageComparisonFixtures.Set(FoliageFixtures.Create(), Zero(-3, -5, 1, 1_048_576, 1024));
        Refused(wide, new MapSetFoliageDensity(Id, 31, 0, 2018, 1, Enumerable.Repeat(1, 2018).ToArray()), "65 chunks");
        // Chunks 1 to 64 are read by batches 0 to 64, since a batch's bilinear reads reach one sample into the next chunk.
        Assert.Equal(65, Pure(wide, new MapSetFoliageDensity(Id, 32, 0, 2048, 1, Enumerable.Repeat(1, 2048).ToArray())).Effects.FoliageChunks.Count);
        Refused(wide, new MapSetFoliageDensity(Id, 0, 0, 65_537, 1, new int[65_537]), "65537 addresses");
        Assert.Equal(64, Pure(wide, new MapSetFoliageDensity(Id, 0, 0, 256, 256, Enumerable.Repeat(3, 65_536).ToArray())).Effects.FoliageChunks.Count);
        Refused(wide, new MapPaintFoliage(Id, 200, 200, 128, 9, 1), "66049 addresses");
        // A 241 sample square box from sample 31 to 271 stays under the address limit but spans 9 by 9 chunks.
        Refused(wide, new MapPaintFoliage(Id, 148, 146, 120, 9, 1), "81 chunks");

        // A supported policy definition change needs the digest issued for this before document and this proposal.
        var policy = new MapSetFoliageLayer(Change(layer, l => l["policy"]!["normalBoundaryHeight"] = 0.25f));
        Refused(f, policy, "differential");
        MapFoliageDifferentialResult preview = MapFoliageEdits.PreviewPolicyChange(f.Document, f.Assets, policy);
        Assert.Equal(MapFoliageStatus.Complete, preview.Status);
        Assert.Equal(64, preview.Digest.Length);
        Assert.Equal(FoliageFixtures.Context(f).SnapshotId, preview.BeforeIdentity);
        var otherProposal = policy with { Layer = Change(layer, l => l["policy"]!["normalBoundaryHeight"] = 0.5f) };
        string other = MapFoliageEdits.PreviewPolicyChange(f.Document, f.Assets, otherProposal).Digest;
        string otherBefore = MapFoliageEdits.PreviewPolicyChange(density.After, f.Assets, policy).Digest;
        Assert.Equal(3, new[] { preview.Digest, other, otherBefore }.Distinct().Count());
        Refused(f, policy with { ExpectedDifferentialDigest = other }, "stale");
        Refused(f, policy with { ExpectedDifferentialDigest = otherBefore }, "stale");
        Refused(f, new MapFoliageEditSequence([policy], null), "differential");
        MapPrefabEditResult accepted = MapFoliageEdits.Prepare(f.Document, f.Assets, policy with { ExpectedDifferentialDigest = preview.Digest });
        Assert.Equal(0.25f, accepted.After.Foliage!.Layers[0].Policy.NormalBoundaryHeight);
        Assert.Equal(preview.ProposalDigest, FoliageFixtures.Context(new(accepted.After, f.Assets)).SnapshotId);
        Assert.Equal(MapNativeInvalidation.Foliage, accepted.Effects.Invalidates);
        Assert.Equal(seeded.BeforeDigest, Assert.Single(accepted.Effects.FoliageLayers).BeforeDigest);
        Assert.Empty(accepted.Effects.FoliageChunks);
        // The token binds the candidate that lands. A single-step sequence lands exactly the previewed proposal, while a
        // sequence that also edits density lands another candidate, so its token is stale.
        var tokened = policy with { ExpectedDifferentialDigest = preview.Digest };
        var sequenced = MapFoliageEdits.Prepare(f.Document, f.Assets, new MapFoliageEditSequence([tokened], null));
        Assert.Equal(preview.ProposalDigest, FoliageFixtures.Context(new(sequenced.After, f.Assets)).SnapshotId);
        Refused(f, new MapFoliageEditSequence([tokened, new MapSetFoliageDensity(Id, 1, 2, 2, 1, [7, 9])], null), "stale");
        // A proposal whose lattice cannot be represented has no Complete differential and issues no digest.
        var unrepresentable = new MapSetFoliageLayer(Change(layer, l => { l["policy"]!["normalBoundaryHeight"] = 0.25f; l["originX"] = 1e12f; }));
        MapFoliageDifferentialResult incomplete = MapFoliageEdits.PreviewPolicyChange(f.Document, f.Assets, unrepresentable);
        Assert.NotEqual(MapFoliageStatus.Complete, incomplete.Status);
        Assert.Equal("", incomplete.Digest);
        Refused(f, unrepresentable with { ExpectedDifferentialDigest = preview.Digest }, "issues no digest");
        Assert.Equal(original, MapDocumentFile.SaveText(f.Document));

        // A sequence applies its owner edit and every foliage step on one detached candidate, validated together.
        PrefabFixture p = PrefabFixtures.Create();
        string prefabState = MapDocumentFile.SaveText(p.Document);
        MapFoliageLayer exterior = Change(Zero(1, 1, 1, 4, 4), l =>
        {
            l["support"] = new JsonObject { ["kind"] = "Surface", ["surfaceId"] = "exterior" };
            l["models"] = new JsonArray(new JsonObject { ["assetId"] = p.Assets.Assets[0].Id, ["weight"] = 1 });
            l["allowedVisibleMaterials"] = new JsonArray();
            l["materials"] = new JsonArray();
            l["compatibilityOrdinals"] = new JsonArray(new JsonObject { ["surfaceId"] = "exterior", ["ordinal"] = 0 });
            l["chunks"] = new JsonArray();
        });
        var together = MapFoliageEdits.Prepare(p.Document, p.Assets, new MapFoliageEditSequence(
            [new MapSetFoliageLayer(exterior), Paint(1, 1, 0.5f, 40, 1)], new MapMovePrefab("a", new(new Vector3(1, 0, 0), 0, 1))));
        Assert.Equal(1, together.After.Prefabs!.Instances.Single(i => i.Id == "a").Transform.Position.X);
        Assert.Equal((byte)40, together.After.Foliage!.Layers[0].DensityAt(0, 0));
        Assert.True(together.WriteSet.Prefabs && together.WriteSet.Foliage);
        Assert.True(together.Effects.Invalidates.HasFlag(MapNativeInvalidation.Placements) && together.Effects.Invalidates.HasFlag(MapNativeInvalidation.Foliage));
        Assert.Empty(together.Effects.FoliageChunks);
        Assert.Null(Assert.Single(together.Effects.FoliageLayers).BeforeDigest);
        Assert.Equal(prefabState, MapDocumentFile.SaveText(p.Document));

        // A policy token previewed for the layer alone is stale once an owner edit joins the candidate.
        var moved = new FoliageFixture(together.After, p.Assets);
        var ownerPolicy = new MapSetFoliageLayer(Change(moved.Document.Foliage!.Layers[0], l => l["policy"]!["normalBoundaryHeight"] = 0.25f));
        MapFoliageDifferentialResult ownerPreview = MapFoliageEdits.PreviewPolicyChange(moved.Document, p.Assets, ownerPolicy);
        Assert.Equal(MapFoliageStatus.Complete, ownerPreview.Status);
        ownerPolicy = ownerPolicy with { ExpectedDifferentialDigest = ownerPreview.Digest };
        Refused(moved, new MapFoliageEditSequence([ownerPolicy], new MapMovePrefab("a", new(new Vector3(2, 0, 0), 0, 1))), "stale");
        Assert.Equal(0.25f, MapFoliageEdits.Prepare(moved.Document, p.Assets, ownerPolicy).After.Foliage!.Layers[0].Policy.NormalBoundaryHeight);
    }

    [Fact]
    public void PureFoliageEdits_ExpandOnceOnlyForNewEffectiveReferences()
    {
        FoliageFixture f = FoliageFixtures.Create();
        MapFoliageLayer layer = f.Document.Foliage!.Layers[0];
        JsonObject Mask(string owner, JsonObject support) => new()
        {
            ["id"] = "mask-" + owner,
            ["owner"] = new JsonObject { ["scope"] = "World", ["kind"] = owner == FoliageFixtures.FloorId ? "Surface" : "Placement", ["id"] = owner },
            ["support"] = support,
            ["kind"] = "RoofAbove",
            ["footprint"] = new JsonObject { ["minX"] = -1, ["minZ"] = -1, ["maxX"] = 1, ["maxZ"] = 1 },
        };
        JsonObject Floor() => new() { ["kind"] = "Surface", ["surfaceId"] = FoliageFixtures.FloorId };
        JsonObject Space(string id) => new() { ["kind"] = "Space", ["spaceId"] = id, ["referenceY"] = 1.25f, ["searchBelow"] = 0.5f, ["searchAbove"] = 0.75f };

        // A missing owner and a missing space pass the bound gate and refuse through the effective owner check.
        Refused(f, new MapSetFoliageLayer(Change(layer, l => l["exceptionMasks"] = new JsonArray(Mask("ghost", Floor())))),
            "missing effective World Placement owner 'ghost'");
        Refused(f, new MapSetFoliageLayer(Change(layer, l => l["exceptionMasks"] = new JsonArray(Mask(FoliageFixtures.FloorId, Space("nowhere"))))),
            "missing effective support space 'nowhere'");

        // A valid new effective reference costs exactly one expansion and no payload write.
        var valid = new MapSetFoliageLayer(Change(layer, l => l["exceptionMasks"] = new JsonArray(Mask(FoliageFixtures.FloorId, Floor()))));
        long expansions = MapPrefabResolver.ThreadExpansions, writes = MapFoliageJson.ThreadPayloadWrites;
        MapPrefabEditResult result = MapFoliageEdits.Prepare(f.Document, f.Assets, valid);
        Assert.Equal((1L, 0L), (MapPrefabResolver.ThreadExpansions - expansions, MapFoliageJson.ThreadPayloadWrites - writes));
        Assert.Single(result.After.Foliage!.Layers[0].ExceptionMasks);
        // Once the reference is in the before document, a whole-layer edit of the same layer expands nothing, since its
        // layer entry needs no preparation.
        Pure(new(result.After, f.Assets), new MapSetFoliageLayer(Change(result.After.Foliage.Layers[0], l => l["seed"] = 18)));
    }

    [Fact]
    public void MetreBrush_SkipsUnreachableChunksBeforeResidency()
    {
        // Chunk (0, 0) is advertised but not resident. The disc centred on sample (48, 48) with a 20 sample radius has a
        // bounding box that covers the corner chunk, but its nearest sample (31, 31) is about 24 samples away.
        MapFoliageLayer zero = Zero(0, 0, 1, 96, 96);
        var advertised = new MapFoliageChunk(new(0, 0), Enumerable.Repeat((byte)5, MapFoliageChunk.ByteLength).ToArray());
        MapFoliageLayer partial = MapFoliageLayer.FromStorage(zero, [new(advertised.Key, advertised.Sha256)], []);
        MapFoliageLayer painted = MapFoliageBrush.Apply(partial, Paint(48, 48, 20, 90, 1));
        Assert.Equal((byte)90, painted.DensityAt(48, 48));
        Assert.Equal((byte)90, painted.DensityAt(34, 34));
        Assert.Contains(painted.ChunkDigests, d => d.Key == advertised.Key && d.Sha256 == advertised.Sha256);
        Assert.DoesNotContain(painted.Chunks, c => c.Key == advertised.Key);
        Assert.Contains(painted.Chunks, c => c.Key == new MapFoliageChunkKey(0, 1));
        Assert.Equal((byte)90, painted.DensityAt(31, 48));
        // A disc that reaches the advertised chunk still refuses rather than reading it as zero.
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageBrush.Apply(partial, Paint(48, 48, 25, 90, 1)));
        Assert.Contains("chunk MapFoliageChunkKey { X = 0, Z = 0 } is unavailable", error.Message, StringComparison.Ordinal);
    }

    static MapPaintFoliage Paint(float x, float z, float radius, byte target, float hardness) => new(Id, x, z, radius, target, hardness);

    static MapFoliageLayer Change(MapFoliageLayer layer, Action<JsonObject> change) => FoliageStorageFixtures.Change(layer, change);

    static MapFoliageLayer Zero(float originX, float originZ, float cell, int width, int depth) =>
        Change(FoliageFixtures.BasicLayer([]), l =>
        {
            l["originX"] = originX;
            l["originZ"] = originZ;
            l["cellSize"] = cell;
            l["width"] = width;
            l["depth"] = depth;
        });

    // A pure foliage edit serializes no payload. Finalization expands the document at most once, shared by both sides
    // since expansion never reads foliage, and a no-op edit expands nothing.
    static MapPrefabEditResult Pure(FoliageFixture f, MapFoliageEdit edit)
    {
        long expansions = MapPrefabResolver.ThreadExpansions, writes = MapFoliageJson.ThreadPayloadWrites;
        MapPrefabEditResult result = MapFoliageEdits.Prepare(f.Document, f.Assets, edit);
        Assert.Equal(result.Effects.FoliageChunks.Count == 0 ? 0L : 1L, MapPrefabResolver.ThreadExpansions - expansions);
        Assert.Equal(0L, MapFoliageJson.ThreadPayloadWrites - writes);
        return result;
    }

    static void Refused(FoliageFixture f, MapFoliageEdit edit, string? detail = null)
    {
        string beforeState = MapDocumentFile.SaveText(f.Document);
        var error = Assert.Throws<MapDocumentException>(() => MapFoliageEdits.Prepare(f.Document, f.Assets, edit));
        if (detail is not null) Assert.Contains(detail, error.Message, StringComparison.Ordinal);
        string refusedState = MapDocumentFile.SaveText(f.Document);
        Assert.Equal(beforeState, refusedState);
    }

    static byte[] Samples(MapFoliageLayer layer)
    {
        var values = new byte[layer.Width * layer.Depth];
        for (int z = 0; z < layer.Depth; z++)
            for (int x = 0; x < layer.Width; x++) values[z * layer.Width + x] = layer.DensityAt(x, z);
        return values;
    }

    // The released full-domain brush loop, copied as the reference for every reached sample.
    static byte[] Legacy(MapFoliageLayer layer, MapPaintFoliage brush)
    {
        byte[] values = Samples(layer);
        float radius = brush.RadiusMetres, hardness = brush.Hardness, hardRadius = radius * hardness;
        int density = brush.Target;
        for (int z = 0; z < layer.Depth; z++)
        {
            float sampleZ = layer.OriginZ + (z * layer.CellSize);
            for (int x = 0; x < layer.Width; x++)
            {
                float sampleX = layer.OriginX + (x * layer.CellSize);
                float distance = MathF.Sqrt(((sampleX - brush.WorldX) * (sampleX - brush.WorldX)) +
                    ((sampleZ - brush.WorldZ) * (sampleZ - brush.WorldZ)));
                if (distance > radius) continue;
                float strength = distance <= hardRadius || hardness == 1f ? 1f : (radius - distance) / (radius - hardRadius);
                int index = (z * layer.Width) + x;
                values[index] = (byte)Math.Clamp((int)MathF.Round(values[index] + ((density - values[index]) * strength)), 0, byte.MaxValue);
            }
        }
        return values;
    }
}
