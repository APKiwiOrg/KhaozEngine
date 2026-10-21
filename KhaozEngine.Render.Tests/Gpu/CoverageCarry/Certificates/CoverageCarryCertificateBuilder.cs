using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

// Finite witness search, deliberately separate from the verifier. No symbolic solver, animation prediction,
// geometric tolerance or sampled-pose verdict participates in accepting a domain.
internal static class CoverageCarryCertificateBuilder
{
    internal static CoverageCarryBuild Build(CoverageCarryFixture fixture, int pose, Matrix4x4? equipment = null)
    {
        var palette = CoverageCarryFixture.Palette(pose);
        var commands = fixture.Commands(0, equipment ?? palette[0]);
        var exact = new CoverageCarryAnchor(fixture.Topology, palette, commands);
        var attempts = new List<CoverageCarryAttempt>();
        var candidates = new List<CoverageCarryCell>();
        foreach (float reserve in new[] { 0f, .00025f, .00050f, .00100f })
        {
            var anchor = Reserve(exact, palette, commands, reserve);
            var seed = new CoverageCarryCell(palette, 0, anchor, commands,
                CoverageCarryCellBinding.Inputs(fixture.Topology, anchor, commands), "", [], reserve, pose == 0 ? "lifted" : "bent");
            var point = Attempt(seed, attempts);
            if (!point.Check.Accepted) continue;
            for (int division = 0; division < 19; division++)
            {
                float radius = MathF.ScaleB(.002f, -division);
                var row = Attempt(seed with { Radius = radius }, attempts);
                if (!row.Check.Accepted) continue;
                candidates.Add(row.Cell);
                if (reserve == 0) return new(row.Cell, attempts.ToArray(), candidates.ToArray());
                break;
            }
        }
        return new(candidates.FirstOrDefault(), attempts.ToArray(), candidates.ToArray());
    }

    internal static CoverageCarryAnchor Reserve(CoverageCarryAnchor exact, Matrix4x4[] palette,
        CoverageCommandPose[] commands, float world)
    {
        if (world == 0) return exact;
        if (world is not (.00025f or .00050f or .00100f)) throw new ArgumentOutOfRangeException(nameof(world));
        var t = exact.Topology; var scratch = new CoverageReferenceScratch(t);
        if (!CoverageReferenceFrames.TryPrepare(t, Matrix4x4.Identity, commands, scratch, out var failure))
            throw new InvalidOperationException(failure.ToString());
        var full = exact.Recession.ToArray();
        for (int g = 0; g < full.Length; g++)
        {
            int v = exact.Representatives[g];
            if (v < 0 || full[g] == 0) continue;
            var record = t.Vertices[v]; var frame = scratch.Frames[scratch.RegionCommands[record.RegionIndex]];
            var d = CoverageBoundedVector.From(frame.Recess).Length;
            // Round the added world reserve outward. The checker subsequently proves the unchanged cap.
            double amount = world * (double)record.Strength / d.Min;
            full[g] = MathF.BitIncrement((float)(full[g] + amount));
        }
        return new(exact, full);
    }

    internal static (CoverageCarryCell Cell, CoverageCarryCheck Check) Attempt(CoverageCarryCell seed,
        List<CoverageCarryAttempt>? attempts = null)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
        var cell = Propose(seed);
        var check = CoverageCarryCertificateChecker.Check(cell);
        watch.Stop();
        using var process = Process.GetCurrentProcess();
        long? peak = process.PeakWorkingSet64 > 0 ? process.PeakWorkingSet64 : null;
        attempts?.Add(new(cell.Motion, cell.ReserveWorld, cell.Radius, check, watch.Elapsed.TotalMilliseconds,
            GC.GetAllocatedBytesForCurrentThread() - allocated, peak,
            CoverageCarryCellBinding.ProofBytes(cell)));
        return (cell, check);
    }

    static CoverageCarryCell Propose(CoverageCarryCell seed)
    {
        var t = seed.Anchor.Topology; var enclosure = new CoverageCarryEnclosure(seed);
        var triangles = Enumerable.Range(0, t.TriangleCount).Select(i => CoverageCarryProofGeometry.Triangle(t, enclosure.After, i)).ToArray();
        var groups = Enumerable.Range(0, t.TriangleCount).Select(i => CoverageCarryProofGeometry.Groups(t, i)).ToArray();
        var planes = new List<CoverageCarryPlane>();
        for (int f = 0; f < triangles.Length; f++)
            for (int g = f + 1; g < triangles.Length; g++)
            {
                var a = triangles[f]; var b = triangles[g];
                if (CoverageCarryProofGeometry.CoordinateGap(a, b, true) > 0) continue;
                int common = groups[f].Intersect(groups[g]).Count();
                if (common == 2) continue;
                var best = Vector3.Zero; double margin = double.NegativeInfinity;
                foreach (var axis in CoverageCarryProofGeometry.Axes(a, b, true))
                {
                    double value = CoverageCarryProofGeometry.HostMargin(a, b, groups[f], groups[g], axis);
                    if (value > margin) { margin = value; best = axis; }
                    if (value > 0) break;
                }
                planes.Add(new(f, g, -1, best));
            }
        for (int k = 0; k < seed.Commands.Length; k++)
        {
            var c = seed.Commands[k]; var solid = CoverageCarryProofGeometry.Equipment(c);
            for (int f = 0; f < triangles.Length; f++)
            {
                double required = t.Triangles[f].RegionIndex == c.RegionIndex ? c.Clearance * 1.25 : 0;
                for (int g = 0; g < solid.Length; g++)
                {
                    var a = triangles[f]; var b = solid[g];
                    if (CoverageCarryProofGeometry.CoordinateGap(a, b) > required) continue;
                    var best = Vector3.Zero; double margin = double.NegativeInfinity;
                    foreach (var axis in CoverageCarryProofGeometry.Axes(a, b, false))
                    {
                        double value = CoverageCarryProofGeometry.Gap(a, b, axis);
                        if (value > margin) { margin = value; best = axis; }
                        if (value > required) break;
                    }
                    planes.Add(new(f, g, k, best));
                }
            }
        }
        var result = seed with { Planes = planes.ToArray() };
        return result with { DomainDigest = CoverageCarryCellBinding.Domain(result) };
    }
}
