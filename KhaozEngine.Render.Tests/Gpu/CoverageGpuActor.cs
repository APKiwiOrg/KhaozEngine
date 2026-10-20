using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using KhaozEngine.Primitives;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

internal sealed class CoverageGpuActor : IDisposable
{
    readonly Scene3D scene;
    readonly CoverageFixture data;
    readonly SkinnedHostConformer conformer;
    readonly SkinnedMeshHandle host, reference, blendedReference, removed;
    readonly MeshHandle rigid;
    readonly CoverageSupportSurface support;
    readonly Matrix4x4[] palette, blendedPalette;
    readonly string normalSignature, blendedSignature;
    readonly ModelVertex[] blendedFinal, blendedPosed;
    ModelVertex[] selectedFinal = [], selectedPosed = [];
    readonly Matrix4x4[] identity = [Matrix4x4.Identity, Matrix4x4.Identity];
    readonly RigidInstanceDraw[] equipment = new RigidInstanceDraw[1];
    readonly MotionKey key;
    internal Skeleton Skeleton { get; }
    internal SkinnedMeshHandle Host => host;
    internal Matrix4x4[] Palette => palette;
    internal CoverageFixture Data => data;
    internal SkinnedCoverageFrame Frame;
    internal string Signature { get; private set; }
    internal ModelVertex[] Final { get; }
    internal CoverageGeometryOracle.Result Oracle { get; }
    internal CoverageGeometryOracle.Result Baseline { get; }

    internal CoverageGpuActor(Scene3D scene, IGpuDevice device, int actor, bool mapped)
    {
        this.scene = scene;
        key = MotionKey.From((ulong)(100 + actor));
        data = CoverageFixture.CurvedPanel(seams: false);
        palette = CoverageFixture.Pose;
        if (actor % 2 != 0) palette[1] *= Matrix4x4.CreateRotationZ(.025f);
        blendedPalette = [palette[0], Matrix4x4.Lerp(palette[1], palette[1] * Matrix4x4.CreateRotationZ(.04f), .5f)];
        var skin = data.Source.SkinVertices.ToArray();
        if (mapped)
            for (int i = 0; i < skin.Length; i++)
                skin[i].Uv = new((skin[i].Position.X + 1.2f) / 2.4f, (skin[i].Position.Y + 1.2f) / 2.4f);
        var source = new CoverageSourceTopology(data.Source.Identity, CoverageFixture.Hash,
            data.Source.Positions.ToArray(), data.Source.Vertices, data.Source.Triangles, skin);
        var posed = new ModelVertex[skin.Length];
        Assert.True(CoverageReferenceSkinning.TrySkin(new(), new(source, 2), source, palette, posed, out _));
        data = data with { Source = source, Posed = posed };
        var skeleton = Skeleton = new Skeleton([-1, 0], [JointPose.Identity, JointPose.Identity], [0, 1], [0, 1], ["root", "hip"]);
        uint[] indices = source.Triangles.SelectMany(t => new[] { (uint)t.A, (uint)t.B, (uint)t.C }).ToArray();
        var mesh = new SkinnedGltfMesh(skin, indices, identity, identity, skeleton);
        var covered = new SkinnedGltfMesh(skin, indices, identity, identity, skeleton)
        { Coverage = HostCoveragePart.Create(data.Map, source, 0, mesh) };
        var maps = mapped ? new Scene3D.SurfaceMaps(default,
            scene.LoadTexture([160, 128, 250, 255], 1, 1)) : default;
        host = scene.LoadSkinnedMesh(covered, maps);
        conformer = scene.CreateHostConformer(skeleton, [host]);
        support = CoverageCommands.Plate(data);
        conformer.ReserveSupports([support]);
        rigid = scene.LoadMesh(new GltfMesh(data.RigidPositions.Select(p =>
            new ModelVertex(p, Vector3.UnitZ, Vector4.One)).ToArray(), data.RigidIndices));
        var referenceWorld = actor >= 4 ? CoverageGpuScene.ShadowWorlds[actor - 4] : Matrix4x4.CreateTranslation((actor % 2) * 3, 0, 0);
        var commands = CoverageCommands.Poses(data, conformer.Topology, support, referenceWorld);
        Final = new ModelVertex[conformer.Topology.VertexCount];
        Assert.True(CoverageReferenceConstructor.TryConstruct(conformer.Topology, data.Posed, referenceWorld,
            commands, new CoverageReferenceScratch(conformer.Topology), Final, out var failure), failure.ToString());
        var before = new ModelVertex[Final.Length];
        conformer.Topology.Interpolate(data.Posed, before);
        var refined = conformer.Topology.Indices.ToArray();
        bool[] protectedVertices = conformer.Topology.Vertices.Select(v => v.Strength == 0).ToArray();
        Oracle = CoverageGeometryOracle.Validate(before, Final, data, refined, protectedVertices);
        Baseline = CoverageGeometryOracle.Validate(before, before, data, refined, protectedVertices);
        Assert.Equal(0, Oracle.ClearanceViolations);
        Assert.Equal(0, Oracle.EquipmentIntersections);
        Assert.Equal(0, Oracle.Folds);
        Assert.Equal(0, Oracle.OpenBoundaryEdges);
        Assert.Equal(0, Oracle.ProtectedPositionChanges);
        Assert.True(Baseline.BreakthroughArea > 0);
        Signature = normalSignature = Hash(Final);
        var finalSkin = Final.Select(v => new SkinnedVertex
        {
            Position = v.Position,
            Normal = v.Normal,
            Color = v.Color,
            Uv = v.Uv,
            Tangent = v.Tangent,
            BoneWeights = new(1, 0, 0, 0),
        }).ToArray();
        reference = scene.LoadSkinnedMesh(new SkinnedGltfMesh(finalSkin, refined, identity, identity, skeleton), maps);
        var blendPosed = blendedPosed = new ModelVertex[skin.Length];
        Assert.True(CoverageReferenceSkinning.TrySkin(new(), new(source, 2), source, blendedPalette, blendPosed, out _));
        var blendFinal = blendedFinal = new ModelVertex[Final.Length];
        Assert.True(CoverageReferenceConstructor.TryConstruct(conformer.Topology, blendPosed, referenceWorld,
            commands, new CoverageReferenceScratch(conformer.Topology), blendFinal, out failure), failure.ToString());
        var blendBefore = new ModelVertex[Final.Length];
        conformer.Topology.Interpolate(blendPosed, blendBefore);
        var blendOracle = CoverageGeometryOracle.Validate(blendBefore, blendFinal, data with { Posed = blendPosed }, refined, protectedVertices);
        Assert.Equal(0, blendOracle.ClearanceViolations);
        Assert.Equal(0, blendOracle.EquipmentIntersections);
        Assert.Equal(0, blendOracle.Folds);
        Assert.Equal(0, blendOracle.ProtectedPositionChanges);
        blendedSignature = Hash(blendFinal);
        blendedReference = scene.LoadSkinnedMesh(new SkinnedGltfMesh(blendFinal.Select(v => new SkinnedVertex
        {
            Position = v.Position,
            Normal = v.Normal,
            Color = v.Color,
            Uv = v.Uv,
            Tangent = v.Tangent,
            BoneWeights = new(1, 0, 0, 0),
        }).ToArray(), refined, identity, identity, skeleton), maps);
        uint[] removal = Enumerable.Range(0, conformer.Topology.TriangleCount)
            .Where(t => conformer.Topology.Triangles[t].Role != CoverageTriangleRole.Core)
            .SelectMany(t => refined.Skip(t * 3).Take(3)).ToArray();
        removed = scene.LoadSkinnedMesh(new SkinnedGltfMesh(finalSkin, removal, identity, identity, skeleton), maps);
    }

    internal void Draw(CoverageCase row, int route, Matrix4x4 world, ulong instance = 1, float weight = 1,
        bool submitEquipment = true, float dissolve = 0, bool complement = false, bool invertShadow = false, bool blended = false, float? recession = null)
    {
        Frame = default;
        var selectedPalette = blended ? blendedPalette : palette;
        Signature = blended ? blendedSignature : normalSignature;
        selectedFinal = blended ? blendedFinal : Final;
        selectedPosed = blended ? blendedPosed : data.Posed;
        if (route == 0) Signature = Hash(selectedPosed);
        var rigidDraw = new RigidInstanceDraw(rigid, world)
        {
            Tint = new Color(.12f, .7f, 1, 1),
            ShadowOnly = row.ShadowOnly,
            Dissolve = row.EquipmentFade,
            DissolveComplement = complement ? 1 : 0,
            InvertShadowDissolve = invertShadow,
        };
        if (route == 1)
        {
            var commands = CoverageCommands.Poses(data, conformer.Topology, support, world)
                .Select(c => c with { EquipmentInstanceId = instance, TransitionWeight = weight, MaxWorldRecession = recession ?? c.MaxWorldRecession }).ToArray();
            Assert.True(scene.TryStageCoverageEquipment(key, 1, instance, support,
                [rigidDraw], equipment, out var failure), failure.ToString());
            Assert.True(conformer.TryPrepare(selectedPalette, world, commands, key,
                CoverageFallback.UncoveredCurrentPose, out Frame, out failure), failure.ToString());
        }
        var draw = new SkinnedInstanceDraw(route == 2 ? blended ? blendedReference : reference : route == 3 ? removed : host, world)
        {
            Coverage = Frame,
            Motion = key,
            ShadowOnly = row.ShadowOnly,
            Tint = Color.White,
            Material = Material.Shiny(.15f),
            Dissolve = dissolve,
            DissolveComplement = complement ? 1 : 0,
            InvertShadowDissolve = invertShadow,
        };
        var pose = route >= 2 ? identity : selectedPalette;
        scene.DrawSkinned(draw, pose);
        if (submitEquipment) scene.Draw(route == 1 ? equipment[0] : rigidDraw);
        if (row.Outlines)
        {
            var group = scene.BeginMeshOutline(Color.White, 1);
            scene.DrawSkinnedOutline(group, draw, pose);
        }
    }

    internal void SelectCpuFallbackSignature() => Signature = Hash(selectedPosed);

    internal void AssertSelectedWords(CoverageSceneSurfaceEvidence surface)
    {
        Assert.Equal(CoveragePresentation.Dispatched, scene.GetCoverageOutcome(Frame).Presentation);
        CoverageSceneEvidence.AssertAssociation(surface);
        Assert.Equal(Frame.Preparation!.Id, surface.PreparationId);
        Assert.Equal(MemoryMarshal.Cast<ModelVertex, uint>(selectedPosed).ToArray(), surface.SourceWords);
        if (surface.Selection == "final")
            Assert.Equal(MemoryMarshal.Cast<ModelVertex, uint>(selectedFinal).ToArray(), surface.FinalWords);
        else
            Assert.Equal("source", surface.Selection);
        Signature = surface.Signature;
    }

    internal void AssertCovered(CoverageSceneSurfaceEvidence surface)
    {
        AssertSelectedWords(surface);
        Assert.Equal("final", surface.Selection);
        Assert.Equal(CoveragePresentation.Covered, surface.CompletedOutcome!.Value.Outcome.Presentation);
        Assert.Equal(CoverageFailureCode.None, surface.CompletedOutcome.Value.Outcome.Failure.Code);
        Assert.Equal(0u, surface.HeaderWords[8]);
        Assert.Equal(0u, surface.HeaderWords[14]);
    }

    internal void AssertSelected(CoverageSceneSurfaceEvidence surface) => AssertCovered(surface);

    internal static string Hash(ModelVertex[] vertices) =>
        Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(vertices.AsSpan())));
    public void Dispose() => conformer.Dispose();
}
