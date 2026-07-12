using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>Headless tests for <see cref="FeatureGeometry.CreateDefault"/>: the click-place tool's
    /// default-parameterized feature per type. Covers the ridge no-pass regression (a placed ridge used to carve a
    /// valley exactly at the click point because the default pass sat there).</summary>
    public class FeatureGeometryTests
    {
        static void Near(float expected, float actual, float eps = 1e-4f) =>
            Assert.True(MathF.Abs(expected - actual) < eps, $"expected ~{expected} but got {actual}");

        // Shortest signed wrap to (-pi, pi], the same idiom FeatureGeometry.Rotated uses on a rim pass angle.
        static float WrapToPi(float a) => MathF.Atan2(MathF.Sin(a), MathF.Cos(a));

        // ---- Rotated: the yaw-ring rewrite per rotational DOF ------------------------------------------

        [Fact]
        public void Rotated_Ridge_RotatesDirectionVector()
        {
            // A ridge pointing +X, rotated a quarter turn on the XZ plane, points +Z. Standard (atan2-increasing)
            // rotation of the direction unit vector, renormalized.
            var ridge = new RidgeFeatureDoc { PointX = 3f, PointZ = 4f, DirectionX = 1f, DirectionZ = 0f, Height = 5f, Width = 8f };

            var rotated = Assert.IsType<RidgeFeatureDoc>(FeatureGeometry.Rotated(ridge, MathF.PI / 2f));
            Near(0f, rotated.DirectionX);
            Near(1f, rotated.DirectionZ);
            Near(1f, MathF.Sqrt(rotated.DirectionX * rotated.DirectionX + rotated.DirectionZ * rotated.DirectionZ)); // unit
            Near(3f, rotated.PointX);   // the through-point and every other field carry over
            Near(4f, rotated.PointZ);
            Near(8f, rotated.Width);
        }

        [Fact]
        public void Rotated_Rim_OffsetsEveryPassAngle()
        {
            // Every pass angle shifts by the same delta, wrapped to the canonical range. The second pass sits high
            // enough that the delta pushes it past pi, so it wraps into the negative half.
            var rim = new RimFeatureDoc { CenterX = 1f, CenterZ = 2f, InnerRadius = 10f, OuterRadius = 14f, WallHeight = 6f };
            rim.Passes.Add(new RimPassDoc { AngleRadians = 0f, HalfWidth = 0.5f });
            rim.Passes.Add(new RimPassDoc { AngleRadians = 3.0f, HalfWidth = 0.3f });

            var rotated = Assert.IsType<RimFeatureDoc>(FeatureGeometry.Rotated(rim, 0.5f));
            Assert.Equal(2, rotated.Passes.Count);
            Near(0.5f, rotated.Passes[0].AngleRadians);
            Near(WrapToPi(3.5f), rotated.Passes[1].AngleRadians);   // 3.5 wraps to 3.5 - 2*pi
            Assert.True(rotated.Passes[1].AngleRadians < 0f);       // demonstrably wrapped past pi
            Near(1f, rotated.CenterX);                              // center and the other fields carry over
            Near(2f, rotated.CenterZ);
        }

        [Fact]
        public void Rotated_LakeFlatten_ReturnsNull()
        {
            // Rotationally symmetric features expose no orientation, so the helper returns null (the gizmo offers
            // no yaw ring and a ring grab cannot arm).
            Assert.Null(FeatureGeometry.Rotated(new LakeFeatureDoc { CenterX = 0f, CenterZ = 0f, Radius = 10f, Depth = 3f }, 1f));
            Assert.Null(FeatureGeometry.Rotated(new FlattenFeatureDoc { CenterX = 0f, CenterZ = 0f, Radius = 10f, TargetHeight = 0f }, 1f));
        }

        [Fact]
        public void Rotated_PasslessRim_ReturnsNull()
        {
            // A rim with zero passes has nothing to rotate, so it is rotationally symmetric too: null, same
            // not-offered contract as lake / flatten (no ring, no grab, and no no-op undo entry).
            var rim = new RimFeatureDoc { CenterX = 0f, CenterZ = 0f, InnerRadius = 10f, OuterRadius = 14f, WallHeight = 6f };
            Assert.Empty(rim.Passes);
            Assert.Null(FeatureGeometry.Rotated(rim, 1f));
        }

        // ---- Clone: Name must carry over for every built-in type ---------------------------------------

        [Fact]
        public void FeatureClone_CopiesName()
        {
            var lake = new LakeFeatureDoc { Name = "north-lake", CenterX = 1f, CenterZ = 2f, Radius = 3f, Depth = 4f };
            Assert.Equal("north-lake", FeatureGeometry.Clone(lake).Name);

            var flatten = new FlattenFeatureDoc { Name = "plaza", CenterX = 1f, CenterZ = 2f, Radius = 3f, TargetHeight = 1f };
            Assert.Equal("plaza", FeatureGeometry.Clone(flatten).Name);

            var ridge = new RidgeFeatureDoc { Name = "wall", PointX = 1f, PointZ = 2f, Height = 3f, Width = 4f };
            Assert.Equal("wall", FeatureGeometry.Clone(ridge).Name);

            var rim = new RimFeatureDoc { Name = "crater", CenterX = 1f, CenterZ = 2f, InnerRadius = 3f, OuterRadius = 4f, WallHeight = 5f };
            Assert.Equal("crater", FeatureGeometry.Clone(rim).Name);
        }

        [Fact]
        public void EditorRidge_CreateDefault_HasNoNotchAtClickPoint()
        {
            MapFeature? feature = FeatureGeometry.CreateDefault("ridge", 10f, 20f, groundHeight: 0f);
            var ridgeDoc = Assert.IsType<RidgeFeatureDoc>(feature);

            var doc = new MapDocument { Id = "ridge-default-test", Bounds = new MapBounds { MinX = -50f, MinZ = -50f, MaxX = 50f, MaxZ = 50f } };
            doc.Terrain.Features.Add(ridgeDoc);

            var registry = MapDocRegistry.CreateDefault();
            var withRidge = MapRuntime.BuildField(doc, registry);

            doc.Terrain.Features.Clear();
            var baseline = MapRuntime.BuildField(doc, registry);

            // The ridge's contribution at the click point itself: with the old always-on pass this was ~0
            // (a carved dip right where the player clicked). The new opt-in default is a solid wall, so the
            // contribution at the click point is the full crest height.
            float contribution = withRidge.SampleHeight(10f, 20f) - baseline.SampleHeight(10f, 20f);
            Assert.Equal(ridgeDoc.Height, contribution, 2);
        }
    }
}
