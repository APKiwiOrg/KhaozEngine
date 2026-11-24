using System;
using System.Numerics;
using KhaozEngine.Attachments;

namespace KhaozEngine.Tests.Attachments;

/// <summary>Five linked cape panels and four independent strap tails on a synthetic running anchor.</summary>
internal static class ChainSwingFixture
{
    internal const int CapeChains = 5;
    internal const int StopTime = 2;
    internal const float SwayFade = 0.25f;

    internal static ChainRig CreateRig(float? stretch = null, bool wideLimits = false, int capeJoints = 5, bool layered = false)
    {
        const int strapChains = 4;
        var cape = new ChainSettings(25, 12, 1, 0.16f, wideLimits ? MathF.PI : 0.16f, 0.015f);
        var strap = new ChainSettings(10, 5, 1, 0.6f, 0.9f, 0.01f);
        var chains = new ChainRigChain[CapeChains + strapChains];
        var links = new ChainLink[CapeChains - 1];
        for (int chain = 0; chain < CapeChains; chain++)
            chains[chain] = CreateChain(capeJoints, new((chain - 2) * 0.18f, 0, -0.2f), -0.2f, cape)
                with { Layer = layered ? AttachmentLayer.Outerwear : null };
        for (int chain = 0; chain < strapChains; chain++)
            chains[CapeChains + chain] = CreateChain(3, new((chain - 1.5f) * 0.12f, -0.15f, 0.2f), -0.12f, strap)
                with { Layer = layered ? AttachmentLayer.Armour : null };
        for (int link = 0; link < links.Length; link++)
            links[link] = new ChainLink(link, link + 1) { Stretch = stretch };
        return new ChainRig(1, chains, links);
    }

    internal static (Matrix4x4 Anchor, Matrix4x4 Wearer) Pose(float seconds)
    {
        const float speed = 3;
        float stride = MathF.Tau * 1.7f * seconds;
        float sway = 1 - Math.Clamp((seconds - StopTime) / SwayFade, 0, 1);
        var wearer = Matrix4x4.CreateTranslation(0, 0, speed * MathF.Min(seconds, StopTime));
        var anchor = Matrix4x4.CreateRotationX(sway * 0.32f * MathF.Sin(stride)) *
            Matrix4x4.CreateRotationY(sway * 0.48f * MathF.Sin(stride + 0.4f)) * wearer;
        return (anchor, wearer);
    }

    private static ChainRigChain CreateChain(int count, Vector3 root, float segmentLength, ChainSettings settings)
    {
        var segment = new Vector3(0, segmentLength, 0);
        var joints = new ChainJointRest[count];
        joints[0] = new ChainJointRest(root, Quaternion.Identity, Vector3.One);
        for (int joint = 1; joint < count; joint++)
            joints[joint] = new ChainJointRest(segment, Quaternion.Identity, Vector3.One);
        return new ChainRigChain(0, joints, segment, settings);
    }
}
