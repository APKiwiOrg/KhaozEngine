using System;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Tests.Attachments;

namespace KhaozEngine.Tests.Render3D;

internal static class SwingCostFixture
{
    const int HostBones = 2;
    const int BodyColliders = 12;

    internal static SwingCostPiece LoadPiece(OutfitRig rig, string name, AttachmentLayer layer, string anchor,
        int chainCount, int chainLength, Vector3 firstRoot, float? linkStretch = null, ColliderDefinition[]? colliders = null,
        float chainSpacing = .12f, Func<int, ChainSettings>? settingsForChain = null)
    {
        var glb = new SkinnedAttachmentGlbFixture(HostBones + chainCount * chainLength);
        Vector3 hip = ModelBindPosition(rig, "hip");
        Vector3 knee = ModelBindPosition(rig, "knee");
        bool onHip = anchor == "hip";
        var hipChildren = new JsonArray(1);
        JsonArray anchorChildren = onHip ? hipChildren : new JsonArray();
        glb.Nodes[0]!["children"] = hipChildren;
        if (!onHip) glb.Nodes[1]!["children"] = anchorChildren;
        glb.Nodes[0]!["name"] = "hip";
        glb.Nodes[1]!["name"] = "knee";
        glb.Nodes[0]!["translation"] = Translation(hip);
        glb.Nodes[1]!["translation"] = Translation(knee - hip);
        glb.SetInverseBind(0, rig.Body.InverseBind[rig.Map.Bone("hip")]);
        glb.SetInverseBind(1, rig.Body.InverseBind[rig.Map.Bone("knee")]);

        var chains = new ChainDefinition[chainCount];
        var links = new ChainLink[linkStretch.HasValue ? chainCount - 1 : 0];
        var settings = new ChainSettings(8f, 3f, 1f, .85f, MathF.PI * .65f, .02f);
        var segment = new Vector3(0, -.18f, 0);
        Vector3 anchorPosition = onHip ? hip : knee;
        for (int chain = 0; chain < chains.Length; chain++)
        {
            int first = HostBones + chain * chainLength;
            anchorChildren.Add(first);
            var joints = new string[chainLength];
            Vector3 root = firstRoot + Vector3.UnitX * (chainSpacing * chain);
            for (int joint = 0; joint < joints.Length; joint++)
            {
                int node = first + joint;
                joints[joint] = $"{name}-{chain}-{joint}";
                glb.Nodes[node]!["name"] = joints[joint];
                glb.Nodes[node]!["translation"] = Translation(joint == 0 ? root : segment);
                if (joint + 1 < joints.Length) glb.Nodes[node]!["children"] = new JsonArray(node + 1);
                glb.SetInverseBind(node, Matrix4x4.CreateTranslation(-(anchorPosition + root + joint * segment)));
            }
            chains[chain] = new ChainDefinition(joints, segment, settingsForChain?.Invoke(chain) ?? settings);
        }
        for (int link = 0; link < links.Length; link++) links[link] = new ChainLink(link, link + 1);

        // The shared triangle needs only three representative weights. The solver steps every declared joint.
        for (int vertex = 0; vertex < 3; vertex++)
        {
            int chain = vertex * (chainCount - 1) / 2;
            int lastJoint = HostBones + (chain + 1) * chainLength - 1;
            glb.SetElement(1, vertex, lastJoint, 0, 0, 0);
            glb.SetElement(2, vertex, 1, 0, 0, 0);
        }

        var part = new SkinnedAttachmentPartDefinition(1, rig.Source.Mesh(name + ".glb", glb.Build()))
        {
            Regions = [new NamedRegionRun("alpha", 1)],
            Motion = new ChainMotionDefinition(chains, links) { LinkStretch = linkStretch },
        };
        var loaded = rig.Load(name + ".json", new AttachmentSetDefinition([part], layer, [], colliders ?? []));
        return new SwingCostPiece(loaded, HostBones + chainCount * chainLength);
    }

    static JsonArray Translation(Vector3 position) => new(position.X, position.Y, position.Z);

    // OutfitRig's synthetic inverse binds are pure translations.
    internal static Vector3 ModelBindPosition(OutfitRig rig, string joint) =>
        -rig.Body.InverseBind[rig.Map.Bone(joint)].Translation;

    internal static LoadedBodyColliders LoadColliders(OutfitRig rig)
    {
        const string key = "body-colliders.json";
        var capsules = new ColliderDefinition[BodyColliders];
        for (int index = 0; index < capsules.Length; index++)
        {
            string joint = index < BodyColliders / 2 ? "hip" : "knee";
            int offset = index % (BodyColliders / 2);
            Vector3 center = ModelBindPosition(rig, joint) + new Vector3((offset % 3 - 1) * .18f, -.16f,
                offset < 3 ? -.08f : .08f);
            Vector3 halfAxis = Vector3.UnitY * .14f;
            capsules[index] = new ColliderDefinition(joint, center - halfAxis, center + halfAxis, .10f);
        }
        var body = new BodyColliderSet(OutfitRig.Rig, capsules);
        return rig.Cache.GetBodyColliders(key, rig.Source.Add(key, BodyCollidersJson.Write(body)), OutfitRig.Rig, rig.Body);
    }
}
