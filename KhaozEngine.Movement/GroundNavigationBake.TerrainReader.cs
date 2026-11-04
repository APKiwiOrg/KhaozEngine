using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

public sealed partial class GroundNavigationBake
{
    static string? DecodeTerrain(ReadOnlySpan<byte> payload, LoadPlan plan, ReadOnlySpan<byte> identity,
        out Vector3 origin, out PhysicsNavColumns? columns, out GroundNavigation[]? profiles, out PhysicsNavDescentEvidence? evidence)
    {
        profiles = null; evidence = null;
        var reader = new NavBakeReader(payload);
        if (DecodeColumns(ref reader, plan, out origin, out columns) is { } problem) return problem;
        if (!reader.TryReadInt32(out int length) || !reader.TryReadBytes(length, out var geometry)) return "Terrain evidence is truncated.";
        evidence = TerrainEvidenceDecoder.Decode(geometry, plan.Options, columns!);
        var result = new GroundNavigation[plan.Profiles.Length];
        var scratch = new LayerScratch(new bool[plan.Cells], new bool[plan.Cells], new float[plan.Cells], new byte[plan.Cells], new float[plan.Cells]);
        for (int i = 0; i < result.Length; i++)
        {
            if (!reader.TryReadInt32(out int bytes) || !reader.TryReadBytes(bytes, out var data) || !reader.TryReadBytes(32, out var digest)) return "Terrain profile is truncated.";
            if (DecodeTerrainProfile(data, plan, plan.Profiles[i], columns!, scratch, evidence, identity, geometry, digest, out var navigation) is { } failed) return failed;
            result[i] = navigation!;
        }
        if (reader.Remaining != 0) return "Terrain payload has trailing bytes.";
        profiles = result;
        return null;
    }

    static string? DecodeTerrainProfile(ReadOnlySpan<byte> data, LoadPlan plan, NavBakeProfile profile,
        PhysicsNavColumns columns, LayerScratch scratch, PhysicsNavDescentEvidence evidence,
        ReadOnlySpan<byte> identity, ReadOnlySpan<byte> geometry, ReadOnlySpan<byte> expectedDigest, out GroundNavigation? navigation)
    {
        var reader = new NavBakeReader(data);
        navigation = null;
        string name = profile.Name;
        int cells = plan.Cells, maskBytes = BitsetLength(cells);
        if (!reader.TryReadInt32(out int layerCount)) return $"The payload is truncated before profile '{name}'.";
        if (layerCount < 1 || layerCount > plan.MaxLayers || layerCount > reader.Remaining / (LayerHeaderBytes + maskBytes))
            return $"Profile '{name}' layer count {layerCount} is impossible for the layer budget and the remaining bytes.";

        var grids = new NavGrid[layerCount];
        var traversal = new NavTraversalLayer[layerCount];
        (bool[] blocked, bool[] accepted, float[] heights, byte[] exits, float[]? resting) = scratch;
        bool contact = true && profile.Tuning.Controller == MovementControllerKind.Contact;
        PhysicsNavBakeOptions options = plan.Options;
        for (int layer = 0; layer < layerCount; layer++)
        {
            if (!reader.TryReadInt32(out int width) || !reader.TryReadInt32(out int height) ||
                !reader.TryReadSingle(out float cellSize) || !reader.TryReadSingle(out float originX) ||
                !reader.TryReadSingle(out float originZ) || !reader.TryReadSingle(out float yaw) ||
                !reader.TryReadSingle(out float yMin) || !reader.TryReadSingle(out float yMax) ||
                !reader.TryReadBytes(maskBytes, out ReadOnlySpan<byte> mask))
                return $"Profile '{name}' layer {layer} is truncated.";
            if (width != plan.Width || height != plan.Height)
                return $"Profile '{name}' layer {layer} dimensions {width} by {height} differ from the capture.";
            if (Bits(cellSize) != Bits(options.CellSize) || Bits(originX) != Bits(options.MinX) ||
                Bits(originZ) != Bits(options.MinZ) || Bits(yaw) != 0u)
                return $"Profile '{name}' layer {layer} placement differs from the options.";
            if (float.IsNaN(yMin) || float.IsNaN(yMax)) return $"Profile '{name}' layer {layer} has a NaN band.";
            if (!UnusedBitsClear(mask, cells)) return $"Profile '{name}' layer {layer} blocked bitset has unused bits set.";

            int open = 0;
            for (int i = 0; i < cells; i++)
            {
                blocked[i] = IsSet(mask, i);
                accepted[i] = !blocked[i];
                if (accepted[i]) open++;
            }
            if (open * (contact ? 9L : 5L) > reader.Remaining) return $"Profile '{name}' layer {layer} is truncated in its open cells.";
            for (int i = 0; i < cells; i++)
            {
                heights[i] = 0f;
                if (blocked[i]) continue;
                if (!reader.TryReadSingle(out heights[i])) return $"Profile '{name}' layer {layer} is truncated in its heights.";
                if (!float.IsFinite(heights[i])) return $"Profile '{name}' layer {layer} open height at cell {i} is not finite.";
            }
            for (int i = 0; i < cells; i++)
            {
                exits[i] = 0;
                if (!blocked[i] && !reader.TryReadUInt8(out exits[i]))
                    return $"Profile '{name}' layer {layer} is truncated in its exits.";
            }
            if (FirstBadExit(blocked, exits, width, height) is int bad)
                return $"Profile '{name}' layer {layer} cell {bad} has an exit to a blocked or outside cell.";
            grids[layer] = NavGrid.FromBlockedSurfaces(width, height, cellSize, originX, originZ, blocked, heights, yMin, yMax, yaw);
            if (contact)
            {
                Array.Copy(heights, resting!, cells);
                for (int i = 0; i < cells; i++)
                {
                    if (blocked[i]) continue;
                    if (!reader.TryReadSingle(out resting![i])) return $"Profile '{name}' layer {layer} is truncated in its resting heights.";
                    if (!float.IsFinite(resting[i]) || (double)resting[i] < (double)heights[i] - profile.Tuning.GroundedEpsilon ||
                        (double)resting[i] > (double)heights[i] + profile.Tuning.StepHeight)
                        return $"Profile '{name}' layer {layer} resting height at cell {i} is outside its raw height band.";
                }
                grids[layer] = grids[layer].WithRestingHeights(resting!);
            }
            traversal[layer] = new NavTraversalLayer(width, height, accepted, exits);
        }

        if (!reader.TryReadInt32(out int stored) || stored < 0 || BitsetLength(stored) != reader.Remaining)
            return "Terrain candidate count is impossible for the remaining bytes.";
        if (!reader.TryReadBytes(BitsetLength(stored), out var bits) || !UnusedBitsClear(bits, stored)) return "Terrain acceptance bits are invalid.";
        var space = new NavSpace(grids, []);
        var empty = new NavTraversalGraph(space, profile.Tuning.CapsuleRadius,
            2f * profile.Tuning.CapsuleHalfHeight, traversal, []);
        PhysicsNavColumns footprintColumns = profile.Aquatic ? AquaticColumns.Derive(columns, profile.Tuning) : columns;
        var ground = new NavAreaFootprint(footprintColumns, options, profile.Tuning, profile.Areas);
        var basis = new GroundNavigation(empty, profile.Tuning, ground, profile.Aquatic, profile.MaxDropMetres, profile.AllowTerrainDescents);
        var sources = profile.AllowTerrainDescents ? new ContactTerrainProfileEvidence(evidence, basis) : null;
        var digest = TerrainWorkload.Digest(identity, geometry, profile.Name, data,
            RegenerateTerrainProfile(basis, evidence, sources), out int regenerated);
        if (regenerated != stored || !digest.AsSpan().SequenceEqual(expectedDigest)) return "The frozen terrain workload does not match.";
        // Only the authenticated, regenerated count sizes the candidate array.
        var candidates = new NavLink[regenerated]; int next = 0;
        foreach (var link in RegenerateTerrainProfile(basis, evidence, sources))
        {
            if (next == candidates.Length) return "Terrain candidate enumeration changed.";
            candidates[next++] = link;
        }
        if (next != candidates.Length) return "Terrain candidate enumeration changed.";
        var links = new List<NavLink>();
        for (int i = 0; i < candidates.Length; i++) if (IsSet(bits, i)) links.Add(candidates[i]);
        var graph = new NavTraversalGraph(new NavSpace(grids, candidates), profile.Tuning.CapsuleRadius,
            2f * profile.Tuning.CapsuleHalfHeight, traversal, links);
        navigation = new GroundNavigation(graph, profile.Tuning, ground, profile.Aquatic, profile.MaxDropMetres, profile.AllowTerrainDescents);
        if (profile.AllowTerrainDescents) navigation.RebindTerrainEvidence(evidence);
        return null;
    }

    static IEnumerable<NavLink> RegenerateTerrainProfile(GroundNavigation navigation, PhysicsNavDescentEvidence evidence,
        ContactTerrainProfileEvidence? sources)
    {
        if (!navigation.AllowTerrainDescents)
            return navigation.MaxDropMetres > 0
                ? ContactDropTopology.Enumerate(navigation.Space.Layers, navigation.Graph.Layers, navigation.Identity.Tuning, navigation.MaxDropMetres)
                : NavLayerLinks.EnumerateGrounded(navigation.Space.Layers, navigation.Identity.Tuning.StepHeight);
        return TerrainLinks();
        IEnumerable<NavLink> TerrainLinks()
        {
            foreach (var link in NavLayerLinks.EnumerateGrounded(navigation.Space.Layers, navigation.Identity.Tuning.StepHeight)) yield return link;
            foreach (var link in ContactTerrainDropCandidates.Enumerate(navigation, evidence, sources!, navigation.Identity.Tuning, navigation.MaxDropMetres)) yield return link;
        }
    }
}
