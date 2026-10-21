#if COVERAGE_FILTER_STUDY
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu.FilterStudy;

[CollectionDefinition("CoverageFilterStudy", DisableParallelization = true)]
public sealed class CoverageFilterStudyCollection;

[Collection("CoverageFilterStudy")]
public sealed class CoverageFilterStudyTests
{
    [Fact]
    public void Certificates_refuse_wrong_finite_seed_words()
    {
        float negativeZero = BitConverter.UInt32BitsToSingle(0x80000000u);
        foreach (float a in new[] { 0f, negativeZero, 1f, .1f, 1e-15f, float.MaxValue })
            foreach (float b in new[] { 1f, -1f, 3f, .25f })
                for (int mode = 0; mode < 3; mode++)
                {
                    float q = a / b, r = MathF.Sqrt(a);
                    Assert.False(StudyFilters.Scalar(a, b, -q, false, mode));
                    Assert.False(StudyFilters.Scalar(a, b, MathF.BitIncrement(q), false, mode));
                    Assert.False(StudyFilters.Scalar(a, b, MathF.BitDecrement(q), false, mode));
                    Assert.False(StudyFilters.Scalar(a, 0, -r, true, mode));
                    Assert.False(StudyFilters.Scalar(a, 0, MathF.BitIncrement(r), true, mode));
                    Assert.False(StudyFilters.Scalar(a, 0, MathF.BitDecrement(r), true, mode));
                    Assert.False(StudyFilters.Scalar(a, 0, 1, false, mode));
                    Assert.False(StudyFilters.Scalar(-1, 0, 1, true, mode));
                }
    }

    [Fact]
    public void Measure_public_corpus_once_when_explicitly_enabled()
    {
        if (Environment.GetEnvironmentVariable("KE_COVERAGE_FILTER_STUDY") != "1") return;
        string destination = Environment.GetEnvironmentVariable("KE_COVERAGE_FILTER_STUDY_OUTPUT")
            ?? throw new InvalidOperationException("An explicit scratch output path is required.");
        Assert.Empty(StudyContext.Metrics);
        var outcomes = new List<object>();
        foreach (var c in CoverageReferenceCases.All()) Run(c, "frozen72", 1, outcomes);

        // Both actual profiled poses and every actual 32-actor world placement, with nine-frame weights.
        var f0 = CoverageReferenceCases.Get("cost-typical-l2-lifted-w1");
        for (int actor = 0; actor < 32; actor++)
            for (int pose = 0; pose < 2; pose++)
            {
                Matrix4x4 world = Matrix4x4.CreateTranslation((actor % 8 - 3.5f) * 3, (actor / 8 - 1.5f) * 1.5f, 0);
                var palette = new Matrix4x4[2]; CoverageCostGeometry.Pose(pose, palette);
                Run(Pose(f0, palette, world, $"actor{actor}-pose{pose}"), "F0-T4-32", pose == actor % 2 ? 5 : 4, outcomes);
            }
        for (int pose = 0; pose < 2; pose++)
        {
            var palette = new Matrix4x4[2]; CoverageCostGeometry.Pose(pose, palette);
            Run(Pose(f0, palette, Matrix4x4.Identity, $"one-pose{pose}"), "F0-T4-1", pose == 0 ? 5 : 4, outcomes);
            Run(Pose(f0, palette, Matrix4x4.Identity, $"alternating-pose{pose}"), "F0-alternating", 180, outcomes);
            foreach (int hz in new[] { 30, 60 })
                for (int frame = 0; frame < 4 * hz; frame++)
                {
                    var moved = Trace(palette, hz, frame);
                    Run(Pose(f0, moved, Matrix4x4.Identity, $"trace-{pose}-{hz}-{frame}"), $"F0-trace-{hz}", 1, outcomes);
                }
        }
        StudyContext.EndFace();
        File.WriteAllText(destination, JsonSerializer.Serialize(new
        {
            Base = "f1a298ef107d691d9ea0341ece6f3ffa29d4fc6a",
            TraceSource = "575472e3a57145fcac3409befd61d20922144ffb/CoverageCarryLifetime.cs",
            Modes = StudyFilters.Modes,
            Outcomes = outcomes,
            StudyContext.CachedEdges,
            Metrics = StudyContext.Metrics.OrderBy(p => p.Key).ToDictionary(),
            Queues = StudyContext.Queues.OrderBy(p => p.Key).ToDictionary(),
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    static void Run(CoverageReferenceCase c, string corpus, int weight, List<object> outcomes)
    {
        StudyContext.EndFace(); StudyContext.Corpus = corpus; StudyContext.Weight = weight;
        var original = new CoverageReferenceScratch(c.Topology);
        bool prepared = CoverageReferenceFrames.TryPrepare(c.Topology, c.Model, c.Commands, original, out var failure)
            && CoverageReferenceConstructor.TryInterpolate(c.Topology, c.Posed, original, out failure)
            && CoverageReferenceConstructor.TryConformGroups(c.Topology, c.Commands, original, out failure);
        if (!prepared) { outcomes.Add(new { c.Id, Corpus = corpus, Weight = weight, Prepared = false, Failure = failure.ToString() }); return; }
        var observed = Copy(c, original);
        bool expected = CoverageReferenceCorrection.TryApply(c.Topology, c.Commands, original, out var expectedFailure);
        bool actual = StudyCorrection.TryApply(c.Topology, c.Commands, observed, out var actualFailure);
        StudyContext.EndFace();
        Assert.Equal(expected, actual); Assert.Equal(expectedFailure, actualFailure);
        Assert.Equal(original.FaceGuards, observed.FaceGuards);
        Assert.Equal(Words(original.FaceCorrections), Words(observed.FaceCorrections));
        Assert.Equal(Words(original.FullRecessions), Words(observed.FullRecessions));
        Assert.Equal(Words(original.GroupPositions.SelectMany(v => new[] { v.X, v.Y, v.Z })), Words(observed.GroupPositions.SelectMany(v => new[] { v.X, v.Y, v.Z })));
        Assert.Equal(original.After.Select(CoverageReferenceBits.Vertex), observed.After.Select(CoverageReferenceBits.Vertex));
        if (actual)
        {
            CoverageReferenceCorrection.ApplyTransition(c.Topology, c.Commands, original);
            StudyCorrection.ApplyTransition(c.Topology, c.Commands, observed);
            bool expectedNormals = CoverageReferenceNormals.TryUpdate(c.Topology, original, out var expectedNormalFailure);
            bool actualNormals = StudyNormals.TryUpdate(c.Topology, observed, out var actualNormalFailure);
            Assert.Equal(expectedNormals, actualNormals); Assert.Equal(expectedNormalFailure, actualNormalFailure);
            Assert.Equal(original.Published.Select(CoverageReferenceBits.Vertex), observed.Published.Select(CoverageReferenceBits.Vertex));
        }
        outcomes.Add(new { c.Id, Corpus = corpus, Weight = weight, Prepared = true, Accepted = actual, Failure = actualFailure.ToString() });
    }

    static uint[] Words(IEnumerable<float> a) => a.Select(BitConverter.SingleToUInt32Bits).ToArray();
    static CoverageReferenceScratch Copy(CoverageReferenceCase c, CoverageReferenceScratch source)
    {
        var target = new CoverageReferenceScratch(c.Topology) { ActiveCommands = source.ActiveCommands };
        source.Before.CopyTo(target.Before, 0); source.After.CopyTo(target.After, 0);
        source.Frames.CopyTo(target.Frames, 0); source.Order.CopyTo(target.Order, 0);
        source.RegionCommands.CopyTo(target.RegionCommands, 0); source.GroupPositions.CopyTo(target.GroupPositions, 0);
        source.ActiveGroupRepresentatives.CopyTo(target.ActiveGroupRepresentatives, 0); source.FullRecessions.CopyTo(target.FullRecessions, 0);
        return target;
    }

    static CoverageReferenceCase Pose(CoverageReferenceCase c, Matrix4x4[] palette, Matrix4x4 model, string id)
    {
        var posed = new ModelVertex[c.Topology.SourceVertexCount];
        for (int i = 0; i < posed.Length; i++) posed[i] = SkinningMath.SkinVertex(c.Topology.Source.SkinVertices[i], palette);
        Matrix4x4 equipment = palette[0] * model;
        var commands = c.Commands.Select(x => x with { EquipmentWorld = equipment, CoverageWorld = x.Support.CoverageLocal * equipment }).ToArray();
        return c with { Id = id, Palette = palette, Posed = posed, Model = model, Commands = commands };
    }

    // Verbatim scalar sampler from the recorded public F0 replay, without its admission machinery.
    static Matrix4x4[] Trace(Matrix4x4[] center, int hz, int frame)
    {
        double time = frame / (double)hz;
        double jumped = time + (time >= .75 ? .1 : 0) + (time >= 1.75 ? .25 : 0) + (time >= 2.75 ? 1 : 0);
        double phase = (time < 2 ? jumped : 4 - jumped) % 4;
        if (phase < 0) phase += 4;
        double excursion = phase < 1 ? .000001 * Math.Sin(phase * Math.Tau)
            : phase < 2 ? .004 * (phase - 1) : phase < 3 ? .004 * (3 - phase)
            : .00005 * Math.Sin(4 * Math.Tau * (phase - 3));
        var palette = (Matrix4x4[])center.Clone();
        int bone = 1;
        palette[bone].M41 += (float)(.000001 * Math.Sin(time * 2.3 + bone));
        palette[bone].M42 += (float)(.000001 * Math.Cos(time * 1.7 + bone));
        palette[bone].M43 += (float)(excursion + .000001 * Math.Sin(time * 3.1 + bone));
        return palette;
    }
}
#endif
