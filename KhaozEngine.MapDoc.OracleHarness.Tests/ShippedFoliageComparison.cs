using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>One owned legacy candidate: the Task 3 channel decision over the actual legacy sampler, and that sampler's
/// detailed answer at the candidate's point. DomainFirst marks a point inside the raster on a tile of an absent source
/// region, where native decides its domain before its raster.</summary>
internal sealed record FoliageExpectedRow(GroundCoverDecision Decision, TileFoliageDecision Legacy, bool DomainFirst = false);

/// <summary>Compares the attested source foliage with four independently loaded native heads, a client and a server of
/// each storage form, chunk by chunk and candidate by candidate. Expected rows come only from the actual legacy sampler
/// and the Task 3 channel kernel. Actual rows come only from the native distribution. Successes stream into aggregate
/// counts, mismatch details are retained up to a fixed bound per category, and both stay in the private report.</summary>
[UnsupportedOSPlatform("windows")]
internal static partial class ShippedFoliageComparison
{
    internal const double PositionTolerance = 0.00001;
    internal const double OrientationTolerance = 0.000001;
    internal const string Scope = "private foliage comparison, neither R11 importer acceptance nor game adoption";
    const int RetainedDetails = 16;

    internal static void Compare(ShippedFoliageInputs inputs, PrivateOracleReport report)
    {
        var tally = new FoliageTally();
        var heads = Heads(inputs, tally);
        foreach (FoliageLayerBinding binding in inputs.Layers)
        {
            var nonzero = SourceChunks(binding.Source);
            tally.Add("rasterBytes", (long)binding.Source.Width * binding.Source.Height);
            tally.Add("storedChunks", nonzero.Count);
            tally.Add("models", binding.Source.Archetypes.Count);
            foreach (var (name, context) in heads)
                if (context is not null) RasterAndSettings(binding, Layer(context, binding), nonzero, name, tally);
        }
        var digests = heads.ToDictionary(h => h.Name, _ => IncrementalHash.CreateHash(HashAlgorithmName.SHA256), StringComparer.Ordinal);
        foreach (FoliageLayerBinding binding in inputs.Layers)
        {
            var surface = new TileFoliageSurface(inputs.World, inputs.Catalogs, binding.Source);
            GroundCoverSettings settings = Settings(binding.Source);
            foreach (RegionCoord region in Regions(inputs.World))
            {
                RectArea area = Area(region);
                Dictionary<(int X, int Z), FoliageExpectedRow> expected = Expected(surface, settings, area, out long scanned);
                Mark(expected, inputs.World, binding.Source);
                tally.Add("scannedCandidates", scanned);
                Count(expected.Values, tally);
                foreach (var (name, context) in heads)
                {
                    if (context is null) continue;
                    List<MapFoliageDecision>? actual = Actual(context, binding.Row.NativeLayerId, area);
                    if (actual is null)
                    {
                        tally.FailResource("foliage-query", Detail(name, binding, region), "layer", "query");
                        continue;
                    }
                    CompareRegion(binding, Layer(context, binding), context.Assets, expected, actual, name, tally, digests[name]);
                }
            }
        }
        string[] finals = heads.Where(h => h.Context is not null).Select(h => Convert.ToHexStringLower(digests[h.Name].GetHashAndReset())).ToArray();
        tally.Add("headMatches", finals.Count(d => d == finals.FirstOrDefault()));
        if (finals.Length != heads.Count || finals.Distinct(StringComparer.Ordinal).Count() != 1)
            tally.Fail("foliage-head", new Dictionary<string, object?> { ["heads"] = heads.Count, ["distinct"] = finals.Distinct(StringComparer.Ordinal).Count() });
        tally.Add("layers", inputs.World.FoliageLayers.Count);
        tally.Add("sourceOwners", inputs.SourceOwnerCount);
        Expectations(inputs.Paths.Expectations, tally);
        report.Record("foliage-summary", tally.Summary(heads.Count));
        tally.Report(report);
    }

    static List<(string Name, MapFoliageGenerationContext? Context)> Heads(ShippedFoliageInputs inputs, FoliageTally tally)
    {
        var heads = new List<(string, MapFoliageGenerationContext?)>();
        foreach (string name in new[] { "monolithic-client", "monolithic-server", "tiled-client", "tiled-server" })
        {
            MapDocument document = name.StartsWith("tiled", StringComparison.Ordinal) ? inputs.ReloadTiled() : inputs.ReloadDocument();
            // One full generation context per head, reused by every region and chunk call below.
            MapFoliageContextResult capture = MapFoliageGenerationContext.Capture(document, inputs.ReloadAssets(document));
            if (capture.Status != MapFoliageStatus.Complete)
                tally.Fail("foliage-context", new Dictionary<string, object?> { ["head"] = name, ["status"] = capture.Status.ToString() });
            heads.Add((name, capture.Context));
        }
        return heads;
    }

    static MapFoliageLayer Layer(MapFoliageGenerationContext context, FoliageLayerBinding binding) =>
        context.Foliage!.Layers.Single(l => l.Id == binding.Row.NativeLayerId);

    /// <summary>The released region area, half-open, exactly as the legacy cover build owns its candidates.</summary>
    internal static RectArea Area(RegionCoord region) => new(
        TileWorldSpace.WorldX(region.OriginX, 1f), TileWorldSpace.WorldZ(region.OriginZ + TileRegion.Size, 1f),
        TileWorldSpace.WorldX(region.OriginX + TileRegion.Size, 1f), TileWorldSpace.WorldZ(region.OriginZ, 1f));

    internal static GroundCoverSettings Settings(TileFoliageLayer layer) => new()
    {
        Seed = layer.Seed,
        Spacing = layer.Spacing,
        ScaleMin = layer.ScaleMin,
        ScaleMax = layer.ScaleMax,
        RootOffset = layer.RootOffset,
        Models = layer.Archetypes.Select(a => new GroundCoverModel(a.Id, a.Weight)).ToArray(),
    };

    /// <summary>Every source region, then every absent region that a layer raster's closed sample rectangle reaches, in
    /// signed region order. The released build generates nothing in an absent region, so native must not either.</summary>
    internal static IReadOnlyList<RegionCoord> Regions(TileWorldDocument world)
    {
        var regions = world.Regions.Keys.ToHashSet();
        foreach (TileFoliageLayer layer in world.FoliageLayers)
        {
            float maxX = layer.OriginX + (layer.Width - 1) * layer.CellSize, maxZ = layer.OriginZ + (layer.Height - 1) * layer.CellSize;
            RegionCoord low = RegionCoord.Of((int)MathF.Floor(TileWorldSpace.TileX(layer.OriginX, 1f)), (int)MathF.Floor(TileWorldSpace.TileZ(maxZ, 1f)));
            RegionCoord high = RegionCoord.Of((int)MathF.Floor(TileWorldSpace.TileX(maxX, 1f)), (int)MathF.Floor(TileWorldSpace.TileZ(layer.OriginZ, 1f)));
            for (int rz = low.Rz; rz <= high.Rz; rz++)
                for (int rx = low.Rx; rx <= high.Rx; rx++) regions.Add(new(rx, rz));
        }
        return regions.OrderBy(r => r.Rz).ThenBy(r => r.Rx).ToArray();
    }

    // Native decides its domain before its raster for a point inside the raster, and the raster first outside it. Only
    // a tile of an absent source region can differ, where the released sampler reads the raster first.
    static void Mark(Dictionary<(int X, int Z), FoliageExpectedRow> rows, TileWorldDocument world, TileFoliageLayer layer)
    {
        foreach (var key in rows.Keys.ToArray())
        {
            GroundCoverCandidate c = rows[key].Decision.Candidate;
            int x = (int)MathF.Floor(TileWorldSpace.TileX(c.WorldX, 1f)), z = (int)MathF.Floor(TileWorldSpace.TileZ(c.WorldZ, 1f));
            float u = (c.WorldX - layer.OriginX) / layer.CellSize, v = (c.WorldZ - layer.OriginZ) / layer.CellSize;
            if (world.GetRegion(RegionCoord.Of(x, z)) is null && !(u < 0f || v < 0f || u > layer.Width - 1 || v > layer.Height - 1))
                rows[key] = rows[key] with { DomainFirst = true };
        }
    }

    /// <summary>Every owned candidate of the area, from the actual legacy sampler through the Task 3 channel kernel.</summary>
    internal static Dictionary<(int X, int Z), FoliageExpectedRow> Expected(TileFoliageSurface surface, GroundCoverSettings settings,
        RectArea area, out long scanned)
    {
        var rows = new Dictionary<(int X, int Z), FoliageExpectedRow>();
        TileFoliageDecision last = default;
        long count = 0;
        GroundCoverDistribution.GenerateTraced(area, settings, candidate =>
        {
            last = surface.SampleDetailed(candidate.WorldX, candidate.WorldZ);
            return new GroundCoverSurfaceInput(last.Sample);
        }, decision =>
        {
            count++;
            if (decision.Kind != GroundCoverDecisionKind.OutsideArea) rows.Add((decision.Candidate.X, decision.Candidate.Z), new(decision, last));
        });
        scanned = count;
        return rows;
    }

    /// <summary>Every owned native decision of the area. A capacity refusal streams the area as four smaller half-open
    /// rectangles, which own exactly the same candidates. Rows are kept only from complete results.</summary>
    internal static List<MapFoliageDecision>? Actual(MapFoliageGenerationContext context, string layerId, RectArea area, int depth = 0)
    {
        var rows = new List<MapFoliageDecision>();
        MapFoliageQueryResult result = MapFoliageDistribution.Query(context, layerId, area, null, decision =>
        {
            if (decision.Distribution.Kind != GroundCoverDecisionKind.OutsideArea) rows.Add(decision);
        });
        if (result.Status == MapFoliageStatus.Complete) return rows;
        if (result.Status != MapFoliageStatus.CapacityExceeded || depth >= 16) return null;
        float midX = (area.MinX + area.MaxX) * 0.5f, midZ = (area.MinZ + area.MaxZ) * 0.5f;
        if (!(area.MinX < midX && midX < area.MaxX && area.MinZ < midZ && midZ < area.MaxZ)) return null;
        var all = new List<MapFoliageDecision>();
        foreach (RectArea part in new RectArea[]
        {
            new(area.MinX, area.MinZ, midX, midZ), new(midX, area.MinZ, area.MaxX, midZ),
            new(area.MinX, midZ, midX, area.MaxZ), new(midX, midZ, area.MaxX, area.MaxZ),
        })
        {
            List<MapFoliageDecision>? rowsOfPart = Actual(context, layerId, part, depth + 1);
            if (rowsOfPart is null) return null;
            all.AddRange(rowsOfPart);
        }
        return all;
    }

    static void CompareRegion(FoliageLayerBinding binding, MapFoliageLayer layer, MapAssetClosure assets,
        Dictionary<(int X, int Z), FoliageExpectedRow> expected, List<MapFoliageDecision> actual, string head, FoliageTally tally,
        IncrementalHash digest)
    {
        var seen = new HashSet<(int X, int Z)>();
        foreach (MapFoliageDecision row in actual)
        {
            var key = (row.Address.X, row.Address.Z);
            if (!expected.TryGetValue(key, out FoliageExpectedRow? want) || !seen.Add(key))
            {
                tally.FailResource("foliage-candidate", Detail(head, binding, key.X, key.Z, "unowned or repeated native address"), "sample", "address");
                continue;
            }
            tally.Add("comparedRows", 1);
            foreach (string category in CompareRow(binding, layer, assets, want, row))
                tally.FailResource(category, Detail(head, binding, key.X, key.Z, category), "sample", category["foliage-".Length..]);
            if (row.Sample is { } sample && head == "monolithic-client")
            {
                tally.Count("supportOwners", sample.Key.Address.SupportOwnerId ?? "");
                tally.Count("modelChoices", sample.AssetId);
            }
        }
        foreach (var key in expected.Keys.Where(k => !seen.Contains(k)))
            tally.FailResource("foliage-candidate", Detail(head, binding, key.X, key.Z, "missing native address"), "sample", "address");
        foreach (MapFoliageDecision row in actual.OrderBy(r => r.Address.Z).ThenBy(r => r.Address.X))
            digest.AppendData(Encoding.UTF8.GetBytes(Canonical(binding, row) + "\n"));
    }

    /// <summary>The categories one owned candidate fails. Channels, reason and acceptance compare exactly. An accepted
    /// pair also compares its key, model, materials, binary32 scale and rank bits, position within 0.00001 m per
    /// component and the full surface-aligned rotation angle within 0.000001 radians, after finite checks.</summary>
    internal static IReadOnlyList<string> CompareRow(FoliageLayerBinding binding, MapFoliageLayer layer, MapAssetClosure assets,
        FoliageExpectedRow expected, MapFoliageDecision actual)
    {
        var failed = new List<string>();
        GroundCoverCandidate want = expected.Decision.Candidate, got = actual.Distribution.Candidate;
        if (!(want.X == got.X && want.Z == got.Z && Bits(want.JitterX) == Bits(got.JitterX) && Bits(want.JitterZ) == Bits(got.JitterZ) &&
            Bits(want.DensityPick) == Bits(got.DensityPick) && Bits(want.ModelPick) == Bits(got.ModelPick) &&
            Bits(want.ScalePick) == Bits(got.ScalePick) && Bits(want.YawPick) == Bits(got.YawPick) && Bits(want.Rank) == Bits(got.Rank)))
            failed.Add("foliage-channel");
        // The Task 7 Decide order: a candidate outside the half-open clip gets no point and is never compared, and a refused
        // point has no support owner. Every other point carries the bound Surface, including off-raster RasterZero and
        // OutsideDomain, which are decided before support resolves and stamped with the requested surface.
        string? owner = actual.Reason == MapFoliageReason.Refused ? null : binding.Row.SupportSurfaceId;
        bool key = actual.Address.LayerId == binding.Row.NativeLayerId && actual.Address.SupportOwnerId == owner &&
            actual.Address.X == want.X && actual.Address.Z == want.Z;
        if (Reason(expected) != actual.Reason) failed.Add("foliage-decision");
        bool accepted = expected.Decision.Instance is not null;
        if (accepted && actual.Sample is null) failed.Add("foliage-missing-sample");
        if (!accepted && actual.Sample is not null) failed.Add("foliage-extra-sample");
        if (expected.Decision.Instance is not { } instance || actual.Sample is not { } sample)
        {
            if (!key) failed.Add("foliage-key");
            return failed;
        }

        MapFoliageSampleKey sampleKey = sample.Key;
        if (!key || !(sampleKey.Address == actual.Address && sampleKey.DistributionVersion == 1 && sampleKey.Policy.Id == "legacy-tile" &&
            sampleKey.Policy.Version == 1))
            failed.Add("foliage-key");
        if (!binding.Models.TryGetValue(instance.ModelId, out string? model) || model != sample.AssetId) failed.Add("foliage-model");
        string? resource = layer.Materials.FirstOrDefault(m => m.Id == sample.VisibleMaterial)?.ResourceId;
        if (sample.VisibleMaterial != expected.Legacy.VisibleMaterial || resource is null ||
            !binding.Materials.TryGetValue(expected.Legacy.VisibleMaterial, out string? source) || source != resource ||
            !sample.MaterialResourceIds.SequenceEqual(assets.GetAsset(sample.AssetId).MaterialResourceIds, StringComparer.Ordinal))
            failed.Add("foliage-material");
        Quaternion legacy = default;
        bool finite = Finite(instance.Position) && Finite(sample.Position) && Finite(sample.Orientation) &&
            float.IsFinite(expected.Decision.Scale) && float.IsFinite(sample.Scale) && float.IsFinite(instance.ThinningRank) &&
            float.IsFinite(sample.ThinningRank) && Matrix4x4.Decompose(instance.Transform, out _, out legacy, out _) && Finite(legacy);
        if (!finite)
        {
            failed.Add("foliage-nonfinite");
            return failed;
        }
        if (Math.Abs((double)instance.Position.X - sample.Position.X) > PositionTolerance ||
            Math.Abs((double)instance.Position.Y - sample.Position.Y) > PositionTolerance ||
            Math.Abs((double)instance.Position.Z - sample.Position.Z) > PositionTolerance)
            failed.Add("foliage-position");
        if (Angle(legacy, sample.Orientation) is not (>= 0 and <= OrientationTolerance)) failed.Add("foliage-orientation");
        if (Bits(expected.Decision.Scale) != Bits(sample.Scale)) failed.Add("foliage-scale");
        if (Bits(instance.ThinningRank) != Bits(sample.ThinningRank)) failed.Add("foliage-rank");
        return failed;
    }

    /// <summary>The full rotation angle between two orientations, normalized and evaluated in binary64 for comparison only.</summary>
    internal static double Angle(Quaternion a, Quaternion b)
    {
        double an = Math.Sqrt((double)a.X * a.X + (double)a.Y * a.Y + (double)a.Z * a.Z + (double)a.W * a.W);
        double bn = Math.Sqrt((double)b.X * b.X + (double)b.Y * b.Y + (double)b.Z * b.Z + (double)b.W * b.W);
        if (!(an > 0) || !(bn > 0)) return double.NaN;
        double dot = ((double)a.X * b.X + (double)a.Y * b.Y + (double)a.Z * b.Z + (double)a.W * b.W) / (an * bn);
        return 2 * Math.Acos(Math.Min(1, Math.Abs(dot)));
    }

    /// <summary>The native reason the legacy stage maps to. A legacy zero density names its stage.</summary>
    internal static MapFoliageReason Reason(FoliageExpectedRow row) => row.Decision.Kind switch
    {
        GroundCoverDecisionKind.Accepted => MapFoliageReason.Accepted,
        GroundCoverDecisionKind.DensityRejected => MapFoliageReason.DensityRejected,
        GroundCoverDecisionKind.ZeroDensity when row.DomainFirst => MapFoliageReason.OutsideDomain,
        GroundCoverDecisionKind.ZeroDensity => row.Legacy.Reason switch
        {
            TileFoliageRejection.ZeroRaster => MapFoliageReason.RasterZero,
            TileFoliageRejection.AbsentRegion => MapFoliageReason.OutsideDomain,
            TileFoliageRejection.VoidUnderlay => MapFoliageReason.MissingUnderlay,
            TileFoliageRejection.UnderlayNotAllowed => MapFoliageReason.MaterialNotAllowed,
            TileFoliageRejection.Water => MapFoliageReason.WaterMaterial,
            TileFoliageRejection.Indoors => MapFoliageReason.Indoor,
            TileFoliageRejection.Solid => MapFoliageReason.Solid,
            TileFoliageRejection.Roof => MapFoliageReason.RoofAbove,
            TileFoliageRejection.Door => MapFoliageReason.Door,
            TileFoliageRejection.EdgeFade => MapFoliageReason.EdgeZero,
            _ => MapFoliageReason.Refused,
        },
        _ => MapFoliageReason.Refused,
    };

    static void RasterAndSettings(FoliageLayerBinding binding, MapFoliageLayer native, HashSet<MapFoliageChunkKey> nonzero, string head,
        FoliageTally tally)
    {
        TileFoliageLayer source = binding.Source;
        var fields = new List<string>();
        void Check(string field, bool ok) { if (!ok) fields.Add(field); }
        Check("origin", Bits(native.OriginX) == Bits(source.OriginX) && Bits(native.OriginZ) == Bits(source.OriginZ));
        Check("cellSize", Bits(native.CellSize) == Bits(source.CellSize));
        Check("extent", native.Width == source.Width && native.Depth == source.Height && native.RowDirection == MapRowDirection.PositiveZ);
        Check("seed", native.Seed == source.Seed);
        Check("spacing", Bits(native.Spacing) == Bits(source.Spacing));
        Check("scale", Bits(native.ScaleMin) == Bits(source.ScaleMin) && Bits(native.ScaleMax) == Bits(source.ScaleMax));
        Check("rootOffset", Bits(native.RootOffset) == Bits(source.RootOffset));
        Check("weights", native.Models.Count == source.Archetypes.Count &&
            native.Models.Zip(source.Archetypes).All(p => Bits(p.First.Weight) == Bits(p.Second.Weight)));
        Check("allowed", native.AllowedVisibleMaterials.SequenceEqual(source.AllowedUnderlays));
        Check("exclusions", native.ExcludeIndoors == source.ExcludeIndoors && native.ExcludeSolidObjects == source.ExcludeSolidObjects);
        Check("door", Bits(native.DoorClearance) == Bits(source.DoorClearance));
        Check("edgeFade", Bits(native.EdgeFade) == Bits(source.EdgeFade));
        Check("policy", native.Policy.Id == "legacy-tile" && native.Policy.Version == 1);
        foreach (string field in fields) tally.FailResource("foliage-settings", Detail(head, binding, field), "layer", field);
        var advertised = native.ChunkDigests.Select(d => d.Key).ToHashSet();
        if (!advertised.SetEquals(nonzero)) tally.FailResource("foliage-chunk", Detail(head, binding, "advertised chunks differ from nonzero source chunks"), "layer", "chunks");
        if (native.Width != source.Width || native.Depth != source.Height) return;
        for (int z = 0; z < source.Height; z++)
            for (int x = 0; x < source.Width; x++)
            {
                byte want = source.DensityAt(x, z);
                byte? got;
                try { got = native.DensityAt(x, z); }
                catch (MapDocumentException) { got = null; }
                tally.Add("comparedBytes", 1);
                if (got is null) tally.FailResource("foliage-chunk", Detail(head, binding, x, z, "unavailable advertised chunk"), "chunk", "availability");
                else if (got != want) tally.FailResource("foliage-byte", Detail(head, binding, x, z, "density byte"), "chunk", "density");
            }
    }

    /// <summary>The 32 by 32 chunks of the source raster holding a nonzero byte, which is exactly what a native store advertises.</summary>
    internal static HashSet<MapFoliageChunkKey> SourceChunks(TileFoliageLayer layer)
    {
        var keys = new HashSet<MapFoliageChunkKey>();
        for (int z = 0; z < layer.Height; z++)
            for (int x = 0; x < layer.Width; x++)
                if (layer.DensityAt(x, z) != 0) keys.Add(new(x / MapFoliageChunk.Size, z / MapFoliageChunk.Size));
        return keys;
    }

    static void Count(IEnumerable<FoliageExpectedRow> rows, FoliageTally tally)
    {
        foreach (FoliageExpectedRow row in rows)
        {
            tally.Add("ownedCandidates", 1);
            if (row.Decision.Instance is not null) tally.Add("accepted", 1);
            else
            {
                tally.Add("rejected", 1);
                tally.Count("rejections", row.Decision.Kind == GroundCoverDecisionKind.DensityRejected ? "DensityRejected" : row.Legacy.Reason.ToString());
            }
        }
    }

    static void Expectations(FoliageExpectations want, FoliageTally tally)
    {
        foreach (var (category, field, value) in new[]
        {
            ("expectation-layers", "layers", want.Layers), ("expectation-raster-bytes", "rasterBytes", want.RasterBytes),
            ("expectation-stored-chunks", "storedChunks", want.StoredChunks), ("expectation-source-owners", "sourceOwners", want.SourceOwners),
            ("expectation-models", "models", want.Models), ("expectation-owned-candidates", "ownedCandidates", want.OwnedCandidates),
            ("expectation-accepted", "accepted", want.Accepted), ("expectation-rejected", "rejected", want.Rejected),
        })
            if (tally.Get(field) != value)
                tally.Fail(category, new Dictionary<string, object?> { ["expected"] = value, ["actual"] = tally.Get(field) });
    }

    static string Canonical(FoliageLayerBinding binding, MapFoliageDecision row)
    {
        var text = new StringBuilder();
        text.Append(binding.Row.NativeLayerId).Append('|').Append(row.Address.X).Append('|').Append(row.Address.Z).Append('|')
            .Append(row.Address.SupportOwnerId).Append('|').Append(row.Reason).Append('|').Append(row.Distribution.Kind);
        if (row.Sample is { } s)
            text.Append('|').Append(s.AssetId).Append('|').Append(s.VisibleMaterial).Append('|')
                .AppendJoin(',', new[] { s.Position.X, s.Position.Y, s.Position.Z, s.Orientation.X, s.Orientation.Y, s.Orientation.Z,
                    s.Orientation.W, s.Scale, s.ThinningRank }.Select(v => Bits(v).ToString("x8", CultureInfo.InvariantCulture)));
        return text.ToString();
    }

    static Dictionary<string, object?> Detail(string head, FoliageLayerBinding binding, string what) =>
        new() { ["head"] = head, ["layer"] = binding.Row.NativeLayerId, ["what"] = what };

    static Dictionary<string, object?> Detail(string head, FoliageLayerBinding binding, RegionCoord region) =>
        new() { ["head"] = head, ["layer"] = binding.Row.NativeLayerId, ["regionX"] = region.Rx, ["regionZ"] = region.Rz };

    static Dictionary<string, object?> Detail(string head, FoliageLayerBinding binding, int x, int z, string what) =>
        new() { ["head"] = head, ["layer"] = binding.Row.NativeLayerId, ["x"] = x, ["z"] = z, ["what"] = what };

    static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    static bool Finite(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);

}
