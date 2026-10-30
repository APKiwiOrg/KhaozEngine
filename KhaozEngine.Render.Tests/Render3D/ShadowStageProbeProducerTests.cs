using System;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class ShadowStageProbeProducerTests
{
    static readonly ShadowStageProbeRequest Request = new(new Guid("a88c28c3-1a1a-455b-9c47-ab8c67f89bf5"), 1, ShadowStageProbeMode.Timings);

    [Fact]
    public void Counts_and_timings_preserve_the_same_queries_signatures_and_draw_submission_order()
    {
        ShadowStageProbeCounts? counts = null;
        string[]? order = null;
        ShadowPassDiagnostics? diagnostics = null;
        long[]? signatures = null;
        foreach (var mode in new[] { ShadowStageProbeMode.Off, ShadowStageProbeMode.Counts, ShadowStageProbeMode.Timings })
        {
            using var f = new ShadowStageProbeFixture();
            f.Render(ShadowStageProbeMode.Off);
            var snapshot = f.Render(mode);
            if (order is not null) Assert.Equal(order, f.Order());
            if (diagnostics.HasValue)
            {
                var previous = diagnostics.Value;
                var current = f.Scene.LastShadowPassDiagnostics;
                Assert.Equal(previous.PointShadowedLights, current.PointShadowedLights);
                Assert.Equal(previous.PointStaticRebuilds, current.PointStaticRebuilds);
                Assert.Equal(previous.PointDynamicRenders, current.PointDynamicRenders);
                Assert.Equal(previous.PointTransientDemand, current.PointTransientDemand);
                Assert.Equal(previous.PointTransientRowsRendered, current.PointTransientRowsRendered);
                Assert.Equal(previous.PointDynamicSkinnedDrawCalls, current.PointDynamicSkinnedDrawCalls);
                Assert.Equal(previous.PointStaticTransientSkinnedDrawCalls, current.PointStaticTransientSkinnedDrawCalls);
                Assert.Equal(previous.PointFaceDrawCalls, current.PointFaceDrawCalls);
                Assert.Equal(previous.PointSlotsInUse, current.PointSlotsInUse);
            }
            if (signatures is not null) Assert.Equal(signatures, f.Signatures());
            signatures = f.Signatures();
            order = f.Order(); diagnostics = f.Scene.LastShadowPassDiagnostics;
            if (mode == ShadowStageProbeMode.Off) { Assert.Null(snapshot.Counters); Assert.Null(snapshot.Timings); continue; }
            Assert.Equal(ShadowStageProbeStatus.Valid, snapshot.Status);
            Assert.Equal(2, snapshot.Counters!.Value.TouchAttempts);
            Assert.Equal(12, snapshot.Counters.Value.PointFaceDrawCalls);
            if (counts.HasValue) Assert.Equal(counts.Value, snapshot.Counters.Value);
            counts = snapshot.Counters;
            Assert.Equal(mode == ShadowStageProbeMode.Timings, snapshot.Timings.HasValue);
            Assert.Equal(0, f.Render(mode).Counters!.Value.CompletedStaticRebuilds);
        }
    }

    [Fact]
    public void Signature_queries_and_rebuilt_row_queries_have_separate_counts()
    {
        using var f = new ShadowStageProbeFixture();
        f.Render(ShadowStageProbeMode.Off);
        var c = f.Render(ShadowStageProbeMode.Counts).Counters!.Value;
        Assert.Equal(1, c.RigidIndexPopulation);
        Assert.Equal(new ShadowStageProbeQueryCounts(1, 1, 1, 1), c.RigidSignatureQueries);
        Assert.Equal(new ShadowStageProbeQueryCounts(2, 2, 2, 2), c.RigidRowQueries);
        Assert.Equal(1, c.FoliageSignatureQueries.Calls);
        Assert.Equal(2, c.FoliageRowQueries.Calls);
        Assert.Equal(0, c.FoliageRowQueries.Accepted);
        var steady = f.Render(ShadowStageProbeMode.Counts).Counters!.Value;
        Assert.Equal(1, steady.RigidSignatureQueries.Calls);
        Assert.Equal(1, steady.RigidRowQueries.Calls);
        Assert.Equal(6, steady.PointFaceDrawCalls);
    }

    [Fact]
    public void Exact_tests_and_accepted_sort_inputs_do_not_collapse_to_one_counter()
    {
        using var f = new ShadowStageProbeFixture();
        // The corner lies inside the query AABB but outside the sphere plus the box radius.
        void Corner(Scene3D scene) => scene.Draw(f.Mesh, Matrix4x4.CreateTranslation(5, .5f, 5));
        f.Render(ShadowStageProbeMode.Off, extraQueue: Corner);
        var c = f.Render(ShadowStageProbeMode.Counts, extraQueue: Corner).Counters!.Value;
        Assert.Equal(2, c.RigidIndexPopulation);
        Assert.Equal(new ShadowStageProbeQueryCounts(1, 2, 1, 1), c.RigidSignatureQueries);
        Assert.Equal(new ShadowStageProbeQueryCounts(2, 4, 2, 2), c.RigidRowQueries);
        Assert.Equal(12, c.PointFaceDrawCalls);
    }

    [Fact]
    public void Full_stream_population_and_selected_prefix_proxies_keep_their_distinct_units()
    {
        using var f = new ShadowStageProbeFixture();
        var motion = new FoliageMotionProfile(.3f, .15f, .4f, .035f);
        using var foliage = f.Scene.CreateFoliageBatch([
            new(f.Mesh, Matrix4x4.Identity, .1f, motion, true),
            new(f.Mesh, Matrix4x4.CreateTranslation(0, 0, 1), .3f, motion, true),
            new(f.Mesh, Matrix4x4.CreateTranslation(0, 0, 2), .7f, motion, true),
            new(f.Mesh, Matrix4x4.CreateTranslation(0, 0, 3), .9f, motion, true)]);
        var half = new FoliageRenderSettings { DrawRadius = 100, QualityDensity = .5f, DistantDensity = .5f };
        f.Render(ShadowStageProbeMode.Off, foliage: foliage, foliageSettings: half);
        var c = f.Render(ShadowStageProbeMode.Counts, foliage: foliage, foliageSettings: half).Counters!.Value;
        Assert.Equal(1, c.FoliageSources); Assert.Equal(1, c.FoliageRanges);
        Assert.Equal(4, c.FoliageStreamProxies); Assert.Equal(2, c.FoliageSelectedProxies);
        Assert.Equal(new ShadowStageProbeQueryCounts(1, 2, 2, 2), c.FoliageSignatureQueries);
        Assert.Equal(new ShadowStageProbeQueryCounts(2, 4, 4, 4), c.FoliageRowQueries);
        Assert.Equal(24, c.PointFoliageIssuedInstances);
    }

    [Fact]
    public void Duplicate_light_keys_report_lookup_phase_outcomes_without_claiming_new_ownership()
    {
        using var f = new ShadowStageProbeFixture();
        f.Render(ShadowStageProbeMode.Off, dynamicLight: false, duplicate: true);
        var c = f.Render(ShadowStageProbeMode.Counts, dynamicLight: false, duplicate: true).Counters!.Value;
        Assert.Equal(2, c.TouchAttempts); Assert.Equal(0, c.TouchHits);
        Assert.Equal(2, c.AcquireAttempts); Assert.Equal(2, c.AcquireSuccesses); Assert.Equal(0, c.AcquireRefusals);
        Assert.Equal(2, c.Signatures); Assert.Equal(1, c.PointSlotsInUse);
        Assert.Equal(1, f.Scene.LastShadowPassDiagnostics.PointSlotsInUse);
    }

    [Fact]
    public void Populated_then_no_requests_no_atlas_empty_foliage_and_skipped_cascades_do_not_reuse_counts()
    {
        using var f = new ShadowStageProbeFixture(directional: true);
        using var foliage = f.Foliage();
        f.Render(ShadowStageProbeMode.Off, foliage: foliage);
        var populated = f.Render(ShadowStageProbeMode.Counts, foliage: foliage).Counters!.Value;
        Assert.Equal(2, populated.FoliageSelectedProxies);
        var empty = f.Render(ShadowStageProbeMode.Counts, point: false, rigid: false).Counters!.Value;
        Assert.False(empty.HasPointRequests); Assert.Equal(0, empty.RigidIndexPopulation);
        Assert.Equal(0, empty.PackedRows); Assert.Equal(0, empty.PointFaceDrawCalls);
        Assert.False(empty.FoliageIndexBuilt); Assert.Equal(0, empty.FoliageSelectedProxies);
        var clean = f.Render(ShadowStageProbeMode.Counts, point: false, rigid: false).Counters!.Value;
        Assert.True(clean.DirectionalSkipped); Assert.Equal(0, clean.DirectionalSelectedInstances);
        Assert.Equal(0, clean.DirectionalRigidDrawCalls); Assert.Equal(0, clean.DirectionalFoliageDrawCalls);
        using var cold = new ShadowStageProbeFixture();
        var noAtlas = cold.Render(ShadowStageProbeMode.Counts).Counters!.Value;
        Assert.True(noAtlas.HasPointRequests); Assert.False(noAtlas.PointAtlasAvailable);
        Assert.Equal(0, noAtlas.RigidIndexPopulation); Assert.Equal(0, noAtlas.RigidSignatureQueries.Calls);
        var noFoliage = f.Render(ShadowStageProbeMode.Counts).Counters!.Value;
        Assert.True(noFoliage.FoliageIndexBuilt); Assert.Equal(0, noFoliage.FoliageSelectedProxies);
        Assert.Equal(0, noFoliage.PointFoliageFaceDrawCalls);
        Assert.True(noFoliage.DirectionalSelectedInstances > 0);
        var cleanPopulated = f.Render(ShadowStageProbeMode.Counts).Counters!.Value;
        Assert.True(cleanPopulated.DirectionalSkipped);
        Assert.Equal(1, cleanPopulated.DirectionalInvalidationCasters);
        Assert.Equal(0, cleanPopulated.DirectionalSelectedInstances);
        Assert.Equal(0, cleanPopulated.DirectionalRigidDrawCalls);
        Assert.Equal(2, populated.FoliageSelectedProxies);
    }

    [Fact]
    public void Saved_payloads_are_independent_of_reused_scene_and_query_scratch()
    {
        using var f = new ShadowStageProbeFixture();
        using var foliage = f.Foliage();
        f.Render(ShadowStageProbeMode.Off, foliage: foliage);
        var saved = f.Render(ShadowStageProbeMode.Timings, foliage: foliage);
        f.Scene.DebugPointCasterSpans(new Vector3(1000, 0, 0), 1);
        f.Render(ShadowStageProbeMode.Counts, point: false, rigid: false);
        Assert.Equal(2, saved.Counters!.Value.FoliageSelectedProxies);
        Assert.Equal(new ShadowStageProbeQueryCounts(1, 1, 1, 1), saved.Counters.Value.RigidSignatureQueries);
        Assert.Equal(24, saved.Counters.Value.PointFaceDrawCalls);
        Assert.True(saved.Timings!.Value.TimestampReads > 0);
    }

    [Theory]
    [InlineData(true, ShadowStageFoliageBuildOrigin.Signature)]
    [InlineData(false, ShadowStageFoliageBuildOrigin.RowPreparation)]
    public void Lazy_foliage_build_records_its_signature_or_row_preparation_origin(bool statics, ShadowStageFoliageBuildOrigin origin)
    {
        using var f = new ShadowStageProbeFixture();
        using var foliage = f.Foliage();
        f.Render(ShadowStageProbeMode.Off, foliage: foliage, staticLight: statics);
        var c = f.Render(ShadowStageProbeMode.Counts, foliage: foliage, staticLight: statics).Counters!.Value;
        Assert.Equal(origin, c.FoliageBuildOrigin);
        Assert.Equal(1, c.FoliageSources); Assert.Equal(1, c.FoliageRanges);
        Assert.Equal(2, c.FoliageStreamProxies); Assert.Equal(2, c.FoliageSelectedProxies);
    }

    [Fact]
    public void Selected_rows_instances_and_draw_calls_keep_their_distinct_units()
    {
        using var f = new ShadowStageProbeFixture();
        using var foliage = f.Foliage();
        f.Render(ShadowStageProbeMode.Off, foliage: foliage);
        var c = f.Render(ShadowStageProbeMode.Counts, foliage: foliage).Counters!.Value;
        Assert.Equal(1, c.SelectedStaticRows); Assert.Equal(1, c.SelectedDynamicRows); Assert.Equal(2, c.PackedRows);
        Assert.Equal(12, c.PointRigidIssuedInstances); Assert.Equal(24, c.PointFoliageIssuedInstances);
        Assert.Equal(12, c.PointFoliageFaceDrawCalls); Assert.Equal(24, c.PointFaceDrawCalls);
    }

    [Fact]
    public void Exact_clock_ticks_preserve_inclusive_nesting_and_zero_start_timestamp()
    {
        using var f = new ShadowStageProbeFixture();
        long next = 0;
        f.Scene.SetShadowStageProbeClock(() => next++, 1000);
        var snapshot = f.Render(ShadowStageProbeMode.Timings, point: false, rigid: false);
        var t = snapshot.Timings!.Value;
        Assert.Equal(8, t.TimestampReads); Assert.Equal(1000, t.Frequency);
        Assert.Equal(7, t.ScenePrefix); Assert.Equal(1, t.CascadeFit);
        Assert.Equal(1, t.PointRequests); Assert.Equal(1, t.PointPreparation);
        Assert.Null(t.RigidIndexBuild); Assert.Null(t.DirectionalRecording);
        Assert.Equal(ShadowStageProbeStatus.Valid, snapshot.Status);
    }

    [Theory]
    [InlineData(ShadowStageProbeMode.Off)]
    [InlineData(ShadowStageProbeMode.Counts)]
    public void Off_and_counts_modes_do_not_read_the_new_producer_clock(ShadowStageProbeMode mode)
    {
        using var f = new ShadowStageProbeFixture();
        f.Scene.SetShadowStageProbeClock(() => throw new InvalidOperationException("clock must stay unused"), 1000);
        var snapshot = f.Render(mode);
        Assert.Null(snapshot.Timings);
        if (mode == ShadowStageProbeMode.Counts) Assert.Equal(1, snapshot.Counters!.Value.StaticRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Producer_deactivates_after_success_or_prepare_failure(bool fail)
    {
        using var f = new ShadowStageProbeFixture();
        long reads = 0;
        f.Scene.SetShadowStageProbeClock(() => reads++, 1000);
        f.Frame.BeginShadowStageCallback(Request);
        var original = new InvalidOperationException("planned preparation failure");
        void Queue(Scene3D scene)
        {
            scene.PreparePointShadowRequests(Vector3.Zero);
            Assert.Equal(2, reads);
            if (fail) throw original;
        }
        if (fail)
            Assert.Same(original, Assert.Throws<InvalidOperationException>(() => GameApp3D.PrepareProbedScene(f.Scene, Queue, f.Frame)));
        else
        {
            GameApp3D.PrepareProbedScene(f.Scene, Queue, f.Frame);
            f.Commands.Begin();
            f.Scene.RenderInternal(f.Commands, 32, 24, f.Target);
            f.Commands.End();
            f.Scene.PublishShadowStageProbe(f.Frame);
            Assert.Equal(Request, f.Frame.CompletedShadowStageCallback.Snapshot!.Value.Request);
        }
        long finished = reads;
        f.Scene.PreparePointShadowRequests(Vector3.Zero);
        Assert.Equal(finished, reads);
    }

    [Fact]
    public void Producer_deactivates_after_render_failure_and_preserves_the_original_exception()
    {
        using var f = new ShadowStageProbeFixture();
        var original = new InvalidOperationException("planned render clock failure");
        int reads = 0;
        f.Scene.SetShadowStageProbeClock(() => { reads++; throw original; }, 1000);
        f.Frame.BeginShadowStageCallback(Request);
        GameApp3D.PrepareProbedScene(f.Scene, _ => { }, f.Frame);
        f.Commands.Begin();
        try
        {
            Assert.Same(original, Assert.Throws<InvalidOperationException>(() =>
                Render3DSurface.RecordSceneOutput(f.Scene, f.Frame, null, f.Target)));
        }
        finally { f.Commands.End(); }
        Assert.Equal(1, reads);
        f.Scene.PreparePointShadowRequests(Vector3.Zero);
        Assert.Equal(1, reads);
        Assert.Null(f.Frame.CompletedShadowStageCallback.Snapshot);
        Assert.False(f.Frame.CompletedShadowStageCallback.Completed);
    }

    [Fact]
    public void A_negative_elapsed_interval_is_invalid_instead_of_a_plausible_duration()
    {
        using var f = new ShadowStageProbeFixture();
        long next = 100;
        f.Scene.SetShadowStageProbeClock(() => next--, 1000);
        Assert.Equal(ShadowStageProbeStatus.InvalidClock, f.Render(ShadowStageProbeMode.Timings).Status);
    }
}
