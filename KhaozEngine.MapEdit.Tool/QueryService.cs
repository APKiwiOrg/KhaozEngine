using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEdit;

/// <summary>Read-only world queries over the session's open document: ground sampling, walkability, rect scans
/// over placements/spawns and a scatter layer preview, and a brute-force flat-area search. Every query reads
/// through <see cref="MapEditSession.Field()"/> and <see cref="MapEditSession.WithDocument{T}"/>. None mutate.</summary>
public sealed class QueryService(MapEditSession session)
{
    /// <summary>Ground height, slope, and water depth at a single world point. <c>SlopeDegrees</c> is the angle
    /// between the surface normal and +Y, in degrees. <c>BelowWater</c> is <c>Height &lt; field.WaterLevel</c>.</summary>
    public GroundInfo GroundHeight(float x, float z)
    {
        TerrainField field = session.Field();
        float height = field.SampleHeight(x, z);
        float slopeDegrees = SlopeDegrees(field.SampleNormal(x, z));
        return new GroundInfo(x, z, height, slopeDegrees, field.WaterLevel, height < field.WaterLevel);
    }

    /// <summary>Whether a world point is walkable. The engine's <see cref="TerrainCollision.IsWalkable"/> gate is
    /// slope-only, so this composes it with a water gate here: submerged ground is never walkable regardless of
    /// slope.</summary>
    public WalkableInfo IsWalkable(float x, float z, float maxSlopeDegrees = 45f)
    {
        TerrainField field = session.Field();
        var collision = new TerrainCollision(field);
        float maxSlopeRadians = maxSlopeDegrees * MathF.PI / 180f;
        bool slopeOk = collision.IsWalkable(x, z, maxSlopeRadians);
        float height = field.SampleHeight(x, z);
        bool belowWater = height < field.WaterLevel;
        float slopeDegrees = SlopeDegrees(field.SampleNormal(x, z));
        return new WalkableInfo(x, z, slopeOk && !belowWater, slopeDegrees, maxSlopeDegrees, belowWater);
    }

    /// <summary>Placements and spawns whose position falls inside the inclusive rect
    /// (<c>minX &lt;= p.X &lt;= maxX &amp;&amp; minZ &lt;= p.Z &lt;= maxZ</c>). A null-Y placement resolves to
    /// the field's sampled ground height (<see cref="PlacementEntry.ExplicitY"/> flags which). Spawns always
    /// resolve to ground height.</summary>
    public PlacementsInRectResult PlacementsInRect(float minX, float minZ, float maxX, float maxZ)
    {
        return session.WithDocument((doc, _) =>
        {
            TerrainField field = session.Field();

            PlacementEntry[] placements = doc.Placements
                .Where(p => p.X >= minX && p.X <= maxX && p.Z >= minZ && p.Z <= maxZ)
                .Select(p => new PlacementEntry(p.Id, p.Kind, p.X, p.Y ?? field.SampleHeight(p.X, p.Z), p.Z,
                    p.Yaw, p.Scale, p.Y != null, p.Tags.ToArray()))
                .ToArray();

            SpawnEntry[] spawns = doc.Spawns
                .Where(s => s.X >= minX && s.X <= maxX && s.Z >= minZ && s.Z <= maxZ)
                .Select(s => new SpawnEntry(s.Id, s.ArchetypeId, s.X, field.SampleHeight(s.X, s.Z), s.Z,
                    s.Enabled, s.Tags.ToArray()))
                .ToArray();

            return new PlacementsInRectResult(placements, spawns);
        });
    }

    /// <summary>Previews a scatter layer's generated props over a rect without baking them. Throws
    /// <see cref="MapDocumentException"/> for an unknown layer, propagated from
    /// <see cref="MapRuntime.BuildScatterConfig"/>. <see cref="ScatterPreviewResult.Total"/> is the full
    /// generated count. Entries are capped at <paramref name="maxResults"/>, with
    /// <see cref="ScatterPreviewResult.Truncated"/> flagging the cap so results are never silently dropped.</summary>
    public ScatterPreviewResult ScatterPreviewInRect(string layer, float minX, float minZ, float maxX, float maxZ,
        int maxResults = 500)
    {
        return session.WithDocument((doc, _) =>
        {
            TerrainField field = session.Field();
            ScatterConfig config = MapRuntime.BuildScatterConfig(doc, layer);
            IReadOnlyList<PropPlacement> generated = PropScatter.Generate(field, config,
                new RectArea(minX, minZ, maxX, maxZ));

            ScatterEntry[] entries = generated.Take(maxResults)
                .Select(p => new ScatterEntry(p.Id, p.X, p.Y, p.Z, p.Yaw, p.Scale))
                .ToArray();

            return new ScatterPreviewResult(layer, generated.Count, generated.Count > maxResults, entries);
        });
    }

    /// <summary>Brute-force grid search for flat spots. The search rect defaults to the document bounds.
    /// Candidate centers step across the rect at <c>max(1, radius / 2)</c>, skipping any whose disc of
    /// <paramref name="radius"/> leaves the rect. Each surviving candidate samples its center plus 8 ring points
    /// at half radius and 8 at full radius (17 samples): all must clear the slope gate, all must be above water
    /// when <paramref name="aboveWater"/>, and the sampled height spread must not exceed
    /// <paramref name="maxHeightSpread"/>. Passing candidates sort by max slope ascending, then height spread
    /// ascending, then X, then Z (fully deterministic), and are capped at <paramref name="maxResults"/>.</summary>
    public FlatAreaResult FindFlatArea(float radius, float maxSlopeDegrees = 30f, float maxHeightSpread = 1.0f,
        float? minX = null, float? minZ = null, float? maxX = null, float? maxZ = null,
        bool aboveWater = true, int maxResults = 5)
    {
        return session.WithDocument((doc, _) =>
        {
            TerrainField field = session.Field();

            float rectMinX = minX ?? doc.Bounds.MinX;
            float rectMinZ = minZ ?? doc.Bounds.MinZ;
            float rectMaxX = maxX ?? doc.Bounds.MaxX;
            float rectMaxZ = maxZ ?? doc.Bounds.MaxZ;
            float maxSlopeRadians = maxSlopeDegrees * MathF.PI / 180f;
            float step = MathF.Max(1f, radius / 2f);

            var candidates = new List<FlatSpot>();
            for (float x = rectMinX; x <= rectMaxX; x += step)
            {
                if (x - radius < rectMinX || x + radius > rectMaxX) continue;

                for (float z = rectMinZ; z <= rectMaxZ; z += step)
                {
                    if (z - radius < rectMinZ || z + radius > rectMaxZ) continue;

                    FlatSpot? spot = EvaluateCandidate(field, x, z, radius, maxSlopeRadians, maxHeightSpread, aboveWater);
                    if (spot is not null) candidates.Add(spot);
                }
            }

            List<FlatSpot> spots = candidates
                .OrderBy(s => s.MaxSlopeDegrees)
                .ThenBy(s => s.HeightSpread)
                .ThenBy(s => s.X)
                .ThenBy(s => s.Z)
                .Take(maxResults)
                .ToList();

            return new FlatAreaResult(radius, spots);
        });
    }

    static FlatSpot? EvaluateCandidate(TerrainField field, float cx, float cz, float radius, float maxSlopeRadians,
        float maxHeightSpread, bool aboveWater)
    {
        float maxSlopeRadiansSeen = 0f;
        float minHeight = float.MaxValue;
        float maxHeight = float.MinValue;

        foreach ((float x, float z) in SamplePoints(cx, cz, radius))
        {
            float height = field.SampleHeight(x, z);
            float slopeRadians = MathF.Acos(Math.Clamp(field.SampleNormal(x, z).Y, 0f, 1f));

            if (slopeRadians > maxSlopeRadians || (aboveWater && height < field.WaterLevel))
                return null;

            maxSlopeRadiansSeen = MathF.Max(maxSlopeRadiansSeen, slopeRadians);
            minHeight = MathF.Min(minHeight, height);
            maxHeight = MathF.Max(maxHeight, height);
        }

        float heightSpread = maxHeight - minHeight;
        if (heightSpread > maxHeightSpread) return null;

        return new FlatSpot(cx, cz, field.SampleHeight(cx, cz), maxSlopeRadiansSeen * 180f / MathF.PI, heightSpread);
    }

    /// <summary>The center plus 8 ring points at half radius and 8 at full radius (17 samples total).</summary>
    static IEnumerable<(float x, float z)> SamplePoints(float cx, float cz, float radius)
    {
        yield return (cx, cz);
        foreach (float ringRadius in new[] { radius * 0.5f, radius })
        {
            for (int i = 0; i < 8; i++)
            {
                float angle = i * MathF.PI / 4f;
                yield return (cx + MathF.Cos(angle) * ringRadius, cz + MathF.Sin(angle) * ringRadius);
            }
        }
    }

    static float SlopeDegrees(Vector3 normal) => MathF.Acos(Math.Clamp(normal.Y, 0f, 1f)) * 180f / MathF.PI;
}
