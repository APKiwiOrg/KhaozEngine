using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapEditor;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>Headless tests for the transform gizmo. Two halves, both GPU-free: <see cref="GizmoGeometry"/>
    /// builds vertex-colored <see cref="GltfMesh"/> handles (DrawOverlayMesh reads color from the vertex only,
    /// so the color is baked in), and <see cref="GizmoDrag"/> is pure ray math (a hit test plus per-handle
    /// drag deltas). Every drag helper is asserted twice on identical input to pin its purity, and the geometry
    /// constants are exercised through the hit test so the visual mesh and the pickable region can never drift.</summary>
    public class GizmoTests
    {
        static void Near(float expected, float actual, float eps = 1e-3f) =>
            Assert.True(MathF.Abs(expected - actual) < eps, $"expected ~{expected} but got {actual}");

        static void NearV(Vector3 expected, Vector3 actual, float eps = 1e-3f) =>
            Assert.True((expected - actual).Length() < eps, $"expected ~{expected} but got {actual}");

        // ---- geometry: non-empty, sane counts, per-handle colors ---------------------------------------

        [Fact]
        public void TranslateArrows_HasDistinctPerAxisColors()
        {
            GltfMesh mesh = GizmoGeometry.TranslateArrows();

            Assert.NotEmpty(mesh.Vertices);
            Assert.True(mesh.Indices32.Length > 0);
            Assert.Equal(0, mesh.Indices32.Length % 3);          // whole triangles
            Assert.All(mesh.Indices32, i => Assert.True(i < mesh.Vertices.Length));

            Vector4[] colors = mesh.Vertices.Select(v => v.Color).Distinct().ToArray();
            // Exactly the three axis colors: X red, Y green, Z blue - one per arrow, distinct.
            Assert.Equal(3, colors.Length);
            Assert.Contains(GizmoGeometry.AxisXColor, colors);
            Assert.Contains(GizmoGeometry.AxisYColor, colors);
            Assert.Contains(GizmoGeometry.AxisZColor, colors);
        }

        [Fact]
        public void TranslateArrowsXZ_HasOnlyXAndZColors_NoYArrow()
        {
            GltfMesh mesh = GizmoGeometry.TranslateArrowsXZ();

            Assert.NotEmpty(mesh.Vertices);
            Assert.True(mesh.Indices32.Length > 0);
            Assert.Equal(0, mesh.Indices32.Length % 3);          // whole triangles
            Assert.All(mesh.Indices32, i => Assert.True(i < mesh.Vertices.Length));

            Vector4[] colors = mesh.Vertices.Select(v => v.Color).Distinct().ToArray();
            // Only the two ground-plane axis colors: X red, Z blue. No Y (green): the vertical handle is
            // RestrictHandle-blocked for every affordance that draws this mesh, so the mesh never offers it.
            Assert.Equal(2, colors.Length);
            Assert.Contains(GizmoGeometry.AxisXColor, colors);
            Assert.Contains(GizmoGeometry.AxisZColor, colors);
            Assert.DoesNotContain(GizmoGeometry.AxisYColor, colors);
        }

        [Fact]
        public void YawRing_IsNonEmptyAndYawColored()
        {
            GltfMesh mesh = GizmoGeometry.YawRing();

            Assert.NotEmpty(mesh.Vertices);
            Assert.Equal(0, mesh.Indices32.Length % 3);
            Assert.All(mesh.Vertices, v => Assert.Equal(GizmoGeometry.YawColor, v.Color));
            // Every ring vertex sits on the band at y = 0, within the outer radius.
            Assert.All(mesh.Vertices, v => Assert.True(MathF.Abs(v.Position.Y) < 1e-4f));
        }

        [Fact]
        public void ScaleHandle_IsNonEmptyAndScaleColored()
        {
            GltfMesh mesh = GizmoGeometry.ScaleHandle();

            Assert.NotEmpty(mesh.Vertices);
            Assert.Equal(0, mesh.Indices32.Length % 3);
            Assert.All(mesh.Vertices, v => Assert.Equal(GizmoGeometry.ScaleColor, v.Color));
            // The cube is centred on the corner (offset, 0, offset), so every vertex is near that corner.
            Assert.All(mesh.Vertices, v =>
            {
                Assert.True(MathF.Abs(v.Position.X - GizmoGeometry.ScaleCubeOffset) <= GizmoGeometry.ScaleCubeHalfExtent + 1e-4f);
                Assert.True(MathF.Abs(v.Position.Z - GizmoGeometry.ScaleCubeOffset) <= GizmoGeometry.ScaleCubeHalfExtent + 1e-4f);
            });
        }

        [Fact]
        public void SelectionMarker_IsNonEmptyAndMarkerColored()
        {
            GltfMesh mesh = GizmoGeometry.SelectionMarker();

            Assert.NotEmpty(mesh.Vertices);
            Assert.Equal(0, mesh.Indices32.Length % 3);
            Assert.All(mesh.Vertices, v => Assert.Equal(GizmoGeometry.MarkerColor, v.Color));
        }

        [Fact]
        public void Builders_AreDeterministic()
        {
            // Same input (none) -> byte-identical mesh shape on every call.
            GltfMesh a = GizmoGeometry.TranslateArrows();
            GltfMesh b = GizmoGeometry.TranslateArrows();
            Assert.Equal(a.Vertices.Length, b.Vertices.Length);
            Assert.Equal(a.Indices32.Length, b.Indices32.Length);
            Assert.True(a.Vertices.Zip(b.Vertices, (x, y) => x.Position == y.Position && x.Color == y.Color).All(ok => ok));
        }

        // ---- hit test: each handle from a known ray ----------------------------------------------------

        static readonly Vector3 Down = new(0f, -1f, 0f);

        [Fact]
        public void HitTest_ScaleCube_ResolvesScale()
        {
            GizmoDrag.GizmoHandle h = GizmoDrag.HitTest(Vector3.Zero, 1f, new Vector3(0.85f, 5f, 0.85f), Down);
            Assert.Equal(GizmoDrag.GizmoHandle.Scale, h);
        }

        [Fact]
        public void HitTest_YArrow_ResolvesTranslateY()
        {
            // Straight down the +Y axis: the vertical arrow box wins (it also overlaps the flat arrows at the
            // origin, and priority puts TranslateY ahead of TranslateXZ).
            GizmoDrag.GizmoHandle h = GizmoDrag.HitTest(Vector3.Zero, 1f, new Vector3(0f, 5f, 0f), Down);
            Assert.Equal(GizmoDrag.GizmoHandle.TranslateY, h);
        }

        [Fact]
        public void HitTest_XArrow_ResolvesTranslateXZ()
        {
            GizmoDrag.GizmoHandle h = GizmoDrag.HitTest(Vector3.Zero, 1f, new Vector3(0.6f, 5f, 0f), Down);
            Assert.Equal(GizmoDrag.GizmoHandle.TranslateXZ, h);
        }

        [Fact]
        public void HitTest_ZArrow_ResolvesTranslateXZ()
        {
            GizmoDrag.GizmoHandle h = GizmoDrag.HitTest(Vector3.Zero, 1f, new Vector3(0f, 5f, 0.6f), Down);
            Assert.Equal(GizmoDrag.GizmoHandle.TranslateXZ, h);
        }

        [Fact]
        public void HitTest_Ring_ResolvesYawRing()
        {
            // A point on the ring at 45 degrees is clear of the axis arrow boxes and the corner cube, so only
            // the flat annulus band is hit.
            float d = GizmoGeometry.RingRadius * MathF.Sqrt(0.5f);
            GizmoDrag.GizmoHandle h = GizmoDrag.HitTest(Vector3.Zero, 1f, new Vector3(d, 5f, d), Down);
            Assert.Equal(GizmoDrag.GizmoHandle.YawRing, h);
        }

        [Fact]
        public void HitTest_OverlapPrefersHigherPriority()
        {
            // Down the origin hits the +Y arrow AND both flat arrows; TranslateY outranks TranslateXZ.
            GizmoDrag.GizmoHandle h = GizmoDrag.HitTest(Vector3.Zero, 1f, new Vector3(0f, 5f, 0f), Down);
            Assert.Equal(GizmoDrag.GizmoHandle.TranslateY, h);
        }

        [Fact]
        public void HitTest_Miss_ReturnsNone()
        {
            // Aimed up and away from every handle volume.
            GizmoDrag.GizmoHandle h = GizmoDrag.HitTest(Vector3.Zero, 1f, new Vector3(5f, 5f, 5f), Vector3.UnitY);
            Assert.Equal(GizmoDrag.GizmoHandle.None, h);
        }

        [Fact]
        public void HitTest_ScaledOffsetGizmo_ResolvesScale()
        {
            // gizmoScale and gizmoPos both non-trivial: the corner cube lands at pos + scale*(offset,0,offset).
            var pos = new Vector3(10f, 2f, 10f);
            float s = 2f;
            var over = new Vector3(10f + s * GizmoGeometry.ScaleCubeOffset, 8f, 10f + s * GizmoGeometry.ScaleCubeOffset);
            GizmoDrag.GizmoHandle h = GizmoDrag.HitTest(pos, s, over, Down);
            Assert.Equal(GizmoDrag.GizmoHandle.Scale, h);
        }

        [Fact]
        public void HitTest_IsPure()
        {
            var over = new Vector3(0.6f, 5f, 0f);
            GizmoDrag.GizmoHandle a = GizmoDrag.HitTest(Vector3.Zero, 1f, over, Down);
            GizmoDrag.GizmoHandle b = GizmoDrag.HitTest(Vector3.Zero, 1f, over, Down);
            Assert.Equal(a, b);
            Assert.Equal(GizmoDrag.GizmoHandle.TranslateXZ, a);
        }

        // ---- drag deltas -------------------------------------------------------------------------------

        [Fact]
        public void TranslateXZDelta_ExactPlaneIntersection()
        {
            // Drag plane is y = StartPoint.Y = 3. The current ray from (5,8,5) straight down meets it at
            // (5,3,5); the delta from the start point (2,3,2) is (3,0,3), on the plane exactly.
            var g = new GizmoDrag.DragGesture(GizmoDrag.GizmoHandle.TranslateXZ,
                StartPoint: new Vector3(2f, 3f, 2f), ObjectStart: new Vector3(2f, 3f, 2f),
                ObjectStartYaw: 0f, ObjectStartScale: 1f);
            var origin = new Vector3(5f, 8f, 5f);

            Vector3 delta = GizmoDrag.TranslateXZDelta(g, origin, Down);
            NearV(new Vector3(3f, 0f, 3f), delta);
            // purity: identical inputs -> identical output.
            Assert.Equal(delta, GizmoDrag.TranslateXZDelta(g, origin, Down));
        }

        [Fact]
        public void TranslateXZDelta_ParallelRay_ReturnsZero()
        {
            var g = new GizmoDrag.DragGesture(GizmoDrag.GizmoHandle.TranslateXZ,
                new Vector3(2f, 3f, 2f), new Vector3(2f, 3f, 2f), 0f, 1f);
            // A ray parallel to the drag plane never meets it: no movement.
            Vector3 delta = GizmoDrag.TranslateXZDelta(g, new Vector3(0f, 8f, 0f), new Vector3(1f, 0f, 0f));
            NearV(Vector3.Zero, delta);
        }

        [Fact]
        public void TranslateYDelta_ClosestApproach()
        {
            // Vertical axis through StartPoint (0,0,0). A horizontal ray sitting at height 3 has its closest
            // approach to the axis at y = 3, so the delta is +3.
            var g = new GizmoDrag.DragGesture(GizmoDrag.GizmoHandle.TranslateY,
                StartPoint: Vector3.Zero, ObjectStart: Vector3.Zero, ObjectStartYaw: 0f, ObjectStartScale: 1f);
            var origin = new Vector3(5f, 3f, 0f);
            var dir = new Vector3(-1f, 0f, 0f);

            float dy = GizmoDrag.TranslateYDelta(g, origin, dir);
            Near(3f, dy);
            Assert.Equal(dy, GizmoDrag.TranslateYDelta(g, origin, dir)); // purity
        }

        [Fact]
        public void YawDelta_QuarterTurnTowardPlusZ_ReturnsMinusHalfPi()
        {
            // Start handle at +X of the gizmo; the current ray lands at +Z. A quarter turn, magnitude pi/2.
            // The sign is NEGATIVE: the delta composes additively with the yaw fed to CreateRotationY, whose
            // +yaw turns object +X toward -Z (row-vector convention), so a drag toward +Z needs a negative yaw.
            var g = new GizmoDrag.DragGesture(GizmoDrag.GizmoHandle.YawRing,
                StartPoint: new Vector3(1f, 0f, 0f), ObjectStart: Vector3.Zero, ObjectStartYaw: 0f, ObjectStartScale: 1f);
            var origin = new Vector3(0f, 5f, 1f);

            float yaw = GizmoDrag.YawDelta(g, origin, Down);
            Near(-MathF.PI / 2f, yaw);
            Assert.Equal(yaw, GizmoDrag.YawDelta(g, origin, Down)); // purity
        }

        [Fact]
        public void YawDelta_ComposedYaw_TracksPointerUnderCreateRotationY()
        {
            // Renderer-convention pin: a nub authored at object +X, gesture started at world +X, dragged to
            // world +Z. Composing newYaw = ObjectStartYaw + YawDelta and rotating with the renderer's
            // Matrix4x4.CreateRotationY must land the nub where the pointer is (+Z), not mirrored to -Z.
            var g = new GizmoDrag.DragGesture(GizmoDrag.GizmoHandle.YawRing,
                StartPoint: new Vector3(1f, 0f, 0f), ObjectStart: Vector3.Zero, ObjectStartYaw: 0f, ObjectStartScale: 1f);
            var origin = new Vector3(0f, 5f, 1f);

            float newYaw = g.ObjectStartYaw + GizmoDrag.YawDelta(g, origin, Down);
            Vector3 nub = Vector3.Transform(new Vector3(1f, 0f, 0f), Matrix4x4.CreateRotationY(newYaw));
            NearV(new Vector3(0f, 0f, 1f), nub);
        }

        [Fact]
        public void ScaleFactor_DoubledDistance_ReturnsTwo()
        {
            // Start radius 1 (handle at +X, unit out). The current ray lands at radius 2, so scale doubles.
            var g = new GizmoDrag.DragGesture(GizmoDrag.GizmoHandle.Scale,
                StartPoint: new Vector3(1f, 0f, 0f), ObjectStart: Vector3.Zero, ObjectStartYaw: 0f, ObjectStartScale: 1f);
            var origin = new Vector3(2f, 5f, 0f);

            float factor = GizmoDrag.ScaleFactor(g, origin, Down);
            Near(2f, factor);
            Assert.Equal(factor, GizmoDrag.ScaleFactor(g, origin, Down)); // purity
        }

        // ---- affordance -> mesh list: pure, headless-testable (mirrors MapEditorScene.ComputeOverlayDrawList;
        // only the per-entry MeshHandle lookup and DrawOverlayMesh submission in DrawGizmo is untested GPU work) --

        [Fact]
        public void SpawnSelection_DrawsTranslateArrows()
        {
            // A spawn's affordance is Marker: before this round it drew only the selection pyramid, so the
            // working ground-plane drag had no visible handle. It must now also draw the XZ arrows.
            GizmoMesh[] meshes = MapEditorScene.ComputeGizmoMeshes(GizmoAffordance.Marker);

            Assert.Contains(GizmoMesh.SelectionMarker, meshes);
            Assert.Contains(GizmoMesh.TranslateArrowsXZ, meshes);
            Assert.DoesNotContain(GizmoMesh.TranslateArrowsFull, meshes);
        }

        [Fact]
        public void MoveScaleAffordance_HasNoYArrow()
        {
            // A feature / disc / rect shape's affordance is MoveScale: RestrictHandle already blocks its Y
            // handle, so the mesh must stop offering one (the ledgered inert +Y arrow).
            GizmoMesh[] meshes = MapEditorScene.ComputeGizmoMeshes(GizmoAffordance.MoveScale);

            Assert.Contains(GizmoMesh.TranslateArrowsXZ, meshes);
            Assert.Contains(GizmoMesh.ScaleHandle, meshes);
            Assert.DoesNotContain(GizmoMesh.TranslateArrowsFull, meshes);
        }

        [Fact]
        public void MoveScaleRotateAffordance_HasYawRingButNoVerticalArrow()
        {
            // A rotatable terrain feature (ridge or rim) draws the XZ arrows, the yaw ring, and the scale cube,
            // but never the +Y arrow (RestrictHandle blocks the vertical handle for every feature). This is the
            // one affordance distinct from Full that offers a ring.
            GizmoMesh[] meshes = MapEditorScene.ComputeGizmoMeshes(GizmoAffordance.MoveScaleRotate);

            Assert.Contains(GizmoMesh.TranslateArrowsXZ, meshes);
            Assert.Contains(GizmoMesh.YawRing, meshes);
            Assert.Contains(GizmoMesh.ScaleHandle, meshes);
            Assert.DoesNotContain(GizmoMesh.TranslateArrowsFull, meshes);
        }

        [Fact]
        public void FullAffordance_StillHasTheVerticalArrowAndYawRing()
        {
            // A placement keeps the full transform: translate (all three axes), yaw, and scale. Unchanged by
            // this round.
            GizmoMesh[] meshes = MapEditorScene.ComputeGizmoMeshes(GizmoAffordance.Full);

            Assert.Contains(GizmoMesh.TranslateArrowsFull, meshes);
            Assert.Contains(GizmoMesh.YawRing, meshes);
            Assert.Contains(GizmoMesh.ScaleHandle, meshes);
            Assert.DoesNotContain(GizmoMesh.TranslateArrowsXZ, meshes);
        }

        [Fact]
        public void NoneAffordance_DrawsNothing()
        {
            Assert.Empty(MapEditorScene.ComputeGizmoMeshes(GizmoAffordance.None));
        }
    }
}
