using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class PointShadowAttributionInputTests
{
    static PointShadowAttributionCapture Capture() => new(Guid.NewGuid(), new(24, 8, 128, 128, 1024, 128, 64, 128));
    static PointAttributionRow Row(PointShadowAttributionCapture c, ShadowStageProbeSnapshot s) => c.GetRow(s.PointAttribution!.Value.RowStart);
    static PointAttributionCaster[] Casters(PointShadowAttributionCapture c, PointAttributionRow r)
    {
        var span = c.GetSpan(r.Span);
        return Enumerable.Range(span.Start, span.Count).Select(i => c.GetCaster(c.GetContributor(i).Caster)).ToArray();
    }

    [Theory]
    [InlineData(0f, 0f, false, 1u, 0u)]
    [InlineData(.4f, 0f, false, 2u, 6u)]
    [InlineData(.401f, 0f, false, 2u, 6u)]
    [InlineData(.5f, 1f, false, 2u, 264u)]
    [InlineData(.5f, 0f, true, 3u, 8u)]
    public void Cast_kind_dissolve_and_complement_are_the_consumed_words(float dissolve, float complement,
        bool inverted, uint kind, uint word)
    {
        using var f = new ShadowStageProbeFixture(); var c = Capture();
        void Queue(Scene3D s) => s.Draw(new RigidInstanceDraw(f.Mesh, Matrix4x4.Identity)
        { Dissolve = dissolve, DissolveComplement = complement, InvertShadowDissolve = inverted });
        f.Render(ShadowStageProbeMode.Counts, rigid: false, dynamicLight: false, extraQueue: Queue, attribution: c);
        var row = Row(c, f.Render(ShadowStageProbeMode.Counts, rigid: false, dynamicLight: false, extraQueue: Queue, attribution: c));
        var caster = Assert.Single(Casters(c, row));
        Assert.Equal(kind, caster.CastKind); Assert.Equal(word, caster.DissolveWord); Assert.Equal(0UL, caster.MotionKey);
        Assert.Equal(PointShadowAttributionProducerTests.Reconstruct(c, row), row.NewSignature);
    }

    [Fact]
    public void Reordered_identical_sets_membership_and_duplicate_keys_preserve_exact_inputs()
    {
        using var f = new ShadowStageProbeFixture(); var c = Capture();
        bool reverse = false, omit = false;
        void Queue(Scene3D s)
        {
            foreach (float x in reverse ? new[] { 1f, 0f } : new[] { 0f, 1f })
                if (!omit || x == 0) s.Draw(new RigidInstanceDraw(f.Mesh, Matrix4x4.CreateTranslation(x, 0, 0)) { Motion = MotionKey.From(7) });
        }
        ShadowStageProbeSnapshot Render() => f.Render(ShadowStageProbeMode.Counts, rigid: false, dynamicLight: false, extraQueue: Queue, attribution: c);
        Render(); var before = Row(c, Render()); reverse = true; var after = Row(c, Render());
        Assert.Equal(new uint[] { 0, 0x3f800000 }, Casters(c, before).Select(x => x.Matrix[12]));
        Assert.Equal(new uint[] { 0x3f800000, 0 }, Casters(c, after).Select(x => x.Matrix[12]));
        Assert.All(Casters(c, after), caster => Assert.Equal(7UL, caster.MotionKey));
        Assert.True(after.Mismatch); Assert.NotEqual(before.Span, after.Span);
        omit = true; var removed = Row(c, Render()); Assert.Single(Casters(c, removed)); Assert.True(removed.Mismatch);
    }

    [Fact]
    public void Raw_light_jitter_quantized_header_interior_version_and_reused_slots_are_observed()
    {
        using var f = new ShadowStageProbeFixture(); var c = Capture();
        float x = 0; long key = 11;
        void Queue(Scene3D s) => s.AddLight(new(x, 2, 0), Color.White, 6, 1, LightShadow.Static(key));
        ShadowStageProbeSnapshot Render() => f.Render(ShadowStageProbeMode.Counts, staticLight: false, dynamicLight: false, extraQueue: Queue, attribution: c);
        Render(); var first = Row(c, Render()); x = .0001f; var jitter = Row(c, Render());
        Assert.False(jitter.Mismatch); Assert.NotEqual(c.GetInput(first.Input).Raw[0], c.GetInput(jitter.Input).Raw[0]);
        Assert.Equal(0UL, c.GetInput(jitter.Input).Header[1]);
        x = .001f; var moved = Row(c, Render()); Assert.True(moved.Mismatch); Assert.Equal(1UL, c.GetInput(moved.Input).Header[1]);
        f.Scene.InteriorCut = InteriorCutShadowTests.Field(); var interior = Row(c, Render());
        Assert.NotEqual(c.GetInput(moved.Input).Header[0], c.GetInput(interior.Input).Header[0]);
        f.Render(ShadowStageProbeMode.Counts, point: false, attribution: c);
        key = 12; var reused = Row(c, Render());
        Assert.Equal(PointAttributionSlotOutcome.Acquired, reused.SlotOutcome); Assert.True(reused.PriorDirty);
        Assert.Equal(int.MinValue, reused.PriorLastRenderedFrame); Assert.Equal(12, reused.Key);
    }

    [Fact]
    public void Mesh_generation_and_foliage_rest_changes_survive_scene_disposal()
    {
        var c = Capture();
        using (var f = new ShadowStageProbeFixture())
        {
            var mesh = f.Mesh;
            void Queue(Scene3D s) => s.Draw(mesh, Matrix4x4.Identity);
            ShadowStageProbeSnapshot Render(FoliageBatch? foliage = null) => f.Render(ShadowStageProbeMode.Counts,
                rigid: false, dynamicLight: false, extraQueue: Queue, foliage: foliage, attribution: c);
            Render(); var first = Row(c, Render()); var old = Assert.Single(Casters(c, first));
            f.Scene.UnloadMesh(mesh); mesh = f.Scene.LoadMesh(MeshPrimitives.Box(1));
            var replacement = Row(c, Render()); var changed = Assert.Single(Casters(c, replacement));
            Assert.Equal(old.MeshIndex, changed.MeshIndex); Assert.NotEqual(old.MeshGeneration, changed.MeshGeneration);
            using var a = f.Scene.CreateFoliageBatch([new(mesh, Matrix4x4.Identity, .1f, default, true)]);
            var foliageA = Row(c, Render(a));
            using var b = f.Scene.CreateFoliageBatch([new(mesh, Matrix4x4.CreateTranslation(.1f, 0, 0), .1f, default, true)]);
            var foliageB = Row(c, Render(b)); Assert.True(foliageB.Mismatch);
            Assert.NotEqual(Casters(c, foliageA)[1].Matrix[12], Casters(c, foliageB)[1].Matrix[12]);
        }
        c.Freeze(); Assert.True(c.GetFrame(c.FrameCount - 1).Complete); Assert.True(c.CasterCount > 0);
    }
}
