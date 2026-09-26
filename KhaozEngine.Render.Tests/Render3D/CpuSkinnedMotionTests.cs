using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]   // one case is a zero-allocation reading (#264)
public sealed class CpuSkinnedMotionTests
{
    static SkinnedGltfMesh Tube() => SkinnedMeshBuilder.BuildTube(0.25f, 2f, 4, 6, 3, Axis.Z);

    static Matrix4x4[] Composed(SkinnedGltfMesh mesh, float bend)
    {
        var palette = new Matrix4x4[mesh.BoneCount];
        for (int b = 0; b < palette.Length; b++)
            palette[b] = SkinningMath.Compose(Matrix4x4.CreateRotationY(bend * b) * mesh.RestPose[b], mesh.InverseBind[b]);
        return palette;
    }

    [Fact]
    public void ReSkinningLastFramesPaletteReproducesLastFramesPositionsExactly()
    {
        SkinnedGltfMesh mesh = Tube();
        Matrix4x4[] lastPalette = Composed(mesh, .3f);
        Matrix4x4 lastWorld = Matrix4x4.CreateRotationY(.2f) * Matrix4x4.CreateTranslation(4f, 0f, -2f);

        var lastFrame = new List<Vector3>();
        foreach (SkinnedVertex v in mesh.Vertices)
            lastFrame.Add(Vector3.Transform(SkinningMath.SkinVertex(v, lastPalette).Position, lastWorld));
        var previous = new List<Vector3>();
        CpuSkinnedMotion.AppendPrevious(mesh.Vertices, lastPalette, lastWorld, previous);

        Assert.Equal(lastFrame, previous);   // bit for bit: the same blend and the same two transforms
    }

    [Fact]
    public void AppendCurrentPlacesThisFramesVerticesInTheWorld()
    {
        var skinned = new[] { new ModelVertex(new Vector3(1f, 2f, 3f), Vector3.UnitY, Vector4.One) };
        var positions = new List<Vector3> { Vector3.One };

        CpuSkinnedMotion.AppendCurrent(skinned, Matrix4x4.CreateTranslation(10f, 0f, 0f), positions);

        Assert.Equal(new[] { Vector3.One, new Vector3(11f, 2f, 3f) }, positions);
    }

    [Fact]
    public void ASteadyFrameOfPreviousPositionsAllocatesNothing()
    {
        SkinnedGltfMesh mesh = Tube();
        Matrix4x4[] palette = Composed(mesh, .1f);
        var positions = new List<Vector3>();
        for (int i = 0; i < 4; i++) { positions.Clear(); CpuSkinnedMotion.AppendPrevious(mesh.Vertices, palette, Matrix4x4.Identity, positions); }

        AllocAssert.NoPerCallAllocation("20 frames of CPU-skinned previous positions", () =>
        {
            for (int i = 0; i < 20; i++)
            {
                positions.Clear();
                CpuSkinnedMotion.AppendPrevious(mesh.Vertices, palette, Matrix4x4.Identity, positions);
            }
        });
    }
}
