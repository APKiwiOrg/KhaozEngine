using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class InteriorCutHighlightGpuTests
{
    [GpuFact]
    public void RepresentedCellBoundariesMatchCpuInColourOutlineAndSilhouette()
    {
        foreach (float offset in new[] { 0f, 4096f })
        {
            var window = new InteriorWindow(new Vector2(offset, offset - 1.92f), .24f, 16);
            Vector2 min = window.Min, max = InteriorLattice.Corner(window, 1, 16);
            var field = InteriorHeightFieldBuilder.Build([new InteriorVolume
            {
                Outline = [min, new(max.X, min.Y), max, new(min.X, max.Y)], FloorY = 0f, CeilingY = 3f,
            }], window, new(0f, .01f));
            float edge = (float)((double)offset + .24f);
            foreach (float x in new[] { MathF.BitDecrement(edge), edge, MathF.BitIncrement(edge) })
                CheckBoundary(field, new Vector3(x, 1f, offset), new Vector3(offset, 128f, offset), vertical: true);
        }
    }

    [GpuFact]
    public void RepresentedHeightBoundariesMatchCpuAwayFromOriginInEveryHighlightPath()
    {
        var window = new InteriorWindow(new Vector2(4094f), .24f, 20);
        Vector2 min = window.Min, max = InteriorLattice.Corner(window, 20, 20);
        var field = InteriorHeightFieldBuilder.Build([new InteriorVolume
        {
            Outline = [min, new(max.X, min.Y), max, new(min.X, max.Y)], FloorY = .1525f, CeilingY = .4025f,
        }], window, new(0f, .01f));
        foreach (float height in new[] { MathF.BitDecrement(.15f), .15f, MathF.BitIncrement(.15f),
            MathF.BitDecrement(.4f), .4f, MathF.BitIncrement(.4f) })
            CheckBoundary(field, new Vector3(4096f, height, 4096f), new Vector3(4096f, 128f, 4096f), vertical: false);
    }

    void CheckBoundary(InteriorHeightField field, Vector3 point, Vector3 renderOrigin, bool vertical)
    {
        // Independent literal GLSL mirror, including both rounds into the render-relative frame.
        Vector3 relative = point - renderOrigin;
        Vector2 minimum = new((float)((double)field.Window.Min.X - renderOrigin.X),
            (float)((double)field.Window.Min.Y - renderOrigin.Z));
        Vector2 cell = (new Vector2(relative.X, relative.Z) - minimum) * (1f / field.Window.CellSize);
        int at = ((int)cell.Y * field.Window.Cells + (int)cell.X) * 4;
        ReadOnlySpan<byte> bytes = field.Texels.Span.Slice(at, 4);
        float q = (relative.Y - (float)((double)field.BaseY - renderOrigin.Y)) * (1f / field.StepY);
        int lo = bytes[0] * 256 + bytes[1], hi = bytes[2] * 256 + bytes[3];
        bool expected = hi > lo && q >= lo && q < hi;
        foreach (BoundaryMode mode in Enum.GetValues<BoundaryMode>())
        {
            byte[] background = _stage.BoundaryFrame(null, point, renderOrigin, vertical, mode, draw: false);
            byte[] solid = _stage.BoundaryFrame(null, point, renderOrigin, vertical, mode);
            byte[] pixels = _stage.BoundaryFrame(field, point, renderOrigin, vertical, mode);
            Assert.Equal(renderOrigin, _stage.Scene.CurrentFrameView.RenderOrigin);
            if (mode is BoundaryMode.OutlineVisible or BoundaryMode.OutlineFull)
            {
                Assert.Equal(expected ? 0f : 1f, _stage.Coverage("_fullCoverage")[Stage.H / 2 * Stage.W + Stage.W / 2]);
                if (mode == BoundaryMode.OutlineVisible)
                    Assert.Equal(expected ? 0f : 1f, _stage.Coverage("_visibleCoverage")[Stage.H / 2 * Stage.W + Stage.W / 2]);
            }
            else
            {
                int pixel = (Stage.H / 2 * Stage.W + Stage.W / 2) * 4;
                Assert.False(solid.AsSpan(pixel, 4).SequenceEqual(background.AsSpan(pixel, 4)));
                Assert.Equal(expected, pixels.AsSpan(pixel, 4).SequenceEqual(background.AsSpan(pixel, 4)));
            }
        }
        Assert.Equal(expected, field.Contains(point, new InteriorCutFrame(renderOrigin)));
    }

    internal enum BoundaryMode { Colour, OutlineVisible, OutlineFull, Silhouette, SilhouetteTemporal }

    public sealed partial class Stage
    {
        MeshHandle? _boundaryPlane, _boundaryHull;

        internal byte[] BoundaryFrame(InteriorHeightField? field, Vector3 point, Vector3 renderOrigin,
            bool vertical, BoundaryMode mode, bool draw = true)
        {
            _boundaryPlane ??= Scene.LoadMesh(MeshPrimitives.Plane(2f, 2f));
            if (_boundaryHull is null)
            {
                GltfMesh plane = MeshPrimitives.Plane(2f, 2f);
                ModelVertex[] vertices = plane.Vertices.Select(v => new ModelVertex(
                    v.Position + new Vector3(0f, .03125f, 0f), -Vector3.UnitY, v.Color, v.Uv)).ToArray();
                _boundaryHull = Scene.LoadMesh(new GltfMesh(vertices, plane.Indices32.Reverse().ToArray()));
            }
            Scene.RenderOrigin = renderOrigin;
            Scene.Camera.Target = point;
            Scene.Camera.Azimuth = vertical ? MathF.PI / 2f : 0f;
            Scene.Camera.Elevation = vertical ? 0f : MathF.PI * .45f;
            Scene.Camera.OrthoSize = 1f;
            Scene.InteriorCut = field;
            Scene.ForceTemporalForTests = mode == BoundaryMode.SilhouetteTemporal;
            Scene.CameraCut();
            byte[] pixels = Fixture.Frame((s, _) =>
            {
                if (!draw) return;
                Matrix4x4 world = (vertical ? new Matrix4x4(0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1) : Matrix4x4.Identity)
                    * Matrix4x4.CreateTranslation(point);
                switch (mode)
                {
                    case BoundaryMode.Colour:
                        s.Draw(new RigidInstanceDraw(_boundaryPlane.Value, world)
                        { Tint = new Color(.8f, .5f, .2f, 1), CutInsideInteriors = true });
                        break;
                    case BoundaryMode.OutlineVisible:
                    case BoundaryMode.OutlineFull:
                        var occlusion = mode == BoundaryMode.OutlineFull ? MeshOutlineOcclusion.None : MeshOutlineOcclusion.SceneDepth;
                        s.DrawMeshOutlineDissolved(s.BeginMeshOutline(new Color(1, 0, 0, 1), 2, occlusion),
                            _boundaryPlane.Value, world, 0f, false, true);
                        break;
                    default:
                        s.DrawMeshSilhouette(_boundaryHull.Value, world, new Color(1, 0, 0, 1), .03125f, true);
                        break;
                }
            });
            // The ordinary stage advances a whole jitter cycle per capture. Boundary probes share that
            // contract so a later temporal hull probe sees the same represented pixel centres as a fresh stage.
            Fixture.SkipFrames(TemporalJitter.NativePhaseCount - 1);
            return pixels;
        }
    }
}
