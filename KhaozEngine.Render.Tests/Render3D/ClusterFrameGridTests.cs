using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class ClusterFrameGridTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void InvalidViewProjectionMarksTheGridInvalid(int invalidKind)
    {
        var grid = new ClusterFrameGrid();
        Matrix4x4 projection = Matrix4x4.CreateOrthographic(32f, 18f, 1f, 25f);
        Assert.True(grid.TryPrepare(projection, Vector3.Zero, -Vector3.UnitZ, projection, Vector3.Zero));
        Matrix4x4 viewProjection = projection;
        Vector3 eye = Vector3.Zero;
        Vector3 forward = -Vector3.UnitZ;
        Vector3 origin = Vector3.Zero;
        if (invalidKind == 0) viewProjection = default;
        if (invalidKind == 1) viewProjection.M11 = float.NaN;
        if (invalidKind == 2) forward = Vector3.Zero;
        if (invalidKind == 3) projection = default;
        if (invalidKind == 4) viewProjection = Matrix4x4.Identity;
        if (invalidKind == 5) eye.X = float.PositiveInfinity;
        if (invalidKind == 6) origin.Y = float.NaN;

        Assert.False(grid.TryPrepare(viewProjection, eye, forward, projection, origin));

        Assert.False(grid.IsValid);
        Assert.Equal(new Vector4(0f, 0f, 0f, -1f), grid.Depth);
        Assert.Equal(Vector4.Zero, grid.CameraForward);
    }

    [Theory]
    [InlineData(true, 1f)]
    [InlineData(false, 0f)]
    public void PerspectiveAndOrthographicSetDepthW(bool perspective, float expectedW)
    {
        var grid = new ClusterFrameGrid();
        Matrix4x4 projection = perspective
            ? Matrix4x4.CreatePerspectiveFieldOfView(1.1f, 16f / 9f, 0.5f, 512f)
            : Matrix4x4.CreateOrthographic(48f, 27f, -2f, 190f);

        Assert.True(grid.TryPrepare(projection, Vector3.Zero, -Vector3.UnitZ, projection, Vector3.Zero));

        Assert.True(grid.IsValid);
        Assert.Equal(expectedW, grid.Depth.W);
        Assert.Equal(170, grid.ClipNear.Length);
        Assert.Equal(170, grid.ClipFar.Length);
        Assert.Equal(25, grid.SliceDepth.Length);
        Assert.True(float.IsFinite(grid.GeometryScale));
        Assert.True(grid.GeometryScale > 0f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void GridMatchesTheBuilderItReplaced(int kind)
    {
        var random = new Random(1588 + kind);
        Vector3 eye = new(random.Next(-20, 21), random.Next(-20, 21), random.Next(-20, 21));
        Vector3 forward = new(random.Next(-8, 9), random.Next(-4, 5), -12f);
        Vector3 origin = kind == 2 ? new Vector3(4_194_304f, -4_194_304f, 4_194_304f) : Vector3.Zero;
        Matrix4x4 projection = kind == 1
            ? Matrix4x4.CreateOrthographic(48f, 27f, -2f, 190f)
            : Matrix4x4.CreatePerspectiveFieldOfView(1.1f, 16f / 9f, 0.5f, 512f);
        Matrix4x4 viewProjection = Matrix4x4.CreateLookAt(eye, eye + forward, Vector3.UnitY) * projection;
        if (kind == 2) viewProjection *= Matrix4x4.CreateScale(1f, -1f, 1f);
        var grid = new ClusterFrameGrid();

        Assert.True(grid.TryPrepare(viewProjection, eye, forward, projection, origin));

        // Captured from PointLightClusterBuilder at ddca81618 before extracting the grid.
        Vector4 expectedDepth = ExpectedDepth(kind);
        Vector4 expectedForward = ExpectedForward(kind);
        float[] expectedSlices = ExpectedSlices(kind);
        AssertClose(expectedDepth.X, grid.Depth.X);
        AssertClose(expectedDepth.Y, grid.Depth.Y);
        AssertClose(expectedDepth.Z, grid.Depth.Z);
        Assert.Equal(expectedDepth.W, grid.Depth.W);
        AssertClose(expectedForward.X, grid.CameraForward.X);
        AssertClose(expectedForward.Y, grid.CameraForward.Y);
        AssertClose(expectedForward.Z, grid.CameraForward.Z);
        Assert.Equal(expectedForward.W, grid.CameraForward.W);
        Assert.Equal(expectedSlices.Length, grid.SliceDepth.Length);
        for (int slice = 0; slice < expectedSlices.Length; slice++)
            AssertClose(expectedSlices[slice], grid.SliceDepth[slice]);
    }

    // The tolerance allows float math to round differently on the supported CPU architectures.
    static void AssertClose(float expected, float actual) =>
        Assert.InRange(actual, expected - MathF.Max(1e-6f, MathF.Abs(expected) * 1e-5f),
            expected + MathF.Max(1e-6f, MathF.Abs(expected) * 1e-5f));

    static Vector4 ExpectedDepth(int kind) => kind switch
    {
        0 => new Vector4(0.50000024f, 512.00024f, 6.931472f, 1f),
        1 => new Vector4(-2.000001f, 190f, 0f, 0f),
        2 => new Vector4(0.49999654f, 511.93616f, 6.931354f, 1f),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    static Vector4 ExpectedForward(int kind) => kind switch
    {
        0 => new Vector4(0f, -0.24253562f, -0.9701425f, 0f),
        1 => new Vector4(0.31524417f, 0.07881104f, -0.9457325f, 0f),
        2 => new Vector4(0.48420015f, -0.2766858f, -0.8300574f, 0f),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    static float[] ExpectedSlices(int kind) => kind switch
    {
        0 =>
        [
            0.50000024f, 0.6674202f, 0.8908991f, 1.1892077f, 1.5874017f,
            2.1189272f, 2.8284285f, 3.7754986f, 5.0396867f, 6.727174f,
            8.9797f, 11.986464f, 16.000008f, 21.35745f, 28.508766f,
            38.054653f, 50.796856f, 67.805664f, 90.5097f, 120.81599f,
            161.26997f, 215.26956f, 287.3505f, 383.56665f, 512.00024f,
        ],
        1 =>
        [
            -2.000001f, 5.999999f, 13.999999f, 22f, 30f,
            38f, 46f, 54f, 62f, 70f,
            78f, 86f, 94f, 102f, 110f,
            118f, 126f, 134f, 142f, 150f,
            158f, 166f, 174f, 182f, 190f,
        ],
        2 =>
        [
            0.49999654f, 0.6674121f, 0.89088386f, 1.1891813f, 1.5873591f,
            2.1188595f, 2.8283243f, 3.7753413f, 5.0394526f, 6.726827f,
            8.979194f, 11.985726f, 15.998947f, 21.355928f, 28.5066f,
            38.051556f, 50.79251f, 67.79951f, 90.50104f, 120.8038f,
            161.25294f, 215.24579f, 287.31726f, 383.52066f, 511.93616f,
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
