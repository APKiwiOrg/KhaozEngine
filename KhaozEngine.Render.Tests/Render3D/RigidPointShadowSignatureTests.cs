using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class RigidPointShadowSignatureTests
{
    [Fact]
    public void MovingMeshReplacementCannotReorderTheCachedStaticSignature()
    {
        using var rig = new RigidPointShadowRig();
        MeshHandle second = rig.Scene.LoadMesh(MeshPrimitives.Box(0.75f));
        MeshHandle replacement = rig.Scene.LoadMesh(MeshPrimitives.Box(0.25f));
        ShadowPassDiagnostics Frame(MeshHandle actor)
        {
            rig.Scene.Begin();
            rig.Scene.Draw(new RigidInstanceDraw(actor, Matrix4x4.Identity)
            { ShadowMobility = ShadowCasterMobility.Dynamic });
            rig.Scene.Draw(second, Matrix4x4.CreateTranslation(2f, 0f, 0f));
            rig.Scene.Draw(rig.Box, Matrix4x4.CreateTranslation(-2f, 0f, 0f));
            rig.Scene.AddLight(new Vector3(0f, 2f, 0f), Color.White, 10f, 1f, LightShadow.Static(7));
            return rig.Render();
        }
        Frame(rig.Box);
        Frame(rig.Box);
        Frame(rig.Box);

        ShadowPassDiagnostics current = Frame(replacement);

        Assert.Equal(0, current.PointStaticRebuilds);
        Assert.Equal(0, current.PointRigidTransitionRowsRendered);
        Assert.Equal(6, current.PointFaceDrawCalls);
    }
    [Fact]
    public void StaticMeshReplacementAndCastOptOutStillRebuildCachedRows()
    {
        using var rig = new RigidPointShadowRig();
        MeshHandle replacement = rig.Scene.LoadMesh(MeshPrimitives.Box(0.75f));
        rig.Frame(mobility: ShadowCasterMobility.Static);
        rig.Frame(mobility: ShadowCasterMobility.Static);

        ShadowPassDiagnostics replaced = rig.Frame(mobility: ShadowCasterMobility.Static, actorMesh: replacement);
        Assert.Equal(2, replaced.PointStaticRebuilds);
        Assert.Equal(0, replaced.PointRigidTransitionRowsRendered);

        ShadowPassDiagnostics optedOut = rig.Frame(mobility: ShadowCasterMobility.Static,
            actorMesh: replacement, casts: false);
        Assert.Equal(2, optedOut.PointStaticRebuilds);
    }

    [Fact]
    public void AddingDuplicateStaticCastersDoesNotCancelTheirSignatureContribution()
    {
        using var rig = new RigidPointShadowRig();
        ShadowPassDiagnostics Frame(int copies)
        {
            rig.Scene.Begin();
            for (int i = 0; i < copies; i++) rig.Scene.Draw(rig.Box, Matrix4x4.Identity);
            rig.Scene.AddLight(new Vector3(0f, 2f, 0f), Color.White, 10f, 1f, LightShadow.Static(7));
            return rig.Render();
        }
        Frame(1);
        Frame(1);

        Assert.Equal(1, Frame(3).PointStaticRebuilds);
        Assert.Equal(0, Frame(3).PointStaticRebuilds);
        Assert.Equal(1, Frame(1).PointStaticRebuilds);
    }

}
