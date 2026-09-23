using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// Proves on seeded scenes that the range-limited cluster builder assigns every cluster exactly the lights, in exactly
/// the order, that the brute-force builder assigned (issue #1112). The brute-force builder is kept verbatim as
/// <see cref="PointLightClusterOracle"/>. Scenes are seeded, so a failure names a seed that reproduces it. One builder is
/// reused across a batch, so state left by the previous scene is exercised too. The one known difference, a light the
/// brute force admits into slice zero only through its own plane rounding, adds no light and is pinned in
/// <see cref="PointLightClusterRangeTests"/>.
/// </summary>
public sealed class PointLightClusterEquivalenceTests
{
    const int ScenesPerBatch = 32;
    static readonly Vector3 Forward = -Vector3.UnitZ;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void SeededScenesAssignEveryClusterExactlyAsTheBruteForceBuilder(int batch)
    {
        var builder = new PointLightClusterBuilder();
        var oracle = new PointLightClusterOracle();
        for (int index = 0; index < ScenesPerBatch; index++)
        {
            int seed = batch * ScenesPerBatch + index;
            ClusterScene scene = ClusterScene.Generate(seed);

            builder.Build(scene.Lights, scene.ViewProjection, scene.Eye, scene.Forward, scene.Projection,
                scene.RenderOrigin);
            oracle.Build(scene.Lights, scene.ViewProjection, scene.Eye, scene.Forward, scene.Projection,
                scene.RenderOrigin);

            PointLightClusterImage.AssertSameAssignment(oracle, builder, $"seed {seed}, {scene.Description}");
        }
    }

    [Fact]
    public void ASphereWhollyOutsideTheFrustumThatThePlaneTestAcceptsIsStillAssigned()
    {
        // Behind and left of a 90 degree camera, reaching the near plane's depth only 10 m off to the side. The sphere
        // lies wholly outside the frustum, yet the six-plane test accepts it for the rightmost column's first slice,
        // because behind the eye that column's side planes have crossed. A frustum cull would drop it and change the
        // assignment, which is why a light that reaches the near plane is never culled by a side plane.
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2f, 1f, 0.1f, 100f);
        ModelRenderer.PointLightData[] lights = [Light(new Vector3(-10.5f, 0f, 4.8f), 5f)];
        var oracle = new PointLightClusterOracle();
        var builder = new PointLightClusterBuilder();

        oracle.Build(lights, projection, Vector3.Zero, Forward, projection, Vector3.Zero);
        builder.Build(lights, projection, Vector3.Zero, Forward, projection, Vector3.Zero);

        Assert.True(PointLightClusterImage.Contains(oracle, 15, 4, 0, 0u),
            "precondition: the brute-force plane test accepts this sphere in cluster (15, 4, 0)");
        PointLightClusterImage.AssertSameAssignment(oracle, builder, "a sphere behind and beside the camera");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACameraInsideALightSphereMatchesTheBruteForceBuilder(bool perspective)
    {
        Matrix4x4 projection = perspective
            ? Matrix4x4.CreatePerspectiveFieldOfView(1.1f, 16f / 9f, 0.1f, 300f)
            : Matrix4x4.CreateOrthographic(32f, 18f, -2f, 22f);
        ModelRenderer.PointLightData[] lights =
        [
            Light(new Vector3(0.2f, -0.1f, 0.3f), 4f),
            Light(new Vector3(3f, 1f, -6f), 2f),
            Light(Vector3.Zero, 0.05f),
        ];
        var oracle = new PointLightClusterOracle();
        var builder = new PointLightClusterBuilder();

        oracle.Build(lights, projection, Vector3.Zero, Forward, projection, Vector3.Zero);
        builder.Build(lights, projection, Vector3.Zero, Forward, projection, Vector3.Zero);

        PointLightClusterImage.AssertSameAssignment(oracle, builder, $"camera inside a light, perspective {perspective}");
    }

    static ModelRenderer.PointLightData Light(Vector3 position, float radius) => new()
    {
        PosRadius = new Vector4(position, radius),
        ColorIntensity = Vector4.One,
    };

    readonly record struct ClusterScene(ModelRenderer.PointLightData[] Lights, Matrix4x4 ViewProjection, Vector3 Eye,
        Vector3 Forward, Matrix4x4 Projection, Vector3 RenderOrigin, string Description)
    {
        internal static ClusterScene Generate(int seed)
        {
            var random = new Random(seed);
            Vector3 origin = random.Next(4) switch
            {
                0 => Vector3.Zero,
                1 => new Vector3(100_000f, -50_000f, 70_000f),
                _ => Scatter(random, 2_000f),
            };
            // A game that sets no render origin renders in world space, so the eye can sit hundreds of metres out.
            Vector3 eye = Scatter(random, random.Next(4) == 0 ? 200f : 50f);
            Vector3 direction = Direction(random);
            Vector3 up = MathF.Abs(direction.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
            (Matrix4x4 projection, string kind) = RandomProjection(random);
            Matrix4x4 viewProjection = Matrix4x4.CreateLookAt(eye, eye + direction, up) * projection;
            bool flipped = random.Next(2) == 0;
            if (flipped) viewProjection *= Matrix4x4.CreateScale(1f, -1f, 1f);
            // The builder normalizes forward itself, so a scaled one must change nothing.
            Vector3 forward = direction * Between(random, 0.5f, 3f);
            bool invertible = Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse);

            var lights = new List<ModelRenderer.PointLightData>();
            int count = random.Next(8) == 0 ? 0 : random.Next(1, 97);
            for (int i = 0; i < count; i++)
            {
                int placement = random.Next(20);
                lights.Add(placement switch
                {
                    < 10 => InView(random, origin, eye, direction, invertible, inverse),
                    < 13 => Light(origin + eye + Scatter(random, 3f), Between(random, 0.2f, 8f)),
                    < 16 => Light(origin + eye - direction * Between(random, 0.5f, 40f) + Scatter(random, 15f),
                        Between(random, 0.5f, 20f)),
                    < 18 => Light(origin + eye + Direction(random) * Between(random, 500f, 4_000f),
                        Between(random, 1f, 50f)),
                    18 when lights.Count > 0 => lights[random.Next(lights.Count)],
                    _ => Malformed(random, origin + eye + direction * 5f),
                });
            }
            int stack = random.Next(6);
            if (stack < 2)
            {
                // Exactly 64, or more than 64, lights on one sphere, which fills or overflows its clusters.
                ModelRenderer.PointLightData stacked = InView(random, origin, eye, direction, invertible, inverse);
                int size = stack == 0 ? 64 : random.Next(65, 81);
                for (int i = 0; i < size; i++) lights.Add(stacked);
            }
            string description = $"{kind}{(flipped ? ", clip-Y flipped" : "")}, {lights.Count} lights, eye {eye}, "
                + $"origin {origin}";
            return new ClusterScene(lights.ToArray(), viewProjection, eye, forward, projection, origin, description);
        }

        static (Matrix4x4 Projection, string Kind) RandomProjection(Random random)
        {
            int kind = random.Next(10);
            if (kind < 6)
            {
                float near = Between(random, 0.05f, 2f);
                float far = MathF.Min(near * Between(random, 20f, 5_000f), 2_000f);
                return (Matrix4x4.CreatePerspectiveFieldOfView(Between(random, 0.3f, 1.9f),
                    Between(random, 0.5f, 2.5f), near, far), "perspective");
            }
            if (kind < 7)
            {
                float near = Between(random, 0.1f, 1f);
                return (Matrix4x4.CreatePerspectiveOffCenter(-Between(random, 0.2f, 1.5f) * near,
                    Between(random, 0.2f, 1.5f) * near, -Between(random, 0.2f, 1.2f) * near,
                    Between(random, 0.2f, 1.2f) * near, near, Between(random, 50f, 800f)), "off-centre perspective");
            }
            float orthoNear = Between(random, -10f, 2f);
            return (Matrix4x4.CreateOrthographic(Between(random, 4f, 80f), Between(random, 4f, 60f), orthoNear,
                orthoNear + Between(random, 5f, 200f)), "orthographic");
        }

        // Anywhere between the near and far planes, biased toward the camera where clusters are smallest.
        static ModelRenderer.PointLightData InView(Random random, Vector3 origin, Vector3 eye, Vector3 direction,
            bool invertible, Matrix4x4 inverse)
        {
            float x = Between(random, -1.2f, 1.2f);
            float y = Between(random, -1.2f, 1.2f);
            float t = Between(random, 0f, 1f);
            float radius = Between(random, 0.05f, 12f);
            Vector3 position = eye + direction * (1f + 60f * t);
            if (invertible && TryUnproject(inverse, x, y, 0f, out Vector3 near)
                && TryUnproject(inverse, x, y, 1f, out Vector3 far))
                position = Vector3.Lerp(near, far, t * t);
            return Light(origin + position, radius);
        }

        static ModelRenderer.PointLightData Malformed(Random random, Vector3 near) => random.Next(5) switch
        {
            0 => Light(new Vector3(float.NaN, near.Y, near.Z), 3f),
            1 => Light(near, -2f),
            2 => Light(near, 0f),
            3 => Light(near, float.PositiveInfinity),
            _ => Light(new Vector3(1e30f, near.Y, near.Z), 5f),
        };

        static bool TryUnproject(Matrix4x4 inverse, float x, float y, float z, out Vector3 point)
        {
            Vector4 homogeneous = Vector4.Transform(new Vector4(x, y, z, 1f), inverse);
            point = new Vector3(homogeneous.X, homogeneous.Y, homogeneous.Z) / homogeneous.W;
            return float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
        }

        static float Between(Random random, float min, float max) => min + (float)random.NextDouble() * (max - min);

        static Vector3 Scatter(Random random, float extent) =>
            new(Between(random, -extent, extent), Between(random, -extent, extent), Between(random, -extent, extent));

        static Vector3 Direction(Random random)
        {
            Vector3 direction;
            do direction = Scatter(random, 1f);
            while (direction.LengthSquared() < 0.01f || direction.LengthSquared() > 1f);
            return Vector3.Normalize(direction);
        }
    }
}
