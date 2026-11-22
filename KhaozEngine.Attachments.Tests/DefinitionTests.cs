using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public class DefinitionTests
{
    [Fact]
    public void Definition_captures_owned_parts()
    {
        var parts = new List<AttachmentPartDefinition>
        {
            new SkinnedAttachmentPartDefinition(2, new("parts/plate.glb", AttachmentFixtures.Hash)),
            AttachmentFixtures.Part(1),
        };
        var first = new AttachmentSetDefinition(parts);
        var reordered = new AttachmentSetDefinition(new[] { parts[1], parts[0] });
        parts.Clear();

        Assert.Equal(first.SemanticSha256, reordered.SemanticSha256);
        Assert.Single(first.Meshes);
        Assert.Equal(2, first.Parts.Count);
        Assert.Equal(1u, first.Parts[0].Id);
        Assert.Equal(2u, first.Parts[1].Id);
        Assert.IsType<RigidAttachmentPartDefinition>(first.Parts[0]);
        Assert.IsType<SkinnedAttachmentPartDefinition>(first.Parts[1]);
        Assert.Throws<NotSupportedException>(() => ((IList<AttachmentPartDefinition>)first.Parts).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<AttachmentMeshRef>)first.Meshes).Clear());
    }

    [Fact]
    public void Meshes_AreOrderedByOrdinalKey()
    {
        var set = new AttachmentSetDefinition(new[]
        {
            AttachmentFixtures.Part(1) with { Mesh = new("z.glb", AttachmentFixtures.Hash) },
            AttachmentFixtures.Part(2) with { Mesh = new("A.glb", AttachmentFixtures.Hash) },
        });
        Assert.Equal("A.glb", set.Meshes[0].Key);
        Assert.Equal("z.glb", set.Meshes[1].Key);
    }

    [Theory]
    [InlineData("empty", AttachmentSetRefusal.PartsEmpty)]
    [InlineData("null-part", AttachmentSetRefusal.PartNull)]
    [InlineData("duplicate", AttachmentSetRefusal.PartIdInvalidOrDuplicate)]
    [InlineData("zero", AttachmentSetRefusal.PartIdInvalidOrDuplicate)]
    [InlineData("joint", AttachmentSetRefusal.JointNameEmpty)]
    [InlineData("key", AttachmentSetRefusal.MeshKeyInvalid)]
    [InlineData("hash", AttachmentSetRefusal.MeshHashInvalid)]
    [InlineData("conflicting-hash", AttachmentSetRefusal.MeshHashConflict)]
    [InlineData("singular", AttachmentSetRefusal.LocalTransformInvalid)]
    [InlineData("non-affine", AttachmentSetRefusal.LocalTransformInvalid)]
    [InlineData("non-finite", AttachmentSetRefusal.LocalTransformInvalid)]
    [InlineData("unknown-kind", AttachmentSetRefusal.PartKindUnsupported)]
    [InlineData("duplicate-across-kinds", AttachmentSetRefusal.PartIdInvalidOrDuplicate)]
    [InlineData("conflicting-hash-across-kinds", AttachmentSetRefusal.MeshHashConflict)]
    [InlineData("skinned-key", AttachmentSetRefusal.MeshKeyInvalid)]
    [InlineData("skinned-hash", AttachmentSetRefusal.MeshHashInvalid)]
    [InlineData("skinned-utf16", AttachmentSetRefusal.MeshKeyInvalid)]
    public void Constructor_RejectsInvalidProgrammaticDefinitions(string name, AttachmentSetRefusal reason)
    {
        RigidAttachmentPartDefinition part = AttachmentFixtures.Part(1);
        AttachmentPartDefinition[] parts = name switch
        {
            "empty" => Array.Empty<RigidAttachmentPartDefinition>(),
            "null-part" => new RigidAttachmentPartDefinition[] { null! },
            "duplicate" => new[] { part, part },
            "zero" => new[] { part with { Id = 0 } },
            "joint" => new[] { part with { Joint = " " } },
            "key" => new[] { part with { Mesh = new("../part.glb", AttachmentFixtures.Hash) } },
            "hash" => new[] { part with { Mesh = new("part.glb", "bad") } },
            "conflicting-hash" => new[] { part, AttachmentFixtures.Part(2) with { Mesh = new("parts/plate.glb", new string('b', 64)) } },
            "singular" => new[] { part with { Local = Matrix4x4.CreateScale(0, 1, 1) } },
            "non-affine" => new[] { part with { Local = Matrix4x4.CreatePerspectiveFieldOfView(1, 1, 1, 10) } },
            "non-finite" => new[] { part with { Local = Matrix4x4.CreateTranslation(float.NaN, 0, 0) } },
            "unknown-kind" => [new UnknownAttachmentPart(1, part.Mesh)],
            "duplicate-across-kinds" => [part, new SkinnedAttachmentPartDefinition(1, part.Mesh)],
            "conflicting-hash-across-kinds" => [part, new SkinnedAttachmentPartDefinition(2, new(part.Mesh.Key, new string('b', 64)))],
            "skinned-key" => [new SkinnedAttachmentPartDefinition(1, new("../part.glb", AttachmentFixtures.Hash))],
            "skinned-hash" => [new SkinnedAttachmentPartDefinition(1, new("part.glb", "bad"))],
            "skinned-utf16" => [new SkinnedAttachmentPartDefinition(1, new("part\uD800.glb", AttachmentFixtures.Hash))],
            _ => throw new ArgumentException(name),
        };
        Assert.Single(new AttachmentSetDefinition([part]).Parts);
        var error = Assert.Throws<AttachmentSetArgumentException>(() => new AttachmentSetDefinition(parts));
        Assert.IsAssignableFrom<ArgumentException>(error);
        Assert.Equal(reason, error.Reason);
        Assert.Equal("parts", error.ParamName);
        if (reason == AttachmentSetRefusal.PartsEmpty) Assert.Equal(0, error.PartCount);
        else if (reason == AttachmentSetRefusal.PartNull) Assert.Equal(0, error.PartIndex);
        else
        {
            Assert.Equal(name is "conflicting-hash" or "conflicting-hash-across-kinds" ? 2u : name == "zero" ? 0u : 1u, error.PartId);
            if (reason == AttachmentSetRefusal.PartIdInvalidOrDuplicate)
                Assert.Equal(name == "zero" ? 0 : 1, error.PartIndex);
            if (reason == AttachmentSetRefusal.PartKindUnsupported) Assert.Equal(typeof(UnknownAttachmentPart), error.ActualType);
            if (reason is AttachmentSetRefusal.MeshKeyInvalid or AttachmentSetRefusal.MeshHashInvalid or AttachmentSetRefusal.MeshHashConflict)
                Assert.Equal(parts[^1].Mesh.Key, error.MeshKey);
            if (reason is AttachmentSetRefusal.MeshHashInvalid or AttachmentSetRefusal.MeshHashConflict)
                Assert.Equal(parts[^1].Mesh.Sha256, error.DeclaredHash);
            if (reason == AttachmentSetRefusal.MeshHashConflict) Assert.Equal(AttachmentFixtures.Hash, error.ExistingHash);
        }
    }

    [Fact]
    public void References_RequireEveryJointAndMatchingMeshHash()
    {
        var set = new AttachmentSetDefinition(new[] { AttachmentFixtures.Part(1) });
        var joints = new HashSet<string>(StringComparer.Ordinal) { "hip" };
        var meshes = new Dictionary<string, string> { ["parts/plate.glb"] = AttachmentFixtures.Hash };
        set.ValidateReferences(joints, meshes);

        joints.Clear();
        AttachmentSetReferenceAssertions.Joint(() => set.ValidateReferences(joints, meshes), 1, "hip");
        joints.Add("hip");
        meshes.Clear();
        AttachmentSetReferenceAssertions.Mesh(() => set.ValidateReferences(joints, meshes), 1, "parts/plate.glb", null);
        meshes["parts/plate.glb"] = new string('b', 64);
        AttachmentSetReferenceAssertions.Mesh(() => set.ValidateReferences(joints, meshes), 1, "parts/plate.glb", new string('b', 64));
    }

    [Fact]
    public void References_require_all_meshes_and_only_explicit_rigid_joints()
    {
        var skinned = new SkinnedAttachmentPartDefinition(2, new("parts/garment.glb", AttachmentFixtures.Hash));
        var set = new AttachmentSetDefinition([AttachmentFixtures.Part(1), skinned]);
        var joints = new HashSet<string>(StringComparer.Ordinal) { "hip" };
        var meshes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["parts/plate.glb"] = AttachmentFixtures.Hash,
            ["parts/garment.glb"] = AttachmentFixtures.Hash,
        };
        set.ValidateReferences(joints, meshes);
        new AttachmentSetDefinition([skinned]).ValidateReferences(new HashSet<string>(), meshes);

        meshes.Remove(skinned.Mesh.Key);
        AttachmentSetReferenceAssertions.Mesh(() => set.ValidateReferences(joints, meshes), 2, "parts/garment.glb", null);
        meshes[skinned.Mesh.Key] = new string('b', 64);
        AttachmentSetReferenceAssertions.Mesh(() => set.ValidateReferences(joints, meshes), 2, "parts/garment.glb", new string('b', 64));
        meshes[skinned.Mesh.Key] = AttachmentFixtures.Hash;
        joints.Clear();
        AttachmentSetReferenceAssertions.Joint(() => set.ValidateReferences(joints, meshes), 1, "hip");
    }

    [Fact]
    public void References_remain_ordinal_with_case_insensitive_collections()
    {
        var set = new AttachmentSetDefinition([AttachmentFixtures.Part(1)]);
        var joints = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "HIP" };
        var meshes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["parts/plate.glb"] = AttachmentFixtures.Hash };
        AttachmentSetReferenceAssertions.Joint(() => set.ValidateReferences(joints, meshes), 1, "hip");
        joints.Clear();
        joints.Add("hip");
        meshes.Clear();
        meshes["PARTS/PLATE.GLB"] = AttachmentFixtures.Hash;
        AttachmentSetReferenceAssertions.Mesh(() => set.ValidateReferences(joints, meshes), 1, "parts/plate.glb", null);
    }

    [Fact]
    public void SemanticRevision_ChangesWithMeshBytesJointOrLocalTransform()
    {
        RigidAttachmentPartDefinition part = AttachmentFixtures.Part(1);
        string initial = new AttachmentSetDefinition(new[] { part }).SemanticSha256;
        foreach (RigidAttachmentPartDefinition changed in new[]
        {
            part with { Mesh = new(part.Mesh.Key, new string('b', 64)) },
            part with { Joint = "knee" },
            part with { Local = Matrix4x4.CreateTranslation(1, 2, 3) },
        })
            Assert.NotEqual(initial, new AttachmentSetDefinition(new[] { changed }).SemanticSha256);
    }

    [Fact]
    public void Semantic_revision_distinguishes_part_kinds_and_skinned_mesh_revisions()
    {
        RigidAttachmentPartDefinition rigid = AttachmentFixtures.Part(1);
        var skinned = new SkinnedAttachmentPartDefinition(1, rigid.Mesh);
        string revision = new AttachmentSetDefinition([skinned]).SemanticSha256;
        Assert.NotEqual(new AttachmentSetDefinition([rigid]).SemanticSha256, revision);
        Assert.NotEqual(revision, new AttachmentSetDefinition([skinned with { Mesh = new(rigid.Mesh.Key, new string('b', 64)) }]).SemanticSha256);
    }

    [Fact]
    public void Semantic_revision_follows_layer_covers_and_region_tags()
    {
        string revision = AttachmentFixtures.RegionsSet().SemanticSha256;
        NamedRegionRun[] split = [new("a", 2), new("a", 1), new("b", 1)];
        var equal = new AttachmentSetDefinition([AttachmentFixtures.TaggedSkinned(split), AttachmentFixtures.TaggedRigid()], AttachmentLayer.Garment, ["b", "a"]);
        split[0] = new("b", 2);

        Assert.Equal(revision, equal.SemanticSha256);
        Assert.Equal(["a", "b"], equal.Covers);
        Assert.Equal([new NamedRegionRun("a", 3), new NamedRegionRun("b", 1)], Assert.IsType<SkinnedAttachmentPartDefinition>(equal.Parts[1]).Regions);
        Assert.Equal(AttachmentFixtures.TaggedSkinned(), equal.Parts[1]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)equal.Covers).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<NamedRegionRun>)((SkinnedAttachmentPartDefinition)equal.Parts[1]).Regions).Clear());
        foreach (AttachmentSetDefinition changed in new[]
        {
            AttachmentFixtures.RegionsSet(AttachmentLayer.Armour),
            new([AttachmentFixtures.TaggedRigid(), AttachmentFixtures.TaggedSkinned()], AttachmentLayer.Garment, ["a"]),
            new([AttachmentFixtures.TaggedRigid("b"), AttachmentFixtures.TaggedSkinned()], AttachmentLayer.Garment, ["a", "b"]),
            new([AttachmentFixtures.TaggedRigid(), AttachmentFixtures.TaggedSkinned(new("a", 2), new("b", 2))], AttachmentLayer.Garment, ["a", "b"]),
        })
            Assert.NotEqual(revision, changed.SemanticSha256);
    }

    [Theory]
    [InlineData("undefined layer", AttachmentSetRefusal.LayerUnknown)]
    [InlineData("covers lists 'a' twice", AttachmentSetRefusal.CoveredRegionDuplicate)]
    [InlineData("blank covered region", AttachmentSetRefusal.RegionNameEmpty)]
    [InlineData("malformed covered region", AttachmentSetRefusal.RegionNameMalformedUtf16)]
    [InlineData("rigid part without region", AttachmentSetRefusal.PartRegionMissing)]
    [InlineData("skinned part without regions", AttachmentSetRefusal.PartRegionMissing)]
    [InlineData("blank rigid region", AttachmentSetRefusal.RegionNameEmpty)]
    [InlineData("malformed run region", AttachmentSetRefusal.RegionNameMalformedUtf16)]
    [InlineData("a run of zero triangles", AttachmentSetRefusal.PartRegionRunEmpty)]
    [InlineData("runs past Int32", AttachmentSetRefusal.RegionTriangleCountOverflow)]
    [InlineData("rigid region without a layer", AttachmentSetRefusal.LayerMissing)]
    [InlineData("skinned regions without a layer", AttachmentSetRefusal.LayerMissing)]
    public void A_bad_layered_definition_is_refused_with_its_reason(string fault, AttachmentSetRefusal reason)
    {
        RigidAttachmentPartDefinition rigid = AttachmentFixtures.TaggedRigid();
        SkinnedAttachmentPartDefinition skinned = AttachmentFixtures.TaggedSkinned();
        Assert.Equal(AttachmentLayer.Garment, AttachmentFixtures.RegionsSet().Layer);
        Assert.Null(new AttachmentSetDefinition([AttachmentFixtures.Part(1), skinned with { Regions = [] }]).Layer);
        Func<AttachmentSetDefinition> build = fault switch
        {
            "undefined layer" => () => new([rigid, skinned], (AttachmentLayer)5, ["a"]),
            "covers lists 'a' twice" => () => new([rigid, skinned], AttachmentLayer.Garment, ["a", "b", "a"]),
            "blank covered region" => () => new([rigid, skinned], AttachmentLayer.Garment, ["a", " "]),
            "malformed covered region" => () => new([rigid, skinned], AttachmentLayer.Garment, ["a", "\uD800"]),
            "rigid part without region" => () => new([rigid with { Region = null }, skinned], AttachmentLayer.Garment, ["a"]),
            "skinned part without regions" => () => new([rigid, skinned with { Regions = [] }], AttachmentLayer.Garment, ["a"]),
            "blank rigid region" => () => new([rigid with { Region = "" }, skinned], AttachmentLayer.Garment, ["a"]),
            "malformed run region" => () => new([rigid, AttachmentFixtures.TaggedSkinned(new NamedRegionRun("\uDC00", 1))], AttachmentLayer.Garment, ["a"]),
            "a run of zero triangles" => () => new([rigid, AttachmentFixtures.TaggedSkinned(new("a", 3), new("b", 0))], AttachmentLayer.Garment, ["a"]),
            "runs past Int32" => () => new([rigid, AttachmentFixtures.TaggedSkinned(new("a", int.MaxValue), new("b", 1))], AttachmentLayer.Garment, ["a"]),
            "rigid region without a layer" => () => new([rigid, skinned with { Regions = [] }]),
            "skinned regions without a layer" => () => new([AttachmentFixtures.Part(1), skinned]),
            _ => throw new ArgumentException(fault),
        };
        var error = Assert.Throws<AttachmentSetArgumentException>(() => build());
        Assert.Equal(reason, error.Reason);
        Assert.Equal(fault switch { "undefined layer" => "layer", "covers lists 'a' twice" or "blank covered region" or "malformed covered region" => "covers", _ => "parts" }, error.ParamName);
        Assert.Equal(fault == "undefined layer" ? (AttachmentLayer?)5 : null, error.Layer);
        Assert.Equal(fault switch { "covers lists 'a' twice" => (int?)2, "blank covered region" or "malformed covered region" => 1, _ => null }, error.CoverIndex);
        Assert.Equal(fault switch
        {
            "covers lists 'a' twice" => (string?)"a",
            "blank covered region" => " ",
            "malformed covered region" => "\uD800",
            "blank rigid region" => "",
            "malformed run region" => "\uDC00",
            "a run of zero triangles" or "runs past Int32" => "b",
            _ => null,
        }, error.RegionName);
        Assert.Equal(fault switch
        {
            "rigid part without region" or "blank rigid region" or "rigid region without a layer" => (uint?)1,
            "skinned part without regions" or "malformed run region" or "a run of zero triangles" or "runs past Int32" or "skinned regions without a layer" => 2u,
            _ => null,
        }, error.PartId);
        Assert.Equal(fault switch { "malformed run region" => (int?)0, "a run of zero triangles" or "runs past Int32" => 1, _ => null }, error.RunIndex);
        Assert.Equal(fault == "runs past Int32" ? (long?)int.MaxValue + 1 : null, error.ActualTriangleCount);
    }

    [Fact]
    public void Skinned_part_value_equality_includes_motion_and_joint_order()
    {
        SkinnedAttachmentPartDefinition follows = AttachmentFixtures.TaggedSkinned();
        SkinnedAttachmentPartDefinition swings = follows with { Motion = AttachmentFixtures.Motion() };
        SkinnedAttachmentPartDefinition copy = follows with { Motion = AttachmentFixtures.Motion() };
        Assert.NotEqual(follows, swings);
        Assert.NotEqual(follows.GetHashCode(), swings.GetHashCode());
        Assert.Equal(swings, copy);
        Assert.Equal(swings.GetHashCode(), copy.GetHashCode());

        var motion = Assert.IsType<ChainMotionDefinition>(copy.Motion);
        SkinnedAttachmentPartDefinition reordered = copy with
        {
            Motion = motion with { Chains = [motion.Chains[0] with { Joints = ["left1", "left0"] }, motion.Chains[1], motion.Chains[2]] },
        };
        Assert.NotEqual(swings, reordered);
    }

    [Fact]
    public void A_worn_set_owns_motion_lists_and_colliders()
    {
        string[] joints = ["left0", "left1"];
        ChainDefinition[] chains = [AttachmentFixtures.Chain(0) with { Joints = joints }, AttachmentFixtures.Chain(1)];
        ChainLink[] links = [new(1, 0)];
        ColliderDefinition[] colliders = AttachmentFixtures.Colliders();
        var part = AttachmentFixtures.TaggedSkinned() with { Motion = new ChainMotionDefinition(chains, links) };
        var set = new AttachmentSetDefinition([part], AttachmentLayer.Garment, [], colliders);
        byte[] before = AttachmentSetJson.Write(set);

        joints[0] = "other";
        chains[1] = AttachmentFixtures.Chain(2);
        links[0] = new(0, 0);
        colliders[0] = default;

        ChainMotionDefinition admitted = Assert.IsType<ChainMotionDefinition>(Assert.IsType<SkinnedAttachmentPartDefinition>(Assert.Single(set.Parts)).Motion);
        Assert.Equal([AttachmentFixtures.Chain(0), AttachmentFixtures.Chain(1)], admitted.Chains);
        Assert.Equal(new ChainLink(0, 1), Assert.Single(admitted.Links));
        Assert.Equal(AttachmentFixtures.Colliders(), set.Colliders);
        Assert.Equal(before, AttachmentSetJson.Write(set));
    }

    [Theory]
    [InlineData(AttachmentSetRefusal.MotionKindUnsupported)]
    [InlineData(AttachmentSetRefusal.ChainsEmpty)]
    [InlineData(AttachmentSetRefusal.TooManyChains)]
    [InlineData(AttachmentSetRefusal.ChainJointsEmpty)]
    [InlineData(AttachmentSetRefusal.TooManyChainJoints)]
    [InlineData(AttachmentSetRefusal.ChainJointNameEmpty)]
    [InlineData(AttachmentSetRefusal.ChainJointNameMalformedUtf16)]
    [InlineData(AttachmentSetRefusal.ChainJointDuplicate)]
    [InlineData(AttachmentSetRefusal.ChainTipInvalid)]
    [InlineData(AttachmentSetRefusal.ChainSettingOutOfRange)]
    [InlineData(AttachmentSetRefusal.ChainLinkInvalid)]
    [InlineData(AttachmentSetRefusal.ChainLinkDuplicate)]
    [InlineData(AttachmentSetRefusal.TooManyLinks)]
    [InlineData(AttachmentSetRefusal.ColliderJointNameEmpty)]
    [InlineData(AttachmentSetRefusal.ColliderJointNameMalformedUtf16)]
    [InlineData(AttachmentSetRefusal.ColliderPointInvalid)]
    [InlineData(AttachmentSetRefusal.ColliderRadiusInvalid)]
    [InlineData(AttachmentSetRefusal.TooManyColliders)]
    [InlineData(AttachmentSetRefusal.MotionWithoutLayer)]
    public void Motion_and_colliders_are_admitted_with_typed_constructor_refusals(AttachmentSetRefusal reason)
    {
        var part = AttachmentFixtures.TaggedSkinned();
        ChainMotionDefinition motion = AttachmentFixtures.Motion();
        ColliderDefinition[] colliders = AttachmentFixtures.Colliders();
        ChainDefinition first = motion.Chains[0];
        ColliderDefinition firstCollider = colliders[0];
        switch (reason)
        {
            case AttachmentSetRefusal.ChainsEmpty:
                motion = new([first], []);
                break;
            case AttachmentSetRefusal.TooManyChains:
                motion = motion with { Chains = AttachmentFixtures.NumberedChains(32) };
                break;
            case AttachmentSetRefusal.TooManyChainJoints:
                first = first with { Joints = AttachmentFixtures.NumberedJoints(16) };
                motion = motion with { Chains = [first, motion.Chains[1], motion.Chains[2]] };
                break;
            case AttachmentSetRefusal.TooManyLinks:
                motion = new(AttachmentFixtures.NumberedChains(12), AttachmentFixtures.NumberedLinks(64));
                break;
            case AttachmentSetRefusal.TooManyColliders:
                colliders = Enumerable.Repeat(firstCollider, 8).ToArray();
                break;
        }
        ChainDefinition ChangeFirstJoint(string joint) => first with { Joints = [joint, first.Joints[1]] };
        ChainMotionDefinition ChangeFirstChain(ChainDefinition chain) => motion with { Chains = [chain, motion.Chains[1], motion.Chains[2]] };
        var refusedMotion = reason switch
        {
            AttachmentSetRefusal.MotionKindUnsupported => (PartMotionDefinition)new UnknownMotion(),
            AttachmentSetRefusal.ChainsEmpty => motion with { Chains = [] },
            AttachmentSetRefusal.TooManyChains => motion with { Chains = AttachmentFixtures.NumberedChains(33) },
            AttachmentSetRefusal.ChainJointsEmpty => ChangeFirstChain(first with { Joints = [] }),
            AttachmentSetRefusal.TooManyChainJoints => ChangeFirstChain(first with { Joints = AttachmentFixtures.NumberedJoints(17) }),
            AttachmentSetRefusal.ChainJointNameEmpty => ChangeFirstChain(ChangeFirstJoint(" ")),
            AttachmentSetRefusal.ChainJointNameMalformedUtf16 => ChangeFirstChain(ChangeFirstJoint("\uD800")),
            AttachmentSetRefusal.ChainJointDuplicate => ChangeFirstChain(ChangeFirstJoint("right0")),
            AttachmentSetRefusal.ChainTipInvalid => ChangeFirstChain(first with { Tip = Vector3.Zero }),
            AttachmentSetRefusal.ChainSettingOutOfRange => ChangeFirstChain(first with { Settings = first.Settings with { Trail = 1.25f } }),
            AttachmentSetRefusal.ChainLinkInvalid => motion with { Links = [new(0, 3), motion.Links[1]] },
            AttachmentSetRefusal.ChainLinkDuplicate => motion with { Links = [..motion.Links, new(1, 0)] },
            AttachmentSetRefusal.TooManyLinks => motion with { Links = AttachmentFixtures.NumberedLinks(65) },
            _ => motion,
        };
        var refusedColliders = reason switch
        {
            AttachmentSetRefusal.ColliderJointNameEmpty => [firstCollider with { Joint = " " }, colliders[1]],
            AttachmentSetRefusal.ColliderJointNameMalformedUtf16 => [firstCollider with { Joint = "\uD800" }, colliders[1]],
            AttachmentSetRefusal.ColliderPointInvalid => [firstCollider with { Start = new(float.PositiveInfinity, 0, 0) }, colliders[1]],
            AttachmentSetRefusal.ColliderRadiusInvalid => [firstCollider with { Radius = 0 }, colliders[1]],
            AttachmentSetRefusal.TooManyColliders => [..colliders, firstCollider],
            _ => colliders,
        };

        bool noLayer = reason == AttachmentSetRefusal.MotionWithoutLayer;
        if (noLayer) part = part with { Regions = [] };
        AttachmentSetDefinition Admit(PartMotionDefinition? candidate, ColliderDefinition[] candidates) => noLayer
            ? new([part with { Motion = candidate }])
            : new([part with { Motion = candidate }], AttachmentLayer.Garment, [], candidates);

        Assert.Single(Admit(noLayer ? null : motion, colliders).Parts);
        var error = Assert.Throws<AttachmentSetArgumentException>(() => Admit(refusedMotion, refusedColliders));
        Assert.Equal(reason, error.Reason);
        bool colliderFault = reason is AttachmentSetRefusal.ColliderJointNameEmpty or AttachmentSetRefusal.ColliderJointNameMalformedUtf16
            or AttachmentSetRefusal.ColliderPointInvalid or AttachmentSetRefusal.ColliderRadiusInvalid or AttachmentSetRefusal.TooManyColliders;
        Assert.Equal(colliderFault ? "colliders" : "parts", error.ParamName);
        Assert.Equal(colliderFault ? null : (uint?)2, error.PartId);
        Assert.Equal(reason == AttachmentSetRefusal.MotionKindUnsupported ? typeof(UnknownMotion) : null, error.ActualType);
        Assert.Equal(reason switch
        {
            AttachmentSetRefusal.ChainJointsEmpty or AttachmentSetRefusal.TooManyChainJoints or AttachmentSetRefusal.ChainJointNameEmpty
                or AttachmentSetRefusal.ChainJointNameMalformedUtf16 or AttachmentSetRefusal.ChainTipInvalid or AttachmentSetRefusal.ChainSettingOutOfRange => 0,
            AttachmentSetRefusal.ChainJointDuplicate => 1,
            _ => (int?)null,
        }, error.ChainIndex);
        Assert.Equal(reason switch
        {
            AttachmentSetRefusal.ChainJointNameEmpty or AttachmentSetRefusal.ChainJointNameMalformedUtf16 or AttachmentSetRefusal.ChainJointDuplicate => 0,
            _ => (int?)null,
        }, error.JointIndex);
        Assert.Equal(reason switch { AttachmentSetRefusal.ChainLinkInvalid => 0, AttachmentSetRefusal.ChainLinkDuplicate => 2, _ => (int?)null }, error.LinkIndex);
        Assert.Equal(colliderFault && reason != AttachmentSetRefusal.TooManyColliders ? 0 : (int?)null, error.ColliderIndex);
        Assert.Equal(reason == AttachmentSetRefusal.ChainSettingOutOfRange ? "trail" : null, error.SettingName);
        Assert.Equal(reason switch
        {
            AttachmentSetRefusal.ChainJointNameEmpty or AttachmentSetRefusal.ColliderJointNameEmpty => " ",
            AttachmentSetRefusal.ChainJointNameMalformedUtf16 or AttachmentSetRefusal.ColliderJointNameMalformedUtf16 => "\uD800",
            AttachmentSetRefusal.ChainJointDuplicate => "right0",
            AttachmentSetRefusal.ColliderPointInvalid or AttachmentSetRefusal.ColliderRadiusInvalid => "legL",
            _ => null,
        }, error.JointName);
        Assert.Equal(reason switch
        {
            AttachmentSetRefusal.ChainsEmpty or AttachmentSetRefusal.ChainJointsEmpty => 0,
            AttachmentSetRefusal.TooManyChains => 33,
            AttachmentSetRefusal.TooManyChainJoints => 17,
            AttachmentSetRefusal.TooManyLinks => 65,
            AttachmentSetRefusal.TooManyColliders => 9,
            _ => (int?)null,
        }, error.ActualCount);
    }

    sealed record UnknownMotion : PartMotionDefinition;

    sealed record UnknownAttachmentPart(uint Id, AttachmentMeshRef Mesh) : AttachmentPartDefinition(Id, Mesh);
}

internal static class AttachmentFixtures
{
    internal const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string Identity = "[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1]";
    internal static RigidAttachmentPartDefinition Part(uint id) => new(id, new("parts/plate.glb", Hash), "hip", Matrix4x4.Identity);
    internal static string PartJson(uint id = 1) => $$"""{"id":{{id}},"mesh":{"key":"parts/plate.glb","sha256":"{{Hash}}"},"joint":"hip","local":{{Identity}}} """;
    internal static string SetJson(string parts) => $$"""{"formatVersion":1,"parts":[{{parts}}]}""";

    internal static RigidAttachmentPartDefinition TaggedRigid(string region = "a") => Part(1) with { Region = region };

    internal static SkinnedAttachmentPartDefinition TaggedSkinned(params NamedRegionRun[] runs) =>
        new(2, new("parts/garment.glb", Hash)) { Regions = runs.Length == 0 ? [new("a", 3), new("b", 1)] : runs };

    internal static AttachmentSetDefinition RegionsSet(AttachmentLayer layer = AttachmentLayer.Garment) =>
        new([TaggedRigid(), TaggedSkinned()], layer, ["a", "b"]);

    // The canonical format 3 bytes of RegionsSet(), member order as Write emits it.
    internal static string RegionsSetJson(string layer = "garment") =>
        $$"""{"formatVersion":3,"layer":"{{layer}}","covers":["a","b"],"parts":[{"id":1,"kind":"rigid","mesh":{"key":"parts/plate.glb","sha256":"{{Hash}}"},"joint":"hip","local":{{Identity}},"region":"a"},{"id":2,"kind":"skinned","mesh":{"key":"parts/garment.glb","sha256":"{{Hash}}"},"regions":[["a",3],["b",1]]}]}""";

    internal static ChainDefinition Chain(int index) => index switch
    {
        0 => new(["left0", "left1"], new(0, -0.25f, 0), new(6, 3, 1, 0.7f, 1.4f, 0.02f)),
        1 => new(["right0"], new(0, -0.5f, 0), new(8, 4, 2, 0.5f, 1, 0)),
        2 => new(["rear0"], new(0, -0.125f, 0), new(0, 0, 0, 0, 0.5f, 0)),
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal static ChainMotionDefinition Motion() => new([Chain(0), Chain(1), Chain(2)], [new(0, 1), new(1, 2)]);

    internal static ColliderDefinition[] Colliders() => [new("legL", Vector3.Zero, Vector3.UnitY, 0.125f), new("upper", Vector3.UnitY, Vector3.UnitY, 0.25f)];

    internal static string[] NumberedJoints(int count) => Enumerable.Range(0, count).Select(i => $"joint{i}").ToArray();

    internal static ChainDefinition[] NumberedChains(int count) => NumberedJoints(count).Select(name => Chain(0) with { Joints = [name] }).ToArray();

    internal static ChainLink[] NumberedLinks(int count) =>
        (from first in Enumerable.Range(0, 12)
         from second in Enumerable.Range(first + 1, 11 - first)
         select new ChainLink(first, second)).Take(count).ToArray();

    internal const string Chain0Json = """{"joints":["left0","left1"],"tip":[0,-0.25,0],"stiffness":6,"damping":3,"gravity":1,"trail":0.7,"angleLimit":1.4,"radius":0.02}""";
    internal const string Chain1Json = """{"joints":["right0"],"tip":[0,-0.5,0],"stiffness":8,"damping":4,"gravity":2,"trail":0.5,"angleLimit":1,"radius":0}""";
    internal const string Chain2Json = """{"joints":["rear0"],"tip":[0,-0.125,0],"stiffness":0,"damping":0,"gravity":0,"trail":0,"angleLimit":0.5,"radius":0}""";
    internal const string Collider0Json = """{"joint":"legL","start":[0,0,0],"end":[0,1,0],"radius":0.125}""";
    internal const string Collider1Json = """{"joint":"upper","start":[0,1,0],"end":[0,1,0],"radius":0.25}""";

    internal static string MotionSetJson() =>
        $$$"""{"formatVersion":4,"layer":"garment","covers":[],"colliders":[{{{Collider0Json}}},{{{Collider1Json}}}],"parts":[{"id":2,"kind":"skinned","mesh":{"key":"parts/garment.glb","sha256":"{{{Hash}}}"},"regions":[["a",4]],"motion":{"kind":"chains","chains":[{{{Chain0Json}}},{{{Chain1Json}}},{{{Chain2Json}}}],"links":[[0,1],[1,2]]}}]}""";

}
