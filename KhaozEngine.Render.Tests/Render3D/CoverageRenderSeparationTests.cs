using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Late render certification runs one full Certify of the exact surface, then rechecks only the separation the
/// captured render bound can change. A reference built from the existing internals runs the former second full
/// Certify, and every result field must match it exactly.</summary>
[Collection("AllocSensitive")]
public sealed class CoverageRenderSeparationTests
{
    internal const float Near = 1f / 1024, Far = 1f / 64;
    static CoverageRenderStamp Stamp => new(1, 2, 3, MotionKey.From(4), 5);

    [Theory]
    [InlineData(1f, false, 1u, 4)]
    [InlineData(1f, true, 1u, 4)]
    // A partial transition is not full weight, so its slot is never checked and the later full command reports.
    [InlineData(.5f, false, 2u, 2)]
    [InlineData(.5f, true, 2u, 2)]
    public void Raised_render_bound_refuses_the_first_full_slot_with_its_exact_count(float transition, bool reversed,
        uint command, int count)
    {
        var scene = Gap(Near, transition: transition);
        var commands = Order(scene.Commands, reversed);
        var context = scene.Context(host: .001);
        // The construction threshold certifies this exact surface, so only the raised render bound can refuse it.
        Assert.True(CoverageCertification.ValidateGeometry(scene.Topology, scene.Posed, scene.Posed, Matrix4x4.Identity, commands,
            new CoverageScratch(scene.Topology), out var geometry), geometry.ToString());

        var scratch = new CoverageScratch(scene.Topology);
        var work = Witness(scratch);
        Assert.False(CoverageCertification.Validate(scene.Topology, scene.Posed, scene.Posed, Matrix4x4.Identity, commands, context,
            scratch, out var certificate, out var failure));
        Assert.Equal(new CoverageFailure(CoverageFailureCode.EquipmentIntersection, scene.Topology.HostIdentity, command, command, count, 0),
            failure);
        Assert.Equal(default, certificate);
        // Both full commands' bounds cross the gap, so slot order alone selects the reported command.
        for (int slot = 0; slot < 2; slot++)
            Assert.InRange((double)Near, scratch.Frames[slot].ConstructionErrorBound, scratch.Frames[slot].RenderErrorBound);
        Assert.Equal(Work((int)command - 1), work);
        Assert.False(Reference(scene.Topology, scene.Posed, scene.Posed, commands, context, out var expected, out var reference));
        Assert.Equal(reference, failure);
        Assert.Equal(expected, certificate);

        var evaluator = new CoverageScratch(scene.Topology);
        work = Witness(evaluator);
        var sentinel = new ModelVertex(new(7, 7, 7), Vector3.UnitY, Vector4.One);
        var final = Enumerable.Repeat(sentinel, scene.Topology.VertexCount).ToArray();
        Assert.False(CoverageEvaluator.TryEvaluate(scene.Topology, scene.Posed, Matrix4x4.Identity, commands, context, evaluator, final,
            out certificate, out failure));
        Assert.Equal(reference, failure);
        Assert.Equal(default, certificate);
        Assert.All(final, v => Assert.Equal(sentinel, v));
        Assert.Equal(Work((int)command - 1), work);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepted_render_recheck_keeps_the_first_certificate_exactly(bool reversed)
    {
        var scene = Gap(Far);
        var commands = Order(scene.Commands, reversed);
        var context = scene.Context(host: .001);
        var scratch = new CoverageScratch(scene.Topology);
        var work = Witness(scratch);
        Assert.True(CoverageCertification.Validate(scene.Topology, scene.Posed, scene.Posed, Matrix4x4.Identity, commands, context,
            scratch, out var certificate, out var failure), failure.ToString());
        Assert.Equal(Work(0, 1), work);
        Assert.True(Reference(scene.Topology, scene.Posed, scene.Posed, commands, context, out var expected, out var reference),
            reference.ToString());
        Same(expected, certificate);
        Assert.True(certificate.FullCoreClearance);

        var evaluator = new CoverageScratch(scene.Topology);
        work = Witness(evaluator);
        var spans = new List<string>();
        evaluator.StageObserver = (span, entering) => spans.Add($"{span}:{(entering ? "in" : "out")}");
        var final = new ModelVertex[scene.Topology.VertexCount];
        Assert.True(CoverageEvaluator.TryEvaluate(scene.Topology, scene.Posed, Matrix4x4.Identity, commands, context, evaluator, final,
            out var evaluated, out failure), failure.ToString());
        Same(expected, evaluated);
        Assert.Equal(scene.Posed, final);
        Assert.Equal(Work(0, 1), work);
        // The full pass and the render recheck are each geometry certification inside the evaluator, never nested.
        Assert.Equal(new[] { "GeometryCertification:in", "GeometryCertification:out", "GeometryCertification:in", "GeometryCertification:out" },
            spans);
        spans.Clear();
        Assert.True(CoverageEvaluator.TryEvaluateGeometry(scene.Topology, scene.Posed, Matrix4x4.Identity, commands, evaluator, final,
            out failure), failure.ToString());
        Assert.Equal(new[] { "GeometryCertification:in", "GeometryCertification:out" }, spans);
    }

    [Fact]
    public void Unraised_render_bounds_skip_only_their_slots()
    {
        // A large recession budget widens construction rounding past the render error of an unperturbed pass.
        var scene = Gap(Far, recession1: 1000, recession2: 1000);
        Check(scene, scene.Context(host: 0));
        var mixed = Gap(Far, recession1: 1000);
        Check(mixed, mixed.Context(host: 0, second: .001), 1);

        static void Check(Scene scene, in CoverageRenderContext context, params int[] rechecked)
        {
            var scratch = new CoverageScratch(scene.Topology);
            var work = Witness(scratch);
            Assert.True(CoverageCertification.Validate(scene.Topology, scene.Posed, scene.Posed, Matrix4x4.Identity, scene.Commands,
                context, scratch, out var certificate, out var failure), failure.ToString());
            for (int slot = 0; slot < 2; slot++)
                Assert.Equal(rechecked.Contains(slot),
                    scratch.Frames[slot].RenderErrorBound > scratch.Frames[slot].ConstructionErrorBound);
            Assert.Equal(Work(rechecked), work);
            Assert.True(Reference(scene.Topology, scene.Posed, scene.Posed, scene.Commands, context, out var expected, out _));
            Same(expected, certificate);
        }
    }

    [Theory]
    [InlineData(1, false, 1f, 0)]
    [InlineData(1, false, 1f, 1e-4)]
    [InlineData(2, false, 1f, 1e-4)]
    [InlineData(2, true, 1f, 1e-4)]
    [InlineData(2, false, .5f, 1e-4)]
    [InlineData(2, false, 1f, .01)]
    public void Conformed_panels_match_the_full_reference(int regions, bool reversed, float transition, double host)
    {
        var fixture = CoverageFixture.CurvedPanel(level: 2, secondRegion: regions == 2);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var commands = Order(CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity, transition),
            reversed);
        var final = new ModelVertex[topology.VertexCount];
        Assert.True(CoverageEvaluator.TryEvaluateGeometry(topology, fixture.Posed, Matrix4x4.Identity, commands, new CoverageScratch(topology),
            final, out var failure), failure.ToString());
        var before = CoverageCommands.Surface(topology, fixture.Posed);
        var passes = new List<CoverageRenderPass> { new(0, 0, Matrix4x4.Identity, Matrix4x4.Identity, Min(final), Max(final), host) };
        var plate = fixture.RigidPositions;
        foreach (var command in commands)
            passes.Add(new(command.Id, command.EquipmentInstanceId, command.EquipmentWorld, Matrix4x4.Identity,
                plate.Aggregate(Vector3.Min), plate.Aggregate(Vector3.Max), 0));
        var context = new CoverageRenderContext(topology, Stamp, Vector3.Zero, passes.ToArray());

        var scratch = new CoverageScratch(topology);
        var work = Witness(scratch);
        bool accepted = CoverageCertification.Validate(topology, before, final, Matrix4x4.Identity, commands, context, scratch,
            out var certificate, out failure);
        Assert.Equal(Reference(topology, before, final, commands, context, out var expected, out var reference), accepted);
        Assert.Equal(reference, failure);
        Same(expected, certificate);
        Assert.Equal(1, work.Count(w => w.StartsWith($"{CoverageCertificationWork.Full}:", StringComparison.Ordinal)));
        if (regions == 1 || transition < 1) Assert.Equal(Work(), work);
        // An unabsorbed render error refuses inside TryBound, before any recheck.
        if (host == .01) Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Code);
        else Assert.True(accepted, failure.ToString());

        var evaluator = new CoverageScratch(topology);
        work = Witness(evaluator);
        var evaluated = new ModelVertex[topology.VertexCount];
        Assert.Equal(accepted, CoverageEvaluator.TryEvaluate(topology, fixture.Posed, Matrix4x4.Identity, commands, context, evaluator,
            evaluated, out var evaluatedCertificate, out failure));
        Assert.Equal(reference, failure);
        Same(expected, evaluatedCertificate);
        if (accepted) Assert.Equal(final, evaluated);
        Assert.Equal(1, work.Count(w => w.StartsWith($"{CoverageCertificationWork.Full}:", StringComparison.Ordinal)));
    }

    [Fact]
    public void Zero_commands_certify_once()
    {
        var scene = Gap(Far);
        var context = new CoverageRenderContext(scene.Topology, Stamp, Vector3.Zero,
            [new(0, 0, Matrix4x4.Identity, Matrix4x4.Identity, Min(scene.Posed), Max(scene.Posed), .001)]);
        var scratch = new CoverageScratch(scene.Topology);
        var work = Witness(scratch);
        Assert.True(CoverageCertification.Validate(scene.Topology, scene.Posed, scene.Posed, Matrix4x4.Identity, [], context, scratch,
            out var certificate, out var failure), failure.ToString());
        Assert.Equal(Work(), work);
        Assert.True(Reference(scene.Topology, scene.Posed, scene.Posed, [], context, out var expected, out _));
        Same(expected, certificate);
    }

    [Fact]
    public void Warm_render_recheck_allocates_nothing()
    {
        var scene = Gap(Far);
        var context = scene.Context(host: .001);
        var scratch = new CoverageScratch(scene.Topology);
        var final = new ModelVertex[scene.Topology.VertexCount];
        Assert.True(CoverageCertification.Validate(scene.Topology, scene.Posed, scene.Posed, Matrix4x4.Identity, scene.Commands, context,
            scratch, out var failure), failure.ToString());
        Assert.True(CoverageEvaluator.TryEvaluate(scene.Topology, scene.Posed, Matrix4x4.Identity, scene.Commands, context, scratch, final,
            out failure), failure.ToString());
        long start = GC.GetAllocatedBytesForCurrentThread();
        bool validated = CoverageCertification.Validate(scene.Topology, scene.Posed, scene.Posed, Matrix4x4.Identity, scene.Commands,
            context, scratch, out _);
        bool evaluated = CoverageEvaluator.TryEvaluate(scene.Topology, scene.Posed, Matrix4x4.Identity, scene.Commands, context, scratch,
            final, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(validated && evaluated);
        Assert.Equal(0, allocated);
    }

    // The former late sequence with its second full Certify, assembled only from existing internals on its own scratch.
    static bool Reference(CoverageRefinement topology, ModelVertex[] before, ModelVertex[] after, CoverageCommandPose[] commands,
        in CoverageRenderContext context, out CoverageCertificate certificate, out CoverageFailure failure)
    {
        var scratch = new CoverageScratch(topology);
        certificate = default;
        double magnitude = CoverageCertification.Magnitude(before, after);
        Assert.True(double.IsFinite(magnitude));
        if (!CoverageFrames.TryPrepare(topology, Matrix4x4.Identity, commands, scratch, magnitude, out failure)
            || !CoverageCertification.Certify(topology, before, after, commands, scratch, out var geometry, out failure)
            || !CoverageRenderError.TryBound(topology, after, Matrix4x4.Identity, commands, scratch, context, out double renderError, out failure)
            || scratch.ActiveCommands > 1 && !CoverageCertification.Certify(topology, before, after, commands, scratch, out geometry, out failure))
            return false;
        certificate = new(geometry.FullCoreClearance, geometry.MinimumCoreClearance, renderError, geometry.DisplacedVertices, context.Stamp);
        return true;
    }

    internal static void Same(in CoverageCertificate expected, in CoverageCertificate actual)
    {
        Assert.Equal(expected, actual);
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.MinimumCoreClearance), BitConverter.DoubleToInt64Bits(actual.MinimumCoreClearance));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.WorldErrorBound), BitConverter.DoubleToInt64Bits(actual.WorldErrorBound));
    }

    static List<string> Witness(CoverageScratch scratch)
    {
        var work = new List<string>();
        scratch.CertificationWitnessForTests = (kind, slot) => work.Add($"{kind}:{slot}");
        return work;
    }

    static string[] Work(params int[] rechecked) =>
        [$"{CoverageCertificationWork.Full}:-1", .. rechecked.Select(s => $"{CoverageCertificationWork.RenderSeparation}:{s}")];

    static CoverageCommandPose[] Order(CoverageCommandPose[] commands, bool reversed) => reversed ? commands.Reverse().ToArray() : commands;

    static Vector3 Min(ModelVertex[] vertices) => vertices.Select(v => v.Position).Aggregate(Vector3.Min);
    static Vector3 Max(ModelVertex[] vertices) => vertices.Select(v => v.Position).Aggregate(Vector3.Max);

    /// <summary>Two closed boxes, x in [-.5, .5] and [1.5, 2.5], z in [0, 1]. Each command's flat collar patch lies at z .5
    /// in the gap between them, `gap` from the other command's box and three quarters of a unit from its own. Zero
    /// strengths keep every vertex in place, so certification alone decides. Region r0 has two cells beside box two,
    /// region r1 four cells, two columns, beside box one, and their triangles follow in that order.</summary>
    internal static Scene Gap(float gap, float recession1 = .8f, float recession2 = .8f, float transition = 1)
    {
        var positions = new List<Vector3>();
        var triangles = new List<CoverageSourceTriangle>();
        Patch(1.25f - gap, .125f, 3, 2);
        int split = triangles.Count;
        Patch(.5f + gap, -.375f, 3, 3);
        var skin = positions.Select(p => new SkinnedVertex { Position = p, Normal = Vector3.UnitZ, Color = Vector4.One, BoneWeights = new(1, 0, 0, 0) })
            .ToArray();
        var source = new CoverageSourceTopology("synthetic-gap", CoverageFixture.Hash, positions.ToArray(),
            positions.Select((_, i) => new CoverageSourceVertex(0, 0, i, -1, 0, i)), triangles, skin);
        int first = 6;
        var regions = new[]
        {
            new HostCoverageRegion("r0", [new(new(0, 0, 0, split), CoverageTriangleRole.Collar)],
                Enumerable.Range(0, first).Select(i => new CoverageSurfaceStrength(i, 0))),
            new HostCoverageRegion("r1", [new(new(0, 0, split, triangles.Count - split), CoverageTriangleRole.Collar)],
                Enumerable.Range(first, positions.Count - first).Select(i => new CoverageSurfaceStrength(i, 0))),
        };
        var map = new HostCoverageMap(CoverageFixture.Hash, positions.Select((_, i) => new CoverageTopologyEntry(0, 0, i, i)), regions, [],
            new("synthetic", "r0"));
        var topology = CoverageRefinement.Build(source, map);
        var box = CoverageCommands.Gabled(0, 0, 1, .5f, .5f, outward: true);
        var support = CoverageSupportSurface.Compile(CoverageCommands.Solid("synthetic-box", box.Positions, box.Indices), [new(0, 0, 0, 4)],
            Matrix4x4.Identity);
        var second = Matrix4x4.CreateTranslation(2, 0, 0);
        CoverageCommandPose[] commands =
        [
            new(1, 1, topology, 0, support, Matrix4x4.Identity, Matrix4x4.Identity, -2, 2, .4f, .02f, recession1, transition),
            new(2, 2, topology, 1, support, second, second, -2, 2, .4f, .02f, recession2),
        ];
        var posed = skin.Select(v => SkinningMath.SkinVertex(v, [Matrix4x4.Identity])).ToArray();
        Assert.Equal(topology.VertexCount, posed.Length);
        return new(topology, posed, commands, box.Positions);

        void Patch(float x, float y, int columns, int rows)
        {
            int start = positions.Count;
            for (int j = 0; j < rows; j++)
                for (int i = 0; i < columns; i++) positions.Add(new(x + i * .125f, y + j * .125f, .5f));
            for (int j = 0; j + 1 < rows; j++)
                for (int i = 0; i + 1 < columns; i++)
                {
                    int a = start + j * columns + i;
                    triangles.Add(new(0, 0, triangles.Count, -1, 0, a, a + 1, a + columns + 1));
                    triangles.Add(new(0, 0, triangles.Count, -1, 0, a, a + columns + 1, a + columns));
                }
        }
    }

    internal sealed record Scene(CoverageRefinement Topology, ModelVertex[] Posed, CoverageCommandPose[] Commands, Vector3[] Box)
    {
        // Identity view projection keeps the captured error at the supplied perturbation plus float rounding.
        internal CoverageRenderContext Context(double host, double first = 0, double second = 0)
        {
            Vector3 low = Box.Aggregate(Vector3.Min), high = Box.Aggregate(Vector3.Max);
            return new(Topology, Stamp, Vector3.Zero, new CoverageRenderPass[]
            {
                new(0, 0, Matrix4x4.Identity, Matrix4x4.Identity, Min(Posed), Max(Posed), host),
                new(1, 1, Commands[0].EquipmentWorld, Matrix4x4.Identity, low, high, first),
                new(2, 2, Commands[1].EquipmentWorld, Matrix4x4.Identity, low, high, second),
            });
        }
    }
}
