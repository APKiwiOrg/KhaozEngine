using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

/// <summary>Pure geometry-derived extension. No provider, physical query or new ordinary node is involved.</summary>
internal static class ContactTerrainDropCandidates
{
    internal static IReadOnlyList<NavLink> Generate(GroundNavigation navigation, PhysicsNavDescentEvidence evidence,
        ContactTerrainProfileEvidence sources, MoveTuning tuning, float allowance, (int Layer, int X, int Z)? onlySource = null)
        => Enumerate(navigation, evidence, sources, tuning, allowance, onlySource).ToArray();

    internal static IEnumerable<NavLink> Enumerate(GroundNavigation navigation, PhysicsNavDescentEvidence evidence,
        ContactTerrainProfileEvidence sources, MoveTuning tuning, float allowance, (int Layer, int X, int Z)? onlySource = null)
    {
        if (!float.IsFinite(allowance) || allowance < 0) throw new ArgumentOutOfRangeException(nameof(allowance));
        if (allowance == 0) yield break;
        var grids = navigation.Space.Layers;
        var layers = navigation.Graph.Layers;
        using var sharp = ContactDropCandidates.Enumerate(grids, layers, tuning, allowance).GetEnumerator();
        bool hasSharp = sharp.MoveNext();
        float cos = MathF.Cos(tuning.MaxSlopeRadians);
        var admitted = new ContactTerrainAdmittedIndex(navigation);
        var bands = new ContactTerrainBandIndex(evidence, Components(evidence, cos), tuning);
        var certified = new Dictionary<(int X, int Z, int SourceBits), bool>();
        for (int layer = 0; layer < grids.Count; layer++)
        {
            var grid = grids[layer];
            for (int z = 0; z < grid.Height; z++)
                for (int x = 0; x < grid.Width; x++)
                {
                    if (!layers[layer].IsAccepted(x, z)) continue;
                    var from = ContactDropProof.Feet(grid, x, z);
                    for (int direction = 0; direction < 8; direction++)
                    {
                        if ((layers[layer].ExitMask(x, z) & (1 << direction)) != 0) continue;
                        int dx = NavTraversalLayer.NeighborX(direction), dz = NavTraversalLayer.NeighborZ(direction);
                        bool selectedSource = onlySource is not { } selected || (selected.Layer == layer && selected.X == x && selected.Z == z);
                        if (hasSharp && sharp.Current.FromLayer == layer && sharp.Current.FromX == x && sharp.Current.FromZ == z &&
                            Math.Sign(sharp.Current.ToX - x) == dx && Math.Sign(sharp.Current.ToZ - z) == dz)
                        {
                            if (selectedSource) yield return sharp.Current;
                            hasSharp = sharp.MoveNext();
                            continue;
                        }
                        if (!selectedSource) continue;
                        int range = bands.Reach(x, z, direction);
                        if (range == 0 || !sources.TryGet(layer, x, z, out var raw)) continue;
                        bool steep = false;
                        int nextAdmitted = admitted.Next(direction, x, z);
                        for (int d = 1; d <= range; d++)
                        {
                            long lx = x + (long)dx * d, lz = z + (long)dz * d;
                            if (lx < 0 || lz < 0 || lx >= grid.Width || lz >= grid.Height) break;
                            int nx = (int)lx, nz = (int)lz;
                            var columnKey = (nx, nz, BitConverter.SingleToInt32Bits(from.Y));
                            if (!certified.TryGetValue(columnKey, out bool valid))
                            {
                                valid = ContactTerrainDropCorridor.CertifyColumn(evidence, nx, nz, (double)from.Y - allowance,
                                    (double)from.Y + tuning.StepHeight, 2d * tuning.CapsuleHalfHeight, navigation.Footprint.Areas);
                                certified.Add(columnKey, valid);
                            }
                            if (!valid) break;
                            foreach (var face in evidence.Column(nx, nz))
                            {
                                var normal = evidence.Face(face.Face).Normal;
                                if (evidence.Face(face.Face).Analytic && normal.Y > 0 && normal.Y < cos)
                                {
                                    if ((double)normal.X * dx + (double)normal.Z * dz <= 0) goto Refused;
                                    steep = true;
                                }
                            }
                            if (nz * grid.Width + nx != nextAdmitted) continue;
                            nextAdmitted = admitted.Next(direction, nx, nz);
                            int target = -1; float highest = float.NegativeInfinity; bool continuation = false;
                            for (int next = 0; next < grids.Count; next++)
                            {
                                if (!layers[next].IsAccepted(nx, nz)) continue;
                                float to = grids[next].RestingHeightAt(nx, nz)!.Value;
                                double drop = (double)from.Y - to;
                                if (Math.Abs(drop) <= tuning.StepHeight) continuation = true;
                                if (drop > tuning.StepHeight && drop <= allowance && to > highest) { target = next; highest = to; }
                            }
                            if (continuation) break;
                            if (target < 0) continue;
                            if (steep && sources.TryGet(target, nx, nz, out var landing) &&
                                evidence.Face(landing.Face).Key.Source == evidence.Face(raw.Face).Key.Source)
                                yield return new(layer, x, z, target, nx, nz) { Kind = NavLinkKind.Drop };
                            break;
                        }
                    Refused:;
                    }
                }
        }
    }

    static List<int[]> Components(PhysicsNavDescentEvidence e, float cos)
    {
        var seen = new HashSet<int>(); var output = new List<int[]>();
        for (int i = 0; i < e.FaceCount; i++)
        {
            var f = e.Face(i); if (!f.Analytic || f.Normal.Y <= 0 || f.Normal.Y >= cos || !seen.Add(i)) continue;
            var component = new List<int> { i }; var queue = new Queue<int>(); queue.Enqueue(i);
            while (queue.TryDequeue(out int next))
                foreach (int joined in e.Joins(next))
                { var face = e.Face(joined); if (face.Analytic && face.Normal.Y > 0 && face.Normal.Y < cos && seen.Add(joined)) { component.Add(joined); queue.Enqueue(joined); } }
            output.Add(component.ToArray());
        }
        return output;
    }
}
