using System;
using System.Linq;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class MetalCascadeDiscardTests : IDisposable
{
    const int OnBlock = 72, OffBlock = 144;
    static readonly IntPtr Buffer = new(1);
    static readonly IntPtr Descriptor = new(7);

    readonly PassTimingTestNative _native = new();
    readonly MetalPassTimingCapture _timing;

    public MetalCascadeDiscardTests()
    {
        _timing = new(_native, 1, 64, 4, labelEvents: 64);
    }

    public void Dispose() => _timing.Dispose();

    void OpenScope(int callback = OnBlock, string id = MetalPassLabels.DirectionalPass)
    {
        _timing.Boundary(callback, 10);
        _timing.ObservePass(Buffer, callback, PassLabelEventKind.Begin, id, 0);
    }

    void EndScope(int callback = OnBlock, string id = MetalPassLabels.DirectionalPass)
        => _timing.ObservePass(Buffer, callback, PassLabelEventKind.End, id, 0);

    IntPtr Open(int callback = OnBlock) => _timing.Begin(MetalEncoderKind.Render, Buffer, Descriptor, callback, true);

    PassTimingEncoder Record()
    {
        _timing.Complete(Buffer, true);
        return Assert.Single(_timing.Snapshot().Encoders.Records);
    }

    [Fact]
    public void Blocks_start_off_alternate_every_72_callbacks_and_stay_timed()
    {
        _timing.Boundary(0, 10);
        _timing.Boundary(1, 11);
        _timing.Boundary(72, 20);
        _timing.Boundary(144, 30);
        _timing.Boundary(216, 40);
        _timing.End(220, 50);
        var blocks = _timing.ExperimentSnapshot().Blocks;
        Assert.Equal(new[] { false, true, false, true }, blocks.Select(b => b.Discard));
        Assert.Equal(new[] { 0, 72, 144, 216 }, blocks.Select(b => b.StartCallback));
        Assert.Equal(new[] { 72, 144, 216, 220 }, blocks.Select(b => b.EndCallbackExclusive));
        Assert.All(_timing.Snapshot().Blocks, b => Assert.True(b.Timed));
    }

    [Fact]
    public void A_scope_that_ended_before_the_encoder_discards_and_the_descriptor_goes_back_to_store()
    {
        OpenScope();
        IntPtr encoder = Open();
        Assert.Equal("unknown", _native.DescriptorStoresAtOpen.Single());
        Assert.Equal("store", _native.DescriptorStore);
        EndScope();
        Assert.Empty(_native.Resolutions);
        _timing.BeforeEndEncoding(encoder, sampling: true);
        Assert.Equal((encoder, true), _native.Resolutions.Single());
        var record = Record();
        Assert.Equal(PassStoreResolution.DontCare, record.StoreResolution);
        Assert.True(record.DirectionalScope);
        var result = _timing.ExperimentSnapshot();
        Assert.Equal(1, result.OpenedUnknown);
        Assert.Equal(1, result.DontCareApplied);
        Assert.Equal(0, result.UnresolvedEncoders);
    }

    [Fact]
    public void A_nested_scope_is_innermost_when_the_first_draw_opens_the_encoder_and_still_discards()
    {
        OpenScope();
        _timing.ObservePass(Buffer, OnBlock, PassLabelEventKind.Begin, "shadow.rigid.opaque", 0);
        IntPtr encoder = Open();
        _timing.ObservePass(Buffer, OnBlock, PassLabelEventKind.End, "shadow.rigid.opaque", 0);
        // A nested scope ending is not the directional scope ending.
        _timing.BeforeEndEncoding(encoder, sampling: true);
        Assert.Equal((encoder, false), _native.Resolutions.Single());
        var record = Record();
        Assert.Equal("shadow.rigid.opaque", _timing.LabelsSnapshot().Labels[record.LabelId]);
        Assert.Equal(PassStoreResolution.StoreScopeOpen, record.StoreResolution);
    }

    [Fact]
    public void A_pass_split_mid_scope_stores_the_first_encoder_and_discards_only_the_last()
    {
        OpenScope();
        IntPtr first = Open();
        _timing.BeforeEndEncoding(first, sampling: true);
        _native.OpenEncoder = new(2000);
        IntPtr second = Open();
        EndScope();
        _timing.BeforeEndEncoding(second, sampling: true);
        Assert.Equal(new[] { (first, false), (second, true) }, _native.Resolutions);
        _timing.Complete(Buffer, true);
        Assert.Equal(new[] { PassStoreResolution.StoreScopeOpen, PassStoreResolution.DontCare },
            _timing.Snapshot().Encoders.Records.Select(e => e.StoreResolution));
        var result = _timing.ExperimentSnapshot();
        Assert.Equal((2L, 1L, 1L), (result.OpenedUnknown, result.DontCareApplied, result.KeptStoreScopeOpen));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Stopping_or_disposing_with_an_encoder_open_resolves_it_to_store(bool disposed)
    {
        OpenScope();
        IntPtr encoder = Open();
        EndScope();
        if (disposed) _timing.Dispose();
        _timing.BeforeEndEncoding(encoder, sampling: false);
        Assert.Equal((encoder, false), _native.Resolutions.Single());
        var result = _timing.ExperimentSnapshot();
        Assert.Equal((1L, 0L, 0), (result.ResolvedDuringStop, result.DontCareApplied, result.UnresolvedEncoders));
    }

    [Fact]
    public void A_failed_discard_falls_back_to_store_is_counted_and_never_escapes_before_end_encoding()
    {
        _native.ThrowOnDontCare = true;
        OpenScope();
        IntPtr encoder = Open();
        EndScope();
        _timing.BeforeEndEncoding(encoder, sampling: true);
        Assert.Equal((encoder, false), _native.Resolutions.Single());
        Assert.Equal(PassStoreResolution.Failed, Record().StoreResolution);
        Assert.Equal(1, _timing.ExperimentSnapshot().ResolveFailures);
    }

    [Theory]
    [InlineData(false, true, false)] // Off block, directional scope.
    [InlineData(true, false, false)] // On block, another pass.
    [InlineData(true, true, true)]   // On block, no depth or stencil attachment.
    public void Encoders_outside_the_experiment_never_change_store_actions(bool onBlock, bool directional, bool noDepth)
    {
        int callback = onBlock ? OnBlock : OffBlock;
        if (noDepth) _native.Planes = new(false, false);
        OpenScope(callback, directional ? MetalPassLabels.DirectionalPass : "opaque.rigid");
        IntPtr encoder = Open(callback);
        EndScope(callback, directional ? MetalPassLabels.DirectionalPass : "opaque.rigid");
        _timing.BeforeEndEncoding(encoder, sampling: true);
        Assert.Equal("store", _native.DescriptorStoresAtOpen.Single());
        Assert.Empty(_native.Resolutions);
        Assert.Equal(directional && !noDepth, Record().DirectionalScope);
        Assert.Equal(0, _timing.ExperimentSnapshot().OpenedUnknown);
    }

    [Fact]
    public void An_encoder_outside_the_frame_recording_or_outside_sampling_is_left_alone()
    {
        OpenScope();
        _timing.Begin(MetalEncoderKind.Render, new(2), Descriptor, OnBlock, true);
        _timing.Begin(MetalEncoderKind.Render, Buffer, Descriptor, OnBlock, false);
        Assert.All(_native.DescriptorStoresAtOpen, s => Assert.Equal("store", s));
        Assert.Equal(0, _timing.ExperimentSnapshot().OpenedUnknown);
    }

    [Fact]
    public void Off_and_on_blocks_share_one_signature_because_it_is_read_before_the_store_action_changes()
    {
        OpenScope(OffBlock);
        _timing.BeforeEndEncoding(Open(OffBlock), sampling: true);
        EndScope(OffBlock);
        OpenScope(OnBlock);
        _timing.BeforeEndEncoding(Open(OnBlock), sampling: true);
        EndScope(OnBlock);
        _timing.Complete(Buffer, true);
        var result = _timing.Snapshot();
        Assert.Equal(1UL, Assert.Single(result.Signatures).Depth.StoreAction);
        Assert.Equal(1, _timing.ExperimentSnapshot().DirectionalEncodersOff);
    }

    [Fact]
    public void A_refused_descriptor_or_a_nil_encoder_leaves_nothing_to_resolve()
    {
        _native.ThrowOnSetUnknown = true;
        OpenScope();
        IntPtr encoder = Open();
        Assert.Equal("store", _native.DescriptorStoresAtOpen.Single());
        Assert.Equal(1, _timing.ExperimentSnapshot().DescriptorFailures);
        _timing.BeforeEndEncoding(encoder, sampling: true);
        Assert.Empty(_native.Resolutions);
        _native.ThrowOnSetUnknown = false;
        _native.NilEncoder = true;
        Assert.Equal(IntPtr.Zero, Open());
        Assert.Equal("store", _native.DescriptorStore);
        Assert.Equal((0L, 0), (_timing.ExperimentSnapshot().OpenedUnknown, _timing.ExperimentSnapshot().UnresolvedEncoders));
    }

    [Fact]
    public void A_throwing_encoder_factory_still_restores_the_descriptor()
    {
        _native.ThrowOnOpen = true;
        OpenScope();
        Assert.Throws<InvalidOperationException>(() => Open());
        Assert.Equal("unknown", _native.DescriptorStoresAtOpen.Single());
        Assert.Equal("store", _native.DescriptorStore);
        Assert.Equal(0, _timing.ExperimentSnapshot().UnresolvedEncoders);
    }
}
