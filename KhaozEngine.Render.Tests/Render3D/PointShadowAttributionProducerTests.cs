using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class PointShadowAttributionProducerTests
{
    static PointShadowAttributionCapture Capture() => new(Guid.NewGuid(), new(32, 8, 128, 256, 1024, 128, 64));
    static PointAttributionRow StaticRow(PointShadowAttributionCapture capture, ShadowStageProbeSnapshot snapshot)
    {
        var frame = Assert.IsType<PointAttributionFrame>(snapshot.PointAttribution);
        return capture.GetRow(frame.RowStart);
    }
    static PointAttributionCaster FirstCaster(PointShadowAttributionCapture c, PointAttributionRow row)
        => c.GetCaster(c.GetContributor(c.GetSpan(row.Span).Start).Caster);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exact_rigid_words_and_optional_keys_match_actual_signatures_without_temporal_changes(bool temporal)
    {
        using var f = new ShadowStageProbeFixture();
        using var control = new ShadowStageProbeFixture();
        if (temporal)
        {
            f.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            control.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        }
        var capture = Capture();
        var matrix = Matrix4x4.CreateTranslation(1.5f, .5f, 0);
        void Queue(Scene3D scene) => scene.Draw(new RigidInstanceDraw(f.Mesh, matrix) { Motion = MotionKey.From(71) });
        ShadowStageProbeSnapshot Render(ShadowStageProbeFixture fixture, PointShadowAttributionCapture? arena)
            => fixture.Render(ShadowStageProbeMode.Counts, rigid: false, dynamicLight: false, extraQueue: Queue, attribution: arena);
        Render(f, capture); Render(control, null);
        var first = StaticRow(capture, Render(f, capture)); Render(control, null);
        Assert.Equal(f.Order(), control.Order()); Assert.Equal(f.Signatures(), control.Signatures());
        var caster = FirstCaster(capture, first);
        Assert.Equal(71UL, caster.MotionKey); Assert.Equal(0x3fc00000u, caster.Matrix[12]);
        Assert.Equal(2000UL, capture.GetInput(first.Input).Header[2]); Assert.Equal(6000UL, capture.GetInput(first.Input).Header[4]);
        Assert.Equal(unchecked((ulong)f.Signatures()[first.Slot]), Reconstruct(capture, first));
        matrix.M41 = MathF.BitIncrement(1.5f);
        var moved = StaticRow(capture, Render(f, capture)); Render(control, null);
        Assert.Equal(0x3fc00001u, FirstCaster(capture, moved).Matrix[12]);
        Assert.True(moved.Mismatch); Assert.Equal(first.NewSignature, moved.OldSignature);
        Assert.Equal(Reconstruct(capture, moved), moved.NewSignature);
        Assert.Equal(f.Order(), control.Order()); Assert.Equal(f.Signatures(), control.Signatures());
        Assert.Equal(0x3fc00000u, caster.Matrix[12]);
    }

    [Fact]
    public void Actual_request_order_dirty_carry_and_row_work_reconcile_with_aggregates()
    {
        using var f = new ShadowStageProbeFixture();
        using var foliage = f.Foliage();
        var capture = Capture();
        f.Scene.Post.Quality.Shadows.PointShadows.MaxStaticRebuildsPerFrame = 1;
        void Extra(Scene3D s) => s.AddLight(new(0, 2, 0), KhaozEngine.Primitives.Color.White, 6, 1, LightShadow.Static(2));
        f.Render(ShadowStageProbeMode.Counts, foliage: foliage, extraQueue: Extra, attribution: capture);
        var snapshot = f.Render(ShadowStageProbeMode.Counts, foliage: foliage, extraQueue: Extra, attribution: capture);
        var range = snapshot.PointAttribution!.Value;
        var rows = Enumerable.Range(range.RowStart, range.RowCount).Select(capture.GetRow).ToArray();
        Assert.Equal(new long[] { 1, 2, 2 }, rows.Select(r => r.Key));
        Assert.Equal(new[] { 1, 1, 2 }, rows.Select(r => r.Mode));
        Assert.Equal(new[] { 0, -1, 1 }, rows.Select(r => r.SelectedOrdinal));
        var counts = snapshot.Counters!.Value;
        Assert.Equal(counts.PointFaceDrawCalls, rows.Sum(r => r.RigidDraws + r.FoliageDraws + r.DynamicSkinnedDraws + r.TransientSkinnedDraws));
        Assert.Equal(counts.PointRigidIssuedInstances, rows.Sum(r => r.RigidInstances));
        Assert.Equal(counts.PointFoliageIssuedInstances, rows.Sum(r => r.FoliageInstances));
        var deferred = f.Render(ShadowStageProbeMode.Counts, foliage: foliage, extraQueue: Extra, attribution: capture);
        var row = capture.GetRow(deferred.PointAttribution!.Value.RowStart + 1);
        Assert.True(row.PriorDirty); Assert.False(row.Mismatch); Assert.True(row.DirtyAfter);
        Assert.Equal(0, row.SelectedOrdinal); Assert.True(row.Completed);
        Assert.Equal(Reconstruct(capture, row), row.NewSignature);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_atlas_and_disabled_are_distinct_from_empty_observation(bool disabled)
    {
        using var f = new ShadowStageProbeFixture();
        f.Scene.Post.Quality.Shadows.PointShadows.Enabled = !disabled;
        var snapshot = f.Render(ShadowStageProbeMode.Counts, attribution: Capture());
        Assert.Equal(disabled ? PointAttributionAvailability.Disabled : PointAttributionAvailability.MissingAtlas,
            snapshot.PointAttribution!.Value.Availability);
    }

    internal static ulong Reconstruct(PointShadowAttributionCapture capture, PointAttributionRow row)
    {
        ulong hash = 0x27d4eb2f165667c5;
        static void Mix(ref ulong h, ulong word)
        {
            h = unchecked(h + word * 0xc2b2ae3d27d4eb4f);
            h = (h << 31) | (h >> 33);
            h = unchecked(h * 0x9e3779b185ebca87);
        }
        for (int i = 0; i < 12; i++) Mix(ref hash, capture.GetInput(row.Input).Header[i]);
        var span = capture.GetSpan(row.Span);
        for (int i = 0; i < span.Count; i++)
        {
            var c = capture.GetCaster(capture.GetContributor(span.Start + i).Caster);
            Mix(ref hash, (uint)c.MeshIndex | (ulong)(uint)c.MeshGeneration << 32);
            if (c.Kind == 0) Mix(ref hash, c.DissolveWord | (ulong)c.CastKind << 32);
            for (int w = 0; w < 16; w += 2) Mix(ref hash, c.Matrix[w] | (ulong)c.Matrix[w + 1] << 32);
        }
        return hash;
    }
}
