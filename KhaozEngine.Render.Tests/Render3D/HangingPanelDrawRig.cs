using System;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

internal sealed partial class HangingPanelDrawRig : IDisposable
{
    private const int HostJointCount = 2;
    private const int JointsPerGuide = 16;
    private const float GuideStretch = .02f;
    private const float LowerStandoff = .008f;
    private const float LiftClearance = .001f;
    private static readonly ChainSettings GuideSettings = new(2.5f, 5f, 1f, .85f, MathF.PI * .65f, .0025f);
    private static readonly RigidInstanceDraw Template = new(default, Matrix4x4.Identity);

    private readonly OutfitRig rig = new();
    private readonly Matrix4x4[] palette = new Matrix4x4[HostJointCount];
    private readonly AttachmentDraw[] draws = new AttachmentDraw[1];
    private readonly Matrix4x4 supportFromBind;
    private readonly int chestBone;
    private readonly int supportBone;
    private readonly SkinnedGltfMesh host;
    private readonly LoadedBodyRegions bodyRegions;
    private readonly BoundOutfit outfit;
    private readonly int garmentIndex;
    private readonly WornMotion? motion;
    private readonly DrawPanel garment;
    private readonly DrawPanel? hardware;

    internal string GarmentMeshDigest { get; } = "";
    internal string GarmentDefinitionDigest { get; } = "";

    private (int Chest, int Support, SkinnedGltfMesh Host, LoadedBodyRegions Regions) CreateHost()
    {
        // The shared draw rig uses intentionally unmatched fixture binds. Here bind pose and posed joints
        // must agree because the physical witness is authored in model metres.
        var inverse = new Matrix4x4[HostJointCount];
        for (int bone = 0; bone < inverse.Length; bone++)
            inverse[bone] = Matrix4x4.CreateTranslation(-rig.Palette[bone].Translation);
        var body = new SkinnedGltfMesh(rig.Body.Vertices, rig.Body.Indices32, inverse, rig.Palette, rig.Skeleton);
        SkinnedMeshHandle hostHandle = rig.Scene.LoadSkinnedMesh(body);
        string hash = rig.BodyMap("panel-body.json", OutfitRig.Rig, new RegionRun(0, 4));
        LoadedBodyRegions regions = rig.Cache.GetBodyRegions("panel-body.json", hash, OutfitRig.Rig, [hostHandle], [body]);
        return (rig.Map.Bone("hip"), rig.Map.Bone("knee"), body, regions);
    }

    internal HangingPanelDrawRig(HangingPanelCut cut, in HangingPanelPose bindPose, ClothDrapeFixture? cloth = null,
        bool matchGuideLift = false)
    {
        (chestBone, supportBone, host, bodyRegions) = CreateHost();
        supportFromBind = Matrix4x4.CreateTranslation(-bindPose.Support.Translation);
        BoundAttachmentSet support = WearSupport(bindPose.Support.Translation);
        var drape = new PanelDrape(HangingPanelFixture.Seam, HangingPanelFixture.PoseAt(0).Support.Translation,
            HangingPanelCut.FittedRadius);
        (BoundAttachmentSet garmentSet, GarmentMeshDigest, GarmentDefinitionDigest) = WearPanel(cut, drape, cloth, matchGuideLift);
        outfit = rig.Cache.BindOutfit(bodyRegions, [support, garmentSet]);
        garmentIndex = 1;
        motion = outfit.CreateMotion(null, new ChainSimulationOptions
        {
            Gravity = new Vector3(0, -9.81f, 0),
            HoldBlendSeconds = cloth is null ? .25f : 0,
        });
        Assert.Equal(HangingPanelCut.Columns - 1, motion.RigLinksForTests.Count);
        Assert.All(motion.RigLinksForTests, link => Assert.Equal(GuideStretch, link.Stretch));
        Assert.False(ChainSheetContact.DisabledForTests);

        SetPose(bindPose);
        AttachmentDraw fitted = Write(garmentIndex, bindPose.Wearer);
        garment = new DrawPanel(rig.Scene, fitted, cloth is null ? cut.Faces : null);
        if (cloth is null)
        {
            Assert.True(rig.Scene.IsSkinnedMeshView(fitted.Skinned.Mesh));
            garment.VerifyRestPositions(cut.Fit(drape));
        }
        else
        {
            garment.VerifyRestPositions(cut.Fit(drape), allowTrailing: true);
            InitializeCloth(cloth, cut, garmentSet.ClothSurface(1)!, matchGuideLift);
        }
        hardware = new DrawPanel(rig.Scene, Write(0, bindPose.Wearer));
    }

    internal HangingPanelDrawRig(Vector3[] triangle, PanelFace face, bool welded)
        : this(triangle, [new PanelTriangle(0, 1, 2, face)], welded)
    {
    }

    internal HangingPanelDrawRig(Vector3[] positions, PanelTriangle[] faces, bool welded)
    {
        HangingPanelPose bindPose = HangingPanelFixture.PoseAt(0);
        (chestBone, supportBone, host, bodyRegions) = CreateHost();
        supportFromBind = Matrix4x4.CreateTranslation(-bindPose.Support.Translation);
        SkinnedAttachmentGlbFixture glb = HostGlb(HostJointCount);
        glb.SetPositions(0, positions);
        glb.SetUniformSkinning(0, positions.Length, new Vector4(welded ? 1 : 0, 0, 0, 0), Vector4.UnitX);
        glb.SetIndexedMaterialParts(Indices(faces), 1);
        var part = new SkinnedAttachmentPartDefinition(1, rig.Source.Mesh("marked-triangle.glb", glb.Build()));
        BoundAttachmentSet set = Wear("marked-triangle.json", new AttachmentSetDefinition([part]));
        outfit = rig.Cache.BindOutfit(bodyRegions, [set]);
        SetPose(bindPose);
        garment = new DrawPanel(rig.Scene, Write(0, bindPose.Wearer), faces);
    }

    internal PresentedPanel Present(in HangingPanelPose pose, HangingPanelCut? cut = null, float seconds = 0,
        MotionLevel level = MotionLevel.Held)
    {
        SetPose(pose);
        motion?.Step(rig.Skeleton, palette, pose.Wearer, seconds, level);
        garment.Read(Write(garmentIndex, pose.Wearer));
        VerifySupportPose(pose);
        return new PresentedPanel(garment.Positions, garment.Faces, cut is null ? default : cut.MaterialTriangles, cut?.Probes);
    }

    private void VerifySupportPose(in HangingPanelPose pose)
    {
        if (hardware is not { } lip) return;
        lip.Read(Write(0, pose.Wearer));
        // The middle, outboard generator is a fixed vertex in the hardware draw.
        Assert.InRange(Vector3.Distance(lip.Positions[SupportRingSize], pose.MarkerInModel), 0, .000001f);
    }

    private void SetPose(in HangingPanelPose pose)
    {
        palette[chestBone] = rig.Palette[chestBone] * pose.Chest;
        palette[supportBone] = rig.Palette[supportBone] * supportFromBind * pose.Support;
    }

    private AttachmentDraw Write(int worn, Matrix4x4 model)
    {
        int count = motion is null
            ? outfit.WriteDraws(worn, rig.Skeleton, palette, model, rig.Key, Template, draws)
            : outfit.WriteDraws(worn, motion, rig.Skeleton, palette, model, rig.Key, Template, draws);
        Assert.Equal(1, count);
        return draws[0];
    }

    private (BoundAttachmentSet Set, string MeshDigest, string DefinitionDigest) WearPanel(HangingPanelCut cut, PanelDrape drape, ClothDrapeFixture? cloth, bool matchGuideLift)
    {
        SkinnedAttachmentGlbFixture glb = HostGlb(HostJointCount + HangingPanelCut.Columns * JointsPerGuide);
        ChainMotionDefinition chainMotion = WriteGuides(glb, drape);
        if (cloth is not null)
            return WearClothPanel(glb, cut, drape, chainMotion, cloth, matchGuideLift);

        Vector3[] sourcePositions = cut.Fit(drape);
        var liftDirections = new Vector3[sourcePositions.Length];
        var liftBearings = new float[sourcePositions.Length];
        glb.SetUniformSkinning(0, sourcePositions.Length, Vector4.Zero, Vector4.UnitX);
        int jointAccessor = glb.Primitives[0]!["attributes"]!["JOINTS_0"]!.GetValue<int>();
        int weightAccessor = glb.Primitives[0]!["attributes"]!["WEIGHTS_0"]!.GetValue<int>();
        for (int vertex = 0; vertex < sourcePositions.Length; vertex++)
        {
            int materialIndex = vertex % HangingPanelCut.VerticesPerSkin;
            int row = materialIndex / HangingPanelCut.Columns;
            int column = materialIndex % HangingPanelCut.Columns;
            liftDirections[vertex] = Vector3.UnitZ;
            liftBearings[vertex] = LowerStandoff + LiftClearance;
            if (row == 0) continue; // The two seam skins are pinned directly to the chest.

            liftBearings[vertex] = LowerStandoff;
            sourcePositions[vertex] -= LiftClearance * Vector3.UnitZ;
            float alongGuide = row * JointsPerGuide / (HangingPanelCut.Rows - 1f);
            int upper = Math.Min((int)alongGuide, JointsPerGuide - 1);
            int lower = Math.Min(upper + 1, JointsPerGuide - 1);
            float lowerWeight = upper == lower ? 0 : alongGuide - upper;
            glb.SetElement(jointAccessor, vertex, GuideBone(column, upper), GuideBone(column, lower), 0, 0);
            glb.SetElement(weightAccessor, vertex, 1 - lowerWeight, lowerWeight, 0, 0);
        }

        glb.SetPositions(0, sourcePositions);
        glb.SetLiftAttributes(0, liftDirections, liftBearings);
        glb.SetIndexedMaterialParts(Indices(cut.Faces), 1);

        var part = new SkinnedAttachmentPartDefinition(1, rig.Source.Mesh("hanging-panel.glb", glb.Build()))
        {
            Regions = [new NamedRegionRun("alpha", cut.Faces.Length)],
            Motion = chainMotion,
            Lift = new(LiftClearance),
        };
        var definition = new AttachmentSetDefinition([part], AttachmentLayer.Outerwear, []);
        return (Wear("hanging-panel.json", definition), part.Mesh.Sha256, definition.SemanticSha256);
    }

    private ChainMotionDefinition WriteGuides(SkinnedAttachmentGlbFixture glb, PanelDrape drape)
    {
        JsonArray hipChildren = glb.Nodes[0]!["children"]!.AsArray();
        var chains = new ChainDefinition[HangingPanelCut.Columns];
        var links = new ChainLink[chains.Length - 1];
        for (int column = 0; column < chains.Length; column++)
        {
            hipChildren.Add(GuideBone(column, 0));
            var joints = new string[JointsPerGuide];
            Vector3 parent = rig.Palette[chestBone].Translation;
            for (int joint = 0; joint < joints.Length; joint++)
            {
                int bone = GuideBone(column, joint);
                Vector3 rest = OnGuide(joint);
                joints[joint] = $"guide-{column}-{joint}";
                glb.Nodes[bone]!["name"] = joints[joint];
                glb.Nodes[bone]!["translation"] = Translation(rest - parent);
                if (joint + 1 < joints.Length)
                    glb.Nodes[bone]!["children"] = new JsonArray(bone + 1);
                glb.SetInverseBind(bone, Matrix4x4.CreateTranslation(-rest));
                parent = rest;
            }
            chains[column] = new ChainDefinition(joints, OnGuide(JointsPerGuide) - parent, GuideSettings);
            if (column > 0) links[column - 1] = new ChainLink(column - 1, column);

            Vector3 OnGuide(int joint)
            {
                float length = joint * (HangingPanelFixture.Length / JointsPerGuide);
                return HangingPanelCut.GuidePoint(drape.AtLength(length).Point, column, length);
            }
        }
        return new ChainMotionDefinition(chains, links) { LinkStretch = GuideStretch };
    }

    private const int SupportRingSize = 48;

    private BoundAttachmentSet WearSupport(Vector3 center)
    {
        var vertices = new Vector3[3 * SupportRingSize];
        var indices = new ushort[12 * SupportRingSize];
        float span = HangingPanelFixture.LipHalfSpan;
        float radius = HangingPanelFixture.LipRadius;
        for (int ring = 0; ring < 3; ring++)
        for (int i = 0; i < SupportRingSize; i++)
        {
            float angle = MathF.Tau * i / SupportRingSize;
            int vertex = ring * SupportRingSize + i;
            vertices[vertex] = center + new Vector3((ring - 1) * span, radius * MathF.Sin(angle), radius * MathF.Cos(angle));
            if (ring == 2) continue;
            int next = ring * SupportRingSize + (i + 1) % SupportRingSize;
            int firstIndex = vertex * 6;
            indices[firstIndex] = (ushort)vertex;
            indices[firstIndex + 1] = (ushort)next;
            indices[firstIndex + 2] = (ushort)(next + SupportRingSize);
            indices[firstIndex + 3] = (ushort)vertex;
            indices[firstIndex + 4] = (ushort)(next + SupportRingSize);
            indices[firstIndex + 5] = (ushort)(vertex + SupportRingSize);
        }

        SkinnedAttachmentGlbFixture glb = HostGlb(HostJointCount);
        glb.SetPositions(0, vertices);
        glb.SetUniformSkinning(0, vertices.Length, new Vector4(1, 0, 0, 0), Vector4.UnitX);
        glb.SetIndexedMaterialParts(indices, 1);
        var part = new SkinnedAttachmentPartDefinition(1, rig.Source.Mesh("transverse-lip.glb", glb.Build()))
        {
            Regions = [new NamedRegionRun("alpha", indices.Length / 3)],
        };
        Vector3 radialSpan = Vector3.UnitX * span;
        var lip = new ColliderDefinition("knee", center - radialSpan, center + radialSpan, radius);
        Vector3 recessCenter = HangingPanelFixture.RecessCenter;
        var recess = new ColliderDefinition("hip", recessCenter - radialSpan, recessCenter + radialSpan, HangingPanelFixture.RecessRadius);
        return Wear("transverse-lip.json", new AttachmentSetDefinition([part], AttachmentLayer.Garment, [],
            [lip, recess], [new("alpha", LowerStandoff)]));
    }

    private SkinnedAttachmentGlbFixture HostGlb(int joints)
    {
        var glb = new SkinnedAttachmentGlbFixture(joints);
        glb.Nodes[0]!["name"] = "hip";
        glb.Nodes[0]!["children"] = new JsonArray(1);
        glb.Nodes[0]!["translation"] = Translation(rig.Palette[chestBone].Translation);
        glb.Nodes[1]!["name"] = "knee";
        glb.Nodes[1]!["translation"] = Translation(rig.Palette[supportBone].Translation - rig.Palette[chestBone].Translation);
        glb.SetInverseBind(0, host.InverseBind[chestBone]);
        glb.SetInverseBind(1, host.InverseBind[supportBone]);
        return glb;
    }

    private BoundAttachmentSet Wear(string key, AttachmentSetDefinition definition) =>
        rig.Load(key, definition).Bind(rig.Map, host);

    private static ushort[] Indices(PanelTriangle[] faces)
    {
        var indices = new ushort[faces.Length * 3];
        for (int i = 0; i < faces.Length; i++)
        {
            PanelTriangle face = faces[i];
            indices[3 * i] = (ushort)face.A;
            indices[3 * i + 1] = (ushort)face.B;
            indices[3 * i + 2] = (ushort)face.C;
        }
        return indices;
    }

    private static JsonArray Translation(Vector3 p) => new(p.X, p.Y, p.Z);
    private static int GuideBone(int column, int joint) => HostJointCount + column * JointsPerGuide + joint;

    public void Dispose()
    {
        outfit.Dispose();
        rig.Dispose();
    }

    private sealed class DrawPanel
    {
        private readonly SkinnedMeshHandle handle;
        private readonly SkinnedGltfMesh mesh;
        private readonly Matrix4x4[] composed;

        internal Vector3[] Positions { get; }
        internal PanelTriangle[] Faces { get; }
        internal SkinnedGltfMesh SourceMesh => mesh;

        internal DrawPanel(Scene3D scene, AttachmentDraw draw, PanelTriangle[]? reference = null)
        {
            Assert.True(draw.IsSkinned);
            handle = draw.Skinned.Mesh;
            mesh = scene.SkinnedSourceSnapshotForTests(handle);
            composed = new Matrix4x4[mesh.InverseBind.Length];
            Positions = new Vector3[mesh.Vertices.Length];
            Faces = reference is null ? [] : ReadFaces(mesh.Indices32, reference);
        }

        internal void VerifyRestPositions(Vector3[] expected, bool allowTrailing = false)
        {
            Assert.Equal(expected.Length, allowTrailing ? Math.Min(expected.Length, mesh.Vertices.Length) : mesh.Vertices.Length);
            for (int i = 0; i < expected.Length; i++)
                Assert.InRange(Vector3.Distance(expected[i], mesh.Vertices[i].Position), 0, .000001f);
        }

        internal void Read(AttachmentDraw draw)
        {
            Assert.Equal(handle, draw.Skinned.Mesh);
            ReadOnlySpan<Matrix4x4> palette = draw.BoneMatrices.Span;
            for (int bone = 0; bone < composed.Length; bone++)
                composed[bone] = SkinningMath.Compose(palette[bone], mesh.InverseBind[bone]);

            for (int vertex = 0; vertex < Positions.Length; vertex++)
                Positions[vertex] = SkinningMath.SkinVertex(mesh.Vertices[vertex], composed).Position;
        }

        private static PanelTriangle[] ReadFaces(uint[] indices, PanelTriangle[] reference)
        {
            Assert.Equal(reference.Length * 3, indices.Length);
            var labels = new PanelTriangle[reference.Length];
            for (int i = 0; i < indices.Length; i += 3)
            {
                PanelTriangle source = reference[i / 3];
                var submitted = new PanelTriangle((int)indices[i], (int)indices[i + 1], (int)indices[i + 2], source.Face);
                Assert.Equal(source, submitted);
                labels[i / 3] = submitted;
            }
            return labels;
        }
    }
}
