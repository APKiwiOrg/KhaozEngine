using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Coverage through the fragment's cluster lookup, independent of the binner's box and plane tests.</summary>
[Collection("AllocSensitive")]
public sealed class WaterVolumeClusterBuilderTests
{
    const int ClusterCount = 16 * 9 * 24;
    const int RecordStart = ClusterCount * 33;
    static readonly Vector3 Forward = Vector3.Normalize(new Vector3(-0.15f, -0.07f, -1f));

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void EveryPointInsideAVolumeFindsItsRecord(bool perspective, bool distantOrigin)
    {
        Vector3 origin = distantOrigin ? new Vector3(4194304f, 0f, -4194304f) : Vector3.Zero;
        var random = new Random(158803);
        ClusterFrameGrid grid = Grid(perspective, out Matrix4x4 viewProjection, origin);
        Matrix4x4.Invert(Matrix4x4.CreateLookAt(grid.EyeRender, grid.EyeRender + Forward, Vector3.UnitY),
            out Matrix4x4 inverseView);
        var records = new WaterVolumeRecord[128];
        for (int i = 0; i < records.Length; i++)
        {
            float depth = Between(random, 4f, 170f);
            float width = perspective ? depth * 0.65f : 12f;
            Vector3 center = Vector3.Transform(new Vector3(Between(random, -width, width),
                Between(random, -width * 0.5f, width * 0.5f), -depth), inverseView);
            Vector3 half = new(Between(random, 0.5f, 8f), Between(random, 0.5f, 8f), Between(random, 0.5f, 8f));
            // Queue-time world-to-render subtraction, including quantization at the distant origin.
            Vector3 min = center - half + origin - origin;
            Vector3 max = center + half + origin - origin;
            records[i] = Box(min, max);
            if (i % 13 == 0) records[i] = records[i] with { FloorY = float.MinValue };
        }
        var builder = new WaterVolumeClusterBuilder();
        builder.Build(records, grid);
        int checkedPoints = 0;
        for (int attempt = 0; checkedPoints < 2000 && attempt < 20000; attempt++)
        {
            int record = random.Next(records.Length);
            WaterVolumeRecord box = records[record];
            Vector3 point = new(Between(random, box.MinX, box.MaxX),
                Between(random, MathF.Max(box.FloorY, box.SurfaceY - 16f), box.SurfaceY),
                Between(random, box.MinZ, box.MaxZ));
            point = point + origin - origin;
            if (!Contains(box, point) || !ClusterForFragment(point, viewProjection, grid, out int cluster)) continue;
            AssertListed(builder, cluster, (uint)record);
            checkedPoints++;
        }
        Assert.Equal(2000, checkedPoints);
        Assert.True(builder.OverflowedClusters < ClusterCount / 20);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HugeOceanPlusManySmallRectsStaysConservativeAndBounded(bool perspective)
    {
        ClusterFrameGrid grid = Grid(perspective, out Matrix4x4 viewProjection, Vector3.Zero);
        var random = new Random(300);
        var records = new WaterVolumeRecord[301];
        records[0] = Box(new Vector3(-50000f, -100f, -50000f), new Vector3(50000f, 2f, 50000f))
            with { FloorY = float.MinValue };
        for (int i = 1; i < records.Length; i++)
        {
            float depth = Between(random, 5f, 170f);
            float width = perspective ? depth * 0.55f : 12f;
            Vector3 center = new(Between(random, -width, width), Between(random, -6f, 1f), -depth);
            records[i] = Box(center - new Vector3(0.4f), center + new Vector3(0.4f));
        }
        var builder = new WaterVolumeClusterBuilder();
        builder.Build(records, grid);
        int checkedPoints = 0;
        for (int record = 0; record < records.Length; record++)
        {
            WaterVolumeRecord box = records[record];
            for (int sample = 0; sample < 12; sample++)
            {
                Vector3 point = record == 0
                    ? new Vector3(Between(random, -12f, 12f), Between(random, -8f, 1f), Between(random, -170f, -5f))
                    : new Vector3(Between(random, box.MinX, box.MaxX), Between(random, box.FloorY, box.SurfaceY),
                        Between(random, box.MinZ, box.MaxZ));
                if (!ClusterForFragment(point, viewProjection, grid, out int cluster)) continue;
                AssertListed(builder, cluster, (uint)record);
                checkedPoints++;
            }
        }
        Assert.True(checkedPoints > 2000);
        AssertDiagnostics(builder);
        Assert.True(builder.OverflowedClusters < ClusterCount * 0.05f,
            $"{builder.OverflowedClusters} of {ClusterCount} clusters overflowed");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DistantFarPlaneDoesNotInflateNearbyRiverClusters(bool unboundedFloor, bool pitchedCamera)
    {
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2f, 16f / 9f, 1f, 100000f);
        Vector3 forward = pitchedCamera ? Vector3.Normalize(new Vector3(0f, -0.1f, -1f)) : -Vector3.UnitZ;
        Matrix4x4 viewProjection = Matrix4x4.CreateLookAt(Vector3.Zero, forward, Vector3.UnitY) * projection;
        var grid = new ClusterFrameGrid();
        Assert.True(grid.TryPrepare(viewProjection, Vector3.Zero, forward, projection, Vector3.Zero));
        var records = new WaterVolumeRecord[301];
        records[0] = Box(new Vector3(-50000f, -100f, -50000f), new Vector3(50000f, 2f, 50000f))
            with { FloorY = float.MinValue };
        for (int i = 0; i < 300; i++)
        {
            float x = (i % 30 - 14.5f) * 0.2f;
            float z = -2f - i / 30;
            records[i + 1] = Box(new Vector3(x - 0.04f, -1.2f, z - 0.04f),
                new Vector3(x + 0.04f, 0.2f, z + 0.04f));
            if (unboundedFloor) records[i + 1] = records[i + 1] with { FloorY = float.MinValue };
        }
        var builder = new WaterVolumeClusterBuilder();
        builder.Build(records, grid);
        for (int record = 1; record < records.Length; record++)
        {
            WaterVolumeRecord box = records[record];
            Vector3 point = new((box.MinX + box.MaxX) * 0.5f, -0.5f, (box.MinZ + box.MaxZ) * 0.5f);
            Assert.True(ClusterForFragment(point, viewProjection, grid, out int cluster));
            AssertListed(builder, cluster, (uint)record);
        }
        AssertDiagnostics(builder);
        Assert.True(builder.OverflowedClusters < ClusterCount * 0.05f,
            $"{builder.OverflowedClusters} of {ClusterCount} clusters overflowed with a distant far plane");
    }

    [Fact]
    public void StackedVolumesAreBothListed()
    {
        ClusterFrameGrid grid = OrthoGrid(out Matrix4x4 projection);
        WaterVolumeRecord[] records =
        [
            Box(new Vector3(0f, 0.5f, -3f), new Vector3(2f, 4f, -1f)),
            Box(new Vector3(0f, -4f, -3f), new Vector3(2f, -0.5f, -1f)),
        ];
        var builder = new WaterVolumeClusterBuilder();
        builder.Build(records, grid);
        Assert.True(ClusterForFragment(new Vector3(1f, 0f, -2f), projection, grid, out int cluster));
        Assert.Equal(new uint[] { 0, 1 }, Listed(builder, cluster, out bool overflow).ToArray());
        Assert.False(overflow);
    }

    [Fact]
    public void RecordsPastTheCapAreDroppedInOrder()
    {
        ClusterFrameGrid grid = OrthoGrid(out _);
        var records = new WaterVolumeRecord[4100];
        for (int i = 0; i < records.Length; i++)
            records[i] = Box(new Vector3(1000f + i, 0f, -3f), new Vector3(1001f + i, 1f, -2f));
        records[4095] = Box(new Vector3(0f, 0f, -3f), new Vector3(2f, 1f, -2f));
        for (int i = 4096; i < records.Length; i++) records[i] = records[4095];
        var builder = new WaterVolumeClusterBuilder();
        builder.Build(records, grid);
        Assert.Equal(4096, builder.RecordCount);
        Assert.Equal(4, builder.DroppedRecords);
        Assert.Equal(RecordStart + 4096 * 12, builder.UsedUIntCount);
        Assert.Equal(0f, ReadFloat(builder, 4095, 0));
        Assert.Equal(0, builder.OverflowedClusters);
        bool lastWasListed = false;
        for (int cluster = 0; cluster < ClusterCount; cluster++)
        {
            foreach (uint record in Listed(builder, cluster, out _))
            {
                Assert.InRange(record, 0u, 4095u);
                lastWasListed |= record == 4095;
            }
        }
        Assert.True(lastWasListed);
    }

    [Fact]
    public void PackedRecordsPreserveAllFieldsAndUnboundedFloor()
    {
        WaterVolumeRecord record = new(-2f, -8f, 3f, -1f, 4f, float.MinValue, new Vector3(0.1f, 0.2f, 0.3f), 0.03f);
        var builder = new WaterVolumeClusterBuilder();
        builder.Build([record], OrthoGrid(out _));
        float[] expected = [-2f, -8f, 3f, -1f, 4f, float.MinValue, 0.1f, 0.2f, 0.3f, 0.03f, 0f, 0f];
        for (int word = 0; word < expected.Length; word++) Assert.Equal(expected[word], ReadFloat(builder, 0, word));
        Assert.Equal(RecordStart + 12, builder.UsedUIntCount);
        Assert.Equal(WaterVolumeClusterBuilder.ImageUIntCount, builder.Image.Length);
    }

    [Theory]
    [InlineData(32, false)]
    [InlineData(33, true)]
    public void CapacityPressurePreservesSubmissionOrderOrFlagsFullScan(int count, bool expectedOverflow)
    {
        ClusterFrameGrid grid = OrthoGrid(out Matrix4x4 projection);
        var records = new WaterVolumeRecord[count];
        Array.Fill(records, Box(new Vector3(-1f, -1f, -3f), new Vector3(1f, 1f, -1f)));
        var builder = new WaterVolumeClusterBuilder();
        builder.Build(records, grid);
        Assert.True(ClusterForFragment(new Vector3(0f, 0f, -2f), projection, grid, out int cluster));
        ReadOnlySpan<uint> listed = Listed(builder, cluster, out bool overflow);
        Assert.Equal(expectedOverflow, overflow);
        if (overflow) Assert.True(builder.OverflowedClusters > 0);
        else
        {
            Assert.Equal(32, listed.Length);
            for (int i = 0; i < listed.Length; i++) Assert.Equal((uint)i, listed[i]);
            Assert.True(builder.ReferenceCount >= 32);
        }
        AssertDiagnostics(builder);
    }

    [Fact]
    public void EmptyInputBuildsNothing()
    {
        var builder = new WaterVolumeClusterBuilder();
        ClusterFrameGrid grid = OrthoGrid(out _);
        builder.Build([Box(new Vector3(-1f, -1f, -3f), new Vector3(1f, 1f, -1f))], grid);
        builder.Build([], grid);
        AssertEmpty(builder);
    }

    [Fact]
    public void WarmBuildAllocatesNothing()
    {
        var builder = new WaterVolumeClusterBuilder();
        ClusterFrameGrid grid = OrthoGrid(out _);
        WaterVolumeRecord[] records = [Box(new Vector3(-1f, -1f, -3f), new Vector3(1f, 1f, -1f))];
        builder.Build(records, grid);
        AllocAssert.NoPerCallAllocation("second water volume build", () => builder.Build(records, grid));
    }

    [Fact]
    public void InvalidGridBuildsNothing()
    {
        var builder = new WaterVolumeClusterBuilder();
        WaterVolumeRecord[] records = [Box(new Vector3(-1f, -1f, -3f), new Vector3(1f, 1f, -1f))];
        builder.Build(records, OrthoGrid(out _));
        builder.Build(records, new ClusterFrameGrid());
        AssertEmpty(builder);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(25f)]
    [InlineData(0.999f)]
    [InlineData(25.001f)]
    public void DepthBoundaryEpsilonAndClampingKeepTheRecord(float depth)
    {
        ClusterFrameGrid grid = OrthoGrid(out Matrix4x4 projection);
        var point = new Vector3(0f, 0f, -depth);
        var builder = new WaterVolumeClusterBuilder();
        builder.Build([Box(point - new Vector3(0.0001f), point + new Vector3(0.0001f))], grid);
        Assert.True(ClusterForFragment(point, projection, grid, out int cluster));
        AssertListed(builder, cluster, 0);
    }

    [Theory]
    [InlineData(-16.00008f, 0f)]
    [InlineData(16.00008f, 0f)]
    [InlineData(0f, -9.00004f)]
    [InlineData(0f, 9.00004f)]
    public void ScreenBoundaryEpsilonAndClampingKeepTheRecord(float x, float y)
    {
        ClusterFrameGrid grid = OrthoGrid(out Matrix4x4 projection);
        var point = new Vector3(x, y, -2f);
        var builder = new WaterVolumeClusterBuilder();
        builder.Build([Box(point - new Vector3(0.00001f), point + new Vector3(0.00001f))], grid);
        Assert.True(ClusterForFragment(point, projection, grid, out int cluster));
        AssertListed(builder, cluster, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DepthToleranceWithADiagonalCameraKeepsTheRecord(bool atFarPlane)
    {
        Vector3 forward = Vector3.Normalize(new Vector3(-1f));
        Matrix4x4 projection = Matrix4x4.CreateOrthographic(4f, 4f, 1f, 25f);
        Matrix4x4 viewProjection = Matrix4x4.CreateLookAt(Vector3.Zero, forward, Vector3.UnitY) * projection;
        var grid = new ClusterFrameGrid();
        Assert.True(grid.TryPrepare(viewProjection, Vector3.Zero, forward, projection, Vector3.Zero));
        Vector3 point = forward * (atFarPlane ? grid.Depth.Y + 0.0022f : grid.Depth.X - 0.0022f);
        var builder = new WaterVolumeClusterBuilder();
        builder.Build([Box(point - new Vector3(0.00001f), point + new Vector3(0.00001f))], grid);
        Assert.True(ClusterForFragment(point, viewProjection, grid, out int cluster));
        AssertListed(builder, cluster, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DepthToleranceFollowsPerspectiveTileRays(bool atFarPlane)
    {
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2f, 16f / 9f, 1f, 25f);
        var grid = new ClusterFrameGrid();
        Assert.True(grid.TryPrepare(projection, Vector3.Zero, -Vector3.UnitZ, projection, Vector3.Zero));
        float depth = atFarPlane ? grid.Depth.Y + 0.0022f : grid.Depth.X - 0.0022f;
        float ndcX = atFarPlane ? -1f : -0.87501f;
        var point = new Vector3(ndcX * depth * (16f / 9f), 0f, -depth);
        var builder = new WaterVolumeClusterBuilder();
        builder.Build([Box(point - new Vector3(0.00001f), point + new Vector3(0.00001f))], grid);
        Assert.True(ClusterForFragment(point, projection, grid, out int cluster));
        AssertListed(builder, cluster, 0);
    }

    [Fact]
    public void SparseBuildAfterOverflowMatchesFreshImage()
    {
        var builder = new WaterVolumeClusterBuilder();
        var fresh = new WaterVolumeClusterBuilder();
        ClusterFrameGrid grid = OrthoGrid(out _);
        var dense = new WaterVolumeRecord[33];
        Array.Fill(dense, Box(new Vector3(-100f), new Vector3(100f)));
        WaterVolumeRecord[] sparse = [Box(new Vector3(0f, -1f, -3f), new Vector3(2f, 1f, -1f))];
        builder.Build(dense, grid);
        builder.Build(sparse, grid);
        fresh.Build(sparse, grid);
        Assert.Equal(fresh.Image[..fresh.UsedUIntCount], builder.Image[..builder.UsedUIntCount]);
        Assert.Equal(fresh.ReferenceCount, builder.ReferenceCount);
        Assert.Equal(0, builder.OverflowedClusters);
    }

    static void AssertEmpty(WaterVolumeClusterBuilder builder)
    {
        Assert.Equal(0, builder.RecordCount);
        Assert.Equal(0, builder.UsedUIntCount);
        Assert.Equal(0, builder.DroppedRecords);
        Assert.Equal(0, builder.OverflowedClusters);
        Assert.Equal(0, builder.ReferenceCount);
    }

    static void AssertDiagnostics(WaterVolumeClusterBuilder builder)
    {
        int overflows = 0;
        int references = 0;
        for (int cluster = 0; cluster < ClusterCount; cluster++)
        {
            ReadOnlySpan<uint> listed = Listed(builder, cluster, out bool overflow);
            if (overflow) overflows++;
            references += listed.Length;
            for (int index = 0; index < listed.Length; index++)
            {
                Assert.True(listed[index] < builder.RecordCount);
                if (index > 0) Assert.True(listed[index - 1] < listed[index]);
            }
        }
        Assert.Equal(overflows, builder.OverflowedClusters);
        Assert.Equal(references, builder.ReferenceCount);
    }

    static float ReadFloat(WaterVolumeClusterBuilder builder, int record, int word) =>
        BitConverter.UInt32BitsToSingle(builder.Image[RecordStart + record * 12 + word]);

    static ReadOnlySpan<uint> Listed(WaterVolumeClusterBuilder builder, int cluster, out bool overflow)
    {
        uint count = builder.Image[cluster];
        overflow = count == 255u;
        if (overflow) return [];
        Assert.InRange(count, 0u, 32u);
        int first = ClusterCount + cluster * 32;
        Assert.True(first + count <= builder.UsedUIntCount);
        return builder.Image.AsSpan(first, (int)count);
    }

    static void AssertListed(WaterVolumeClusterBuilder builder, int cluster, uint record)
    {
        ReadOnlySpan<uint> listed = Listed(builder, cluster, out bool overflow);
        Assert.True(overflow || listed.Contains(record), $"cluster {cluster} omitted record {record}");
    }

    static bool Contains(WaterVolumeRecord box, Vector3 point) => point.X >= box.MinX && point.X < box.MaxX
        && point.Z >= box.MinZ && point.Z < box.MaxZ && point.Y >= box.FloorY && point.Y < box.SurfaceY;

    static WaterVolumeRecord Box(Vector3 min, Vector3 max) =>
        new(min.X, min.Z, max.X, max.Z, max.Y, min.Y, new Vector3(0.1f, 0.2f, 0.3f), 0.03f);

    static float Between(Random random, float min, float max) => min + (max - min) * random.NextSingle();

    static ClusterFrameGrid Grid(bool perspective, out Matrix4x4 viewProjection, Vector3 origin)
    {
        Matrix4x4 projection = perspective
            ? Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2f, 16f / 9f, 1f, 200f)
            : Matrix4x4.CreateOrthographic(48f, 27f, -2f, 200f);
        // An asymmetric raster projection exercises the same jittered boundary grid as receivers.
        if (perspective) { projection.M31 = 0.007f; projection.M32 = -0.003f; }
        else { projection.M41 = 0.007f; projection.M42 = -0.003f; }
        Vector3 eye = new Vector3(3f, 2f, 5f) + origin - origin;
        viewProjection = Matrix4x4.CreateLookAt(eye, eye + Forward, Vector3.UnitY) * projection;
        var grid = new ClusterFrameGrid();
        Assert.True(grid.TryPrepare(viewProjection, eye, Forward, projection, origin));
        return grid;
    }

    static ClusterFrameGrid OrthoGrid(out Matrix4x4 projection)
    {
        projection = Matrix4x4.CreateOrthographic(32f, 18f, 1f, 25f);
        var grid = new ClusterFrameGrid();
        Assert.True(grid.TryPrepare(projection, Vector3.Zero, -Vector3.UnitZ, projection, Vector3.Zero));
        return grid;
    }

    // Literal C# port of pointLightClusterForFragment in ShaderSources.Lighting.cs, including epsilon and clamping.
    static bool ClusterForFragment(Vector3 point, Matrix4x4 viewProjection, ClusterFrameGrid grid, out int cluster)
    {
        cluster = 0;
        Vector4 parameters = grid.Depth;
        if (parameters.W < -0.5f) return false;
        Vector4 clip = Vector4.Transform(new Vector4(point, 1f), viewProjection);
        if (!Finite(clip) || clip.W <= 0f) return false;
        Vector2 ndc = new(clip.X / clip.W, clip.Y / clip.W);
        if (!float.IsFinite(ndc.X) || !float.IsFinite(ndc.Y)) return false;
        const float ndcEpsilon = 1e-5f;
        if (ndc.X < -1f - ndcEpsilon || ndc.X > 1f + ndcEpsilon
            || ndc.Y < -1f - ndcEpsilon || ndc.Y > 1f + ndcEpsilon) return false;
        float depth = Vector3.Dot(point - grid.EyeRender, new Vector3(grid.CameraForward.X,
            grid.CameraForward.Y, grid.CameraForward.Z));
        if (!float.IsFinite(depth)) return false;
        float depthEpsilon = 1e-4f * MathF.Max(1f, MathF.Max(MathF.Abs(parameters.X), MathF.Abs(parameters.Y)));
        if (depth < parameters.X - depthEpsilon || depth > parameters.Y + depthEpsilon) return false;
        float clampedDepth = Math.Clamp(depth, parameters.X, parameters.Y);
        float zNorm;
        if (parameters.W > 0.5f)
        {
            if (parameters.X <= 0f || parameters.Z <= 0f) return false;
            zNorm = MathF.Log(MathF.Max(clampedDepth, parameters.X) / parameters.X) / parameters.Z;
        }
        else
        {
            float span = parameters.Y - parameters.X;
            if (span <= 0f) return false;
            zNorm = (clampedDepth - parameters.X) / span;
        }
        if (!float.IsFinite(zNorm)) return false;
        int x = Math.Clamp((int)MathF.Floor((ndc.X * 0.5f + 0.5f) * 16f), 0, 15);
        int y = Math.Clamp((int)MathF.Floor((ndc.Y * 0.5f + 0.5f) * 9f), 0, 8);
        int z = Math.Clamp((int)MathF.Floor(Math.Clamp(zNorm, 0f, 1f) * 24f), 0, 23);
        cluster = (z * 9 + y) * 16 + x;
        return true;
    }

    static bool Finite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y)
        && float.IsFinite(value.Z) && float.IsFinite(value.W);
}
