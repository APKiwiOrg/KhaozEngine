using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Scene staging constructs the candidate and its normals only. The one full certification of a scene frame
/// is the late validation against the actual captured context, so a geometry refusal resolves to the current-pose
/// fallback instead of refusing preparation.</summary>
[Collection("AllocSensitive")]
public sealed class CoverageLateCertificationTests
{
    [Fact]
    public void Accepted_scene_frame_certifies_once_at_resolution()
    {
        using var h = new CoverageSceneFixture();
        var work = Witness(h.Conformer.Scratch);
        h.Begin();
        var frame = h.Stage();
        // Pending is acceptance for late resolution, never a certificate.
        Assert.Empty(work);
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        h.Outline(h.HostDraw(frame));
        h.SubmitEquipment();
        h.Render();
        Assert.Equal(CoveragePresentation.Covered, h.Scene.GetCoverageOutcome(frame).Presentation);
        Assert.Equal([CoverageCertificationWork.Full], work);
        h.AssertCanonicalDraws(frame);

        // The stored certificate is exactly an independent late validation of the staged bytes.
        var p = frame.Preparation!;
        var topology = p.Owner.Topology;
        var commands = p.Commands.AsSpan(0, p.CommandCount).ToArray();
        Assert.Equal(h.Expected, p.Candidate);
        Assert.Equal(CoverageCommands.Surface(topology, h.Posed), p.Before);
        var context = Context(h.Scene, p, p.Certificate.RenderStamp.SceneIdentity);
        Assert.True(CoverageCertification.Validate(topology, p.Before, p.Candidate, p.Model, commands, context,
            new CoverageScratch(topology), out var certificate, out var failure), failure.ToString());
        CoverageRenderSeparationTests.Same(certificate, p.Certificate);

        // The public evaluators keep one full certification each, with the same bytes.
        var scratch = new CoverageScratch(topology);
        var evaluator = Witness(scratch);
        var final = new ModelVertex[topology.VertexCount];
        Assert.True(CoverageEvaluator.TryEvaluate(topology, p.Posed, p.Model, commands, context, scratch, final, out failure),
            failure.ToString());
        Assert.Equal(p.Candidate, final);
        Assert.Equal([CoverageCertificationWork.Full], evaluator);
        evaluator.Clear();
        Assert.True(CoverageEvaluator.TryEvaluateGeometry(topology, p.Posed, p.Model, commands, scratch, final, out failure),
            failure.ToString());
        Assert.Equal(p.Candidate, final);
        Assert.Equal([CoverageCertificationWork.Full], evaluator);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Geometry_refusal_stages_pending_and_resolves_the_current_pose_fallback(bool gpu)
    {
        using var f = Intruded(gpu);
        var a = f.Load();
        var b = Intruder(f, .5f);
        using var binding = f.Binding();
        // A partial transition never checks B's solid, so the prior frame is covered and records history.
        Assert.True(binding.TrySetEquipment([a, b], out var failure), failure.ToString());
        var first = Frame(f, binding);
        Assert.Equal(CoveragePresentation.Covered, f.Scene.GetCoverageOutcome(first).Presentation);
        ulong sceneIdentity = first.Preparation!.Certificate.RenderStamp.SceneIdentity;

        // At full weight region r0 is no longer certifiably apart from B's block. Staging still accepts.
        Assert.True(binding.TrySetEquipment([a, b with { TransitionWeight = 1 }], out failure), failure.ToString());
        Begin(f);
        Assert.True(binding.TryPrepare(f.Skeleton, f.Palette, Matrix4x4.Identity, f.Template, out var refused, out failure),
            failure.ToString());
        Assert.Equal(CoveragePresentation.Pending, f.Scene.GetCoverageOutcome(refused).Presentation);
        var descriptors = binding.PreparedEquipmentDraws.ToArray();
        Assert.Equal(4, descriptors.Length);
        Assert.All(descriptors, d => Assert.NotEqual(default, d.Rigid.Coverage));
        f.Submit(refused, descriptors);
        f.Render();

        var p = refused.Preparation!;
        var topology = p.Owner.Topology;
        var commands = p.Commands.AsSpan(0, p.CommandCount).ToArray();
        var outcome = f.Scene.GetCoverageOutcome(refused);
        Assert.Equal(CoveragePresentation.UncoveredFallback, outcome.Presentation);
        // A real finite constructed candidate, refused only by certification.
        Assert.All(p.Candidate, v => Assert.True(float.IsFinite(v.Position.X) && float.IsFinite(v.Position.Y)
            && float.IsFinite(v.Position.Z)));
        Assert.Contains(Enumerable.Range(0, p.Candidate.Length), i => p.Candidate[i].Position != p.Before[i].Position);
        Assert.False(CoverageEvaluator.TryEvaluateGeometry(topology, p.Posed, p.Model, commands, new CoverageScratch(topology),
            new ModelVertex[topology.VertexCount], out var early));
        Assert.Equal((CoverageFailureCode.EquipmentIntersection, b.InstanceId), (early.Code, early.EquipmentInstanceId));
        var context = Context(f.Scene, p, sceneIdentity);
        Assert.False(CoverageCertification.Validate(topology, p.Before, p.Candidate, p.Model, commands, context,
            new CoverageScratch(topology), out var certificate, out var late));
        Assert.Equal(late, outcome.Failure);
        Assert.Equal(default, certificate);
        Assert.Equal((early.Code, early.EquipmentInstanceId, early.CommandId), (late.Code, late.EquipmentInstanceId, late.CommandId));

        // Every pass draws the captured current pose on the ordinary route. Nothing of the candidate is uploaded.
        Assert.Equal(p.Posed, f.Scene.CoverageVerticesForTests(refused).ToArray());
        Assert.Equal(0, f.Scene.CoveredHostPreparationCount);
        Assert.Equal(1, f.Scene.PointShadowedLights);
        Assert.True(f.Scene.CoveragePassesForTests(refused).Length >= 16);
        Assert.DoesNotContain(f.Record.IndexedDraws, d => p.Owner.Indices.Contains(d.IndexBuffer));
        var hosts = new IGpuBuffer[f.Hosts.Length];
        for (int slice = 0; slice < f.Hosts.Length; slice++)
        {
            Assert.Throws<InvalidOperationException>(() => f.Scene.CoverageSliceForTests(refused, slice));
            var route = f.Scene.CoverageFallbackSliceForTests(refused, slice);
            hosts[slice] = route.Indices;
            var draws = f.Record.IndexedDraws.Where(d => ReferenceEquals(d.IndexBuffer, route.Indices)).ToArray();
            Assert.Contains(draws, d => d.Pipeline is FakePipeline { Request: { } request }
                && request.FragmentGlsl == KhaozEngine.Render3D.Internal.ShaderSources.TargetOutlineFullMaskFrag);
            Assert.True(draws.Length >= 4);
            // GPU skinning binds the rest-pose source, deformed by the frozen palette. Native proof covers its output.
            // CPU skinning reskins each ordinary stream, and no upload carries a candidate position.
            if (gpu) Assert.All(draws, d => Assert.True(ReferenceEquals(route.Vertices, d.VertexBuffer) && d.VertexOffset == 0));
        }
        AssertNoCandidateUpload(f.Record, p);
        Assert.NotEmpty(f.Scene.CoverageTransitionForTests.Rectangles);
        byte[]? tokenStream = gpu ? null : OrdinaryStream(f, hosts[0]);
        byte[][]? tokenStreams = gpu ? null : HostStreams(f, hosts);

        // The refused candidate never entered history. The next covered frame seeds its own current surface.
        Assert.True(binding.TrySetEquipment([a, b with { TransitionWeight = .25f }], out failure), failure.ToString());
        var returned = Frame(f, binding);
        Assert.Equal(CoveragePresentation.Covered, f.Scene.GetCoverageOutcome(returned).Presentation);
        Assert.Equal(f.Scene.CoverageVerticesForTests(returned).ToArray().Select(v => v.Position), Previous(f));

        // Without a token the same current pose uploads the same ordinary stream. Visibility may differ, not bytes.
        Begin(f);
        f.Submit(default, []);
        f.Render();
        if (tokenStream is not null && tokenStreams is not null)
        {
            Assert.Equal(tokenStream, OrdinaryStream(f, hosts[0]));
            Assert.Contains(tokenStream, tokenStreams.Select(s => s[sizeof(uint)..]));
            // Every host stream, the reskinned ordinary outline included, carries the same uploaded bytes.
            Assert.Equal(tokenStreams, HostStreams(f, hosts));
        }
        else
            for (int slice = 0; slice < hosts.Length; slice++)
                Assert.Contains(f.Record.IndexedDraws, d => ReferenceEquals(d.IndexBuffer, hosts[slice]) && d.VertexOffset == 0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Warm_accepted_and_late_refused_binding_frames_allocate_zero(bool gpu, bool refuse)
    {
        using var f = Intruded(gpu);
        // Stage recording is test instrumentation. Real staging and validation still run through the adapter.
        f.Adapter.RecordStages = false;
        var a = f.Load();
        var b = Intruder(f, refuse ? 1 : .5f);
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([a, b], out var failure), failure.ToString());
        var expected = refuse ? CoveragePresentation.UncoveredFallback : CoveragePresentation.Covered;
        // Two warm frames fill both history banks and the shadow signature pair.
        for (int i = 0; i < 2; i++) Assert.Equal(expected, f.Scene.GetCoverageOutcome(Frame(f, binding)).Presentation);

        Begin(f);
        var template = f.Template;
        long start = GC.GetAllocatedBytesForCurrentThread();
        bool accepted = binding.TryPrepare(f.Skeleton, f.Palette, Matrix4x4.Identity, template, out var frame, out failure);
        long prepared = GC.GetAllocatedBytesForCurrentThread();
        if (accepted) f.Submit(frame, binding.PreparedEquipmentDraws.Span);
        long submitted = GC.GetAllocatedBytesForCurrentThread();
        f.Scene.PrepareFrame(32, 24);
        long reserve = GC.GetAllocatedBytesForCurrentThread() - submitted;
        long resolutionStart = 0, resolution = 0, surfaceStart = 0, surface = 0;
        f.Scene.CoverageAllocationObserverForTests = entering =>
        {
            if (entering) resolutionStart = GC.GetAllocatedBytesForCurrentThread();
            else resolution += GC.GetAllocatedBytesForCurrentThread() - resolutionStart;
        };
        f.Scene.CoverageSurfaceAllocationObserverForTests = entering =>
        {
            if (entering) surfaceStart = GC.GetAllocatedBytesForCurrentThread();
            else surface += GC.GetAllocatedBytesForCurrentThread() - surfaceStart;
        };
        f.Render(prepare: false);
        f.Scene.CoverageAllocationObserverForTests = null;
        f.Scene.CoverageSurfaceAllocationObserverForTests = null;

        Assert.True(accepted, failure.ToString());
        var outcome = f.Scene.GetCoverageOutcome(frame);
        Assert.Equal(expected, outcome.Presentation);
        if (refuse) Assert.Equal(CoverageFailureCode.EquipmentIntersection, outcome.Failure.Code);
        Assert.Equal(0, prepared - start);
        Assert.Equal(0, submitted - prepared);
        Assert.Equal(0, reserve);
        Assert.Equal(0, resolution);
        Assert.Equal(0, surface);
    }

    // Temporal resolve, cascades, one selected point light and outlines are all active on the two-region host.
    static CoverageAttachmentFixture Intruded(bool gpu)
    {
        var f = new CoverageAttachmentFixture(secondRegion: true);
        f.Scene.UseGpuSkinning = gpu;
        f.Scene.Post.Pixelated = false;
        f.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        f.Scene.Post.Quality.Shadows.PointShadows.FaceResolution = 64;
        f.Scene.Post.Quality.Shadows.PointShadows.MaxShadowedLights = 1;
        return f;
    }

    static void Begin(CoverageAttachmentFixture f)
    {
        f.Scene.Begin();
        f.Scene.AddLight(new(0, 0, 4), Color.White, 25, 1, LightShadow.Dynamic);
    }

    static SkinnedCoverageFrame Frame(CoverageAttachmentFixture f, HostCoverageBinding binding)
    {
        Begin(f);
        Assert.True(binding.TryPrepare(f.Skeleton, f.Palette, Matrix4x4.Identity, f.Template, out var frame, out var failure),
            failure.ToString());
        f.Submit(frame, binding.PreparedEquipmentDraws.Span);
        f.Render();
        return frame;
    }

    /// <summary>Equipment serving region r1 beside a second plate. Its solid also carries a separate closed block
    /// through region r0's rim cell. Construction reads only the support sheet, so the block changes no candidate
    /// byte. At full weight r0's faces cross it, an other-region equipment intersection only certification sees.</summary>
    static CoveredAttachmentInstance Intruder(CoverageAttachmentFixture f, float weight)
    {
        var mesh = f.Source.Mesh("intruder.glb", IntruderGlb());
        var definition = new AttachmentSetDefinition([new RigidAttachmentPartDefinition(1, mesh, "root", Matrix4x4.Identity)]);
        string hash = f.Source.Add("intruder.json", AttachmentSetJson.Write(definition));
        var loaded = f.Cache.Get("intruder.json", hash);
        string companion = f.Source.Add("intruder-coverage.json", EquipmentCoverageJson.Write(new EquipmentCoverageDefinition(hash,
            f.MapHash, [new(1, 1, "r1", Matrix4x4.CreateTranslation(4, 0, 0), [new(0, 0, 0, 4)], .4f, -2, 2, .02f, .8f)])));
        return new(5, loaded.Bind(f.Map(f.Skeleton)), f.Cache.GetCoverage(loaded, "intruder-coverage.json", companion), weight);
    }

    static byte[] IntruderGlb()
    {
        var data = CoverageFixture.CurvedPanel();
        var mesh = new MeshBuilder<VertexPositionNormal, VertexEmpty>("intruder");
        var inward = mesh.UsePrimitive(new MaterialBuilder("inward"));
        var rest = mesh.UsePrimitive(new MaterialBuilder("solid"));
        for (int t = 0; t < data.RigidIndices.Length / 3; t++)
            (t < 4 ? inward : rest).AddTriangle(V(data.RigidPositions, data.RigidIndices, t, 0, new(4, 0, 0)),
                V(data.RigidPositions, data.RigidIndices, t, 1, new(4, 0, 0)), V(data.RigidPositions, data.RigidIndices, t, 2, new(4, 0, 0)));
        // Wound like the plate, with normals into the solid. It spans z -.3 to -.1 around r0's rim faces at z -.2.
        var block = CoverageCommands.Gabled(-.3f, 0, -.1f, .1f, .1f, outward: false);
        for (int t = 0; t < block.Indices.Length / 3; t++)
            rest.AddTriangle(V(block.Positions, block.Indices, t, 0, new(-1, -1, 0)), V(block.Positions, block.Indices, t, 1, new(-1, -1, 0)),
                V(block.Positions, block.Indices, t, 2, new(-1, -1, 0)));
        var scene = new SceneBuilder();
        scene.AddRigidMesh(mesh, Matrix4x4.Identity);
        string path = Path.Combine(Path.GetTempPath(), $"coverage-intruder-{Guid.NewGuid():N}.glb");
        try { scene.ToGltf2().SaveGLB(path); return File.ReadAllBytes(path); }
        finally { File.Delete(path); }

        static VertexBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty> V(Vector3[] positions, uint[] indices, int t, int corner,
            Vector3 offset) => new(new VertexPositionNormal(positions[indices[t * 3 + corner]] + offset, Vector3.UnitZ));
    }

    static CoverageRenderContext Context(Scene3D scene, CoveragePreparation p, ulong sceneIdentity) =>
        new(p.Owner.Topology, new(sceneIdentity, p.Generation, p.View, p.Actor, p.Id), scene.CurrentFrameView.RenderOrigin,
            CollectionsMarshal.AsSpan(p.Passes));

    static List<CoverageCertificationWork> Witness(CoverageScratch scratch)
    {
        var work = new List<CoverageCertificationWork>();
        scratch.CertificationWitnessForTests = (kind, _) => work.Add(kind);
        return work;
    }

    static void AssertNoCandidateUpload(RecordingGpuCommandList record, CoveragePreparation p)
    {
        for (int i = 0; i < p.Candidate.Length; i++)
        {
            if (p.Candidate[i].Position == p.Before[i].Position) continue;
            Vector3 position = p.Candidate[i].Position;
            byte[] bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref position, 1)).ToArray();
            Assert.DoesNotContain(record.Uploads, u => u.Data is { } data && data.AsSpan().IndexOf(bytes) >= 0);
        }
    }

    static byte[] OrdinaryStream(CoverageAttachmentFixture f, IGpuBuffer hostIndices)
    {
        var draw = f.Record.IndexedDraws.First(d => ReferenceEquals(d.IndexBuffer, hostIndices));
        return Assert.Single(f.Record.Uploads, u => ReferenceEquals(u.Buffer, draw.VertexBuffer)).Data!;
    }

    // The uploads, with their offsets, into each vertex buffer the host slices draw from, in first-draw order.
    static byte[][] HostStreams(CoverageAttachmentFixture f, IGpuBuffer[] hostIndices)
    {
        var buffers = new List<IGpuBuffer>();
        foreach (var d in f.Record.IndexedDraws)
            if (hostIndices.Any(h => ReferenceEquals(h, d.IndexBuffer)) && d.VertexBuffer is { } vertices
                && !buffers.Any(b => ReferenceEquals(b, vertices)))
                buffers.Add(vertices);
        return [.. buffers.SelectMany(b => f.Record.Uploads.Where(u => ReferenceEquals(u.Buffer, b))
            .Select(u => BitConverter.GetBytes(u.Offset).Concat(u.Data ?? []).ToArray()))];
    }

    static Vector3[] Previous(CoverageAttachmentFixture f) => MemoryMarshal.Cast<byte, Vector3>(Assert.Single(
        f.Record.Uploads, upload => ReferenceEquals(upload.Buffer, f.Scene.CoverageMotionReserveForTests.Buffer)).Data).ToArray();
}
