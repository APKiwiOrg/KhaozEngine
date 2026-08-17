using System;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Headless coverage for the last-frame shadow snapshot (<see cref="ShadowPassDiagnostics"/>), the instrument
    /// issue #410 forwards to a consumer's field telemetry. Two things are pinned here, both without a GPU.
    /// <para>
    /// First, the snapshot must NAME the responsible reason rather than only report that the pass was dirty. Each
    /// row below pins two things SEPARATELY: that the dirty predicate would render for exactly that reason, and
    /// that the struct carries exactly that bit. The wiring between the pass and the snapshot needs a live device
    /// and is proved only by <c>ShadowPassDiagnosticsGpuTests</c>, not here. A snapshot that could not distinguish
    /// a moving sun from a present skinned caster would answer the wrong question in the field, which is the whole
    /// reason the struct exists rather than one dirty flag.
    /// </para>
    /// <para>
    /// Second, the count surface must behave as a value: the per-cascade spans are copied into the struct, an out
    /// of range cascade reads 0, and reading a snapshot allocates nothing (the shadow dirty check is documented
    /// allocation-free, and an instrument that allocated per frame would change the thing it measures).
    /// </para>
    /// <para>
    /// The end-to-end proof that each bit matches what the real pass DID is
    /// KhaozEngine.Tests.Gpu.ShadowPassDiagnosticsGpuTests.
    /// </para>
    /// </summary>
    public sealed class ShadowPassDiagnosticsTests
    {
        static ShadowPassDiagnostics Snapshot(bool rendered = true, bool hadPrevious = true,
            bool anySkinnedCaster = false, bool skinnedCastersCleared = false, bool resolutionChanged = false,
            bool lightMatrixChanged = false, bool casterDataChanged = false, int skinnedCasterCount = 0,
            int cascadeCount = 4, int[]? rigidSpans = null, int rigidDrawCalls = 0, int skinnedDrawCalls = 0)
            => new(active: true, rendered: rendered, skipped: !rendered, hadPrevious: hadPrevious,
                anySkinnedCaster: anySkinnedCaster, skinnedCastersCleared: skinnedCastersCleared,
                resolutionChanged: resolutionChanged,
                lightMatrixChanged: lightMatrixChanged, casterDataChanged: casterDataChanged,
                skinnedCasterCount: skinnedCasterCount, cascadeCount: cascadeCount,
                rigidSpanCounts: rigidSpans ?? Array.Empty<int>(),
                rigidDrawCalls: rigidDrawCalls, skinnedDrawCalls: skinnedDrawCalls);

        [Fact]
        public void First_frame_names_only_the_missing_previous_atlas()
        {
            ShadowPassDiagnostics d = Snapshot(hadPrevious: false);
            Assert.True(Scene3D.ShadowDepthPassDirty(hadPrevious: false, anySkinnedCaster: false,
                skinnedCastersCleared: false, resolutionChanged: false, lightMatrixChanged: false, casterDataChanged: false));
            Assert.True(d.Rendered);
            Assert.False(d.HadPrevious);
            Assert.False(d.AnySkinnedCaster);
            Assert.False(d.SkinnedCastersCleared);
            Assert.False(d.ResolutionChanged);
            Assert.False(d.LightMatrixChanged);
            Assert.False(d.CasterDataChanged);
        }

        [Fact]
        public void Skinned_caster_is_the_only_reason_a_stationary_scene_names()
        {
            // The load-bearing row, and the current (suspected wasteful) behaviour issue #410 is measuring: nothing
            // moved, no matrix changed, no caster changed, and the pass still re-records because a skinned caster
            // is present at all. Bone palettes are not hashed, so this bit alone forces every frame dirty.
            Assert.True(Scene3D.ShadowDepthPassDirty(hadPrevious: true, anySkinnedCaster: true,
                skinnedCastersCleared: false, resolutionChanged: false, lightMatrixChanged: false, casterDataChanged: false));
            ShadowPassDiagnostics d = Snapshot(anySkinnedCaster: true, skinnedCasterCount: 3);
            Assert.True(d.Rendered);
            Assert.True(d.AnySkinnedCaster);
            Assert.False(d.SkinnedCastersCleared);
            Assert.Equal(3, d.SkinnedCasterCount);
            Assert.False(d.ResolutionChanged);
            Assert.False(d.LightMatrixChanged);
            Assert.False(d.CasterDataChanged);
        }

        [Fact]
        public void A_vanished_skinned_caster_is_the_only_reason_it_names()
        {
            // The #23 row, and the only reason bit that can be set while AnySkinnedCaster is CLEAR: the skinned
            // casters the last rendered pass drew are all gone, so the pass renders once to lift their shadows off
            // the atlas it would otherwise reuse. A field trace that saw Rendered with every reason clear would be
            // reading a snapshot that cannot explain its own decision, which is what this bit prevents.
            Assert.True(Scene3D.ShadowDepthPassDirty(hadPrevious: true, anySkinnedCaster: false,
                skinnedCastersCleared: true, resolutionChanged: false, lightMatrixChanged: false,
                casterDataChanged: false));
            ShadowPassDiagnostics d = Snapshot(skinnedCastersCleared: true);
            Assert.True(d.Rendered);
            Assert.True(d.SkinnedCastersCleared);
            Assert.False(d.AnySkinnedCaster);
            Assert.Equal(0, d.SkinnedCasterCount);
            Assert.False(d.ResolutionChanged);
            Assert.False(d.LightMatrixChanged);
            Assert.False(d.CasterDataChanged);
        }

        [Fact]
        public void Resolution_change_is_the_only_reason_it_names()
        {
            // The one reason the live API cannot reach: ShadowSettings.ShadowMapResolution is a construction-time
            // knob (ThrowIfAtlasCommitted), so no running scene can change it between two passes. The bit is still
            // read and forwarded, so it is pinned here rather than left untested.
            Assert.True(Scene3D.ShadowDepthPassDirty(hadPrevious: true, anySkinnedCaster: false,
                skinnedCastersCleared: false, resolutionChanged: true, lightMatrixChanged: false, casterDataChanged: false));
            ShadowPassDiagnostics d = Snapshot(resolutionChanged: true);
            Assert.True(d.ResolutionChanged);
            Assert.False(d.AnySkinnedCaster);
            Assert.False(d.SkinnedCastersCleared);
            Assert.False(d.LightMatrixChanged);
            Assert.False(d.CasterDataChanged);
        }

        [Fact]
        public void Light_matrix_change_is_the_only_reason_it_names()
        {
            Assert.True(Scene3D.ShadowDepthPassDirty(hadPrevious: true, anySkinnedCaster: false,
                skinnedCastersCleared: false, resolutionChanged: false, lightMatrixChanged: true, casterDataChanged: false));
            ShadowPassDiagnostics d = Snapshot(lightMatrixChanged: true);
            Assert.True(d.LightMatrixChanged);
            Assert.False(d.AnySkinnedCaster);
            Assert.False(d.SkinnedCastersCleared);
            Assert.False(d.ResolutionChanged);
            Assert.False(d.CasterDataChanged);
        }

        [Fact]
        public void Caster_data_change_is_the_only_reason_it_names()
        {
            Assert.True(Scene3D.ShadowDepthPassDirty(hadPrevious: true, anySkinnedCaster: false,
                skinnedCastersCleared: false, resolutionChanged: false, lightMatrixChanged: false, casterDataChanged: true));
            ShadowPassDiagnostics d = Snapshot(casterDataChanged: true);
            Assert.True(d.CasterDataChanged);
            Assert.False(d.AnySkinnedCaster);
            Assert.False(d.SkinnedCastersCleared);
            Assert.False(d.ResolutionChanged);
            Assert.False(d.LightMatrixChanged);
        }

        [Fact]
        public void Clean_stationary_frame_names_no_reason_and_reports_no_work()
        {
            Assert.False(Scene3D.ShadowDepthPassDirty(hadPrevious: true, anySkinnedCaster: false,
                skinnedCastersCleared: false, resolutionChanged: false, lightMatrixChanged: false, casterDataChanged: false));
            ShadowPassDiagnostics d = Snapshot(rendered: false);
            Assert.True(d.Skipped);
            Assert.False(d.Rendered);
            Assert.False(d.AnySkinnedCaster);
            Assert.False(d.SkinnedCastersCleared);
            Assert.False(d.ResolutionChanged);
            Assert.False(d.LightMatrixChanged);
            Assert.False(d.CasterDataChanged);
            Assert.Equal(0, d.TotalRigidSpanCount);
            Assert.Equal(0, d.TotalDrawCalls);
        }

        [Fact]
        public void Default_snapshot_is_inactive_and_empty()
        {
            ShadowPassDiagnostics d = default;
            Assert.False(d.Active);
            Assert.False(d.Rendered);
            Assert.Equal(0, d.CascadeCount);
            Assert.Equal(0, d.TotalRigidSpanCount);
            Assert.Equal(0, d.TotalDrawCalls);
            Assert.Equal(0, d.RigidSpanCount(0));
        }

        [Fact]
        public void Per_cascade_spans_are_copied_in_and_summed()
        {
            ShadowPassDiagnostics d = Snapshot(rigidSpans: new[] { 45, 153, 221, 285 },
                rigidDrawCalls: 700, skinnedDrawCalls: 76);
            Assert.Equal(45, d.RigidSpanCount(0));
            Assert.Equal(153, d.RigidSpanCount(1));
            Assert.Equal(221, d.RigidSpanCount(2));
            Assert.Equal(285, d.RigidSpanCount(3));
            Assert.Equal(704, d.TotalRigidSpanCount);
            Assert.Equal(776, d.TotalDrawCalls);
        }

        [Fact]
        public void Out_of_range_cascade_reads_zero()
        {
            ShadowPassDiagnostics d = Snapshot(rigidSpans: new[] { 7, 8 });
            Assert.Equal(7, d.RigidSpanCount(0));
            Assert.Equal(8, d.RigidSpanCount(1));
            Assert.Equal(0, d.RigidSpanCount(2));               // fewer counts supplied than MaxCascades
            Assert.Equal(0, d.RigidSpanCount(ShadowSettings.MaxCascades));
            Assert.Equal(0, d.RigidSpanCount(-1));
            Assert.Equal(15, d.TotalRigidSpanCount);
        }

        [Fact]
        public void Snapshot_is_a_value_copy_not_a_view()
        {
            // The counts must survive the array the scene reuses being overwritten next frame, or a consumer that
            // holds a snapshot for a frame reads the wrong numbers.
            int[] live = { 1, 2, 3, 4 };
            ShadowPassDiagnostics d = Snapshot(rigidSpans: live);
            live[0] = 999;
            live[3] = 999;
            Assert.Equal(1, d.RigidSpanCount(0));
            Assert.Equal(4, d.RigidSpanCount(3));
            Assert.Equal(10, d.TotalRigidSpanCount);
        }
    }

    /// <summary>
    /// The allocation half of <see cref="ShadowPassDiagnosticsTests"/>, in the AllocSensitive collection because
    /// <c>GC.GetAllocatedBytesForCurrentThread()</c> cannot be measured beside parallel work.
    /// </summary>
    [Collection("AllocSensitive")]
    public sealed class ShadowPassDiagnosticsAllocTests
    {
        [Fact]
        public void Building_and_reading_a_snapshot_allocates_nothing()
        {
            int[] spans = { 45, 153, 221, 285 };
            ShadowPassDiagnostics sink = default;
            int total = 0;
            AllocAssert.NoPerCallAllocation("ShadowPassDiagnostics build + read", () =>
            {
                for (int i = 0; i < 256; i++)
                {
                    sink = new ShadowPassDiagnostics(active: true, rendered: true, skipped: false, hadPrevious: true,
                        anySkinnedCaster: true, skinnedCastersCleared: false, resolutionChanged: false,
                        lightMatrixChanged: true, casterDataChanged: true, skinnedCasterCount: 19, cascadeCount: 4,
                        rigidSpanCounts: spans, rigidDrawCalls: 700, skinnedDrawCalls: 76);
                    total += sink.TotalRigidSpanCount + sink.TotalDrawCalls + sink.RigidSpanCount(2);
                }
            });
            Assert.True(total > 0);
            Assert.True(sink.Rendered);
        }
    }
}
