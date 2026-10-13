using System;
using System.Numerics;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

public sealed class NativeGestureLifetimeTests
{
    static PointerGestureSample End => new(PointerGestureKind.Magnify, PointerGestureSource.MacOSNative,
        PointerGestureInputPhase.Ended, new Vector2(12, 30));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_or_partial_install_releases_storage_and_falls_back(bool throws)
    {
        var backend = new Registration { InstallResult = false, ThrowDuringInstall = throws };
        var input = new InputAccumulator();
        using var owner = new NativeGestureRegistration(7, backend, input, () => true);
        Assert.False(owner.IsInstalled);
        Assert.True(backend.StorageReleased);
        owner.OnSilkScroll(new Vector2(2, 3), new Vector2(10, 20), true);
        var frame = input.Snapshot(Vector2.Zero, true, 800, 600);
        Assert.Single(frame.Gestures.Samples);
        Assert.Equal(PointerGestureSource.ModifierWheelCompatibility, frame.Gestures.Samples[0].Source);
        Assert.Equal(2, frame.HorizontalScrollDelta);
        Assert.Equal(3, frame.ScrollDelta);
    }

    [Fact]
    public void Disposal_is_idempotent_and_late_callbacks_do_not_deliver()
    {
        var backend = new Registration();
        var input = new InputAccumulator();
        var owner = new NativeGestureRegistration(7, backend, input, () => true);
        owner.Dispose();
        owner.Dispose();
        owner.Invoke(() => owner.Deliver(7, End));
        Assert.Empty(input.Snapshot(Vector2.Zero, true, 800, 600).Gestures.Samples);
        Assert.True(backend.StorageReleased);
        Assert.Equal(1, backend.RemovalCount);
    }

    [Fact]
    public void Removal_failure_retains_storage_until_a_later_success()
    {
        var backend = new Registration { RemoveResult = false };
        var input = new InputAccumulator();
        var owner = new NativeGestureRegistration(7, backend, input, () => true);
        owner.Dispose();
        Assert.False(backend.StorageReleased);
        owner.Invoke(() => owner.Deliver(7, End));
        Assert.Empty(input.Snapshot(Vector2.Zero, true, 800, 600).Gestures.Samples);
        backend.RemoveResult = true;
        owner.Dispose();
        Assert.True(backend.StorageReleased);
    }

    [Fact]
    public void Reentrant_removal_waits_for_the_outermost_callback_to_unwind()
    {
        var backend = new Registration();
        var input = new InputAccumulator();
        var owner = new NativeGestureRegistration(7, backend, input, () => true);
        bool prematurelyReleased = false;
        owner.Invoke(() =>
        {
            owner.Invoke(() => owner.Dispose());
            prematurelyReleased = backend.StorageReleased;
        });
        Assert.False(prematurelyReleased);
        Assert.True(backend.StorageReleased);
    }

    [Fact]
    public void Only_matching_window_receives_zero_delta_lifecycle_events()
    {
        var input = new InputAccumulator();
        bool accepts = true;
        using var owner = new NativeGestureRegistration(7, new Registration(), input, () => accepts);
        owner.Invoke(() => owner.Deliver(8, End));
        owner.Invoke(() => owner.Deliver(7, new PointerGestureSample(PointerGestureKind.Magnify,
            PointerGestureSource.MacOSNative, PointerGestureInputPhase.Began, new Vector2(12, 30))));
        owner.Invoke(() => owner.Deliver(7, End));
        accepts = false;
        owner.Invoke(() => owner.Deliver(7, End));
        var samples = input.Snapshot(Vector2.Zero, true, 800, 600).Gestures.Samples;
        Assert.Equal(2, samples.Count);
        Assert.Equal(PointerGestureInputPhase.Began, samples[0].Phase);
        Assert.Equal(End, samples[1]);
    }

    [Fact]
    public void Native_stream_and_silk_scroll_do_not_duplicate_samples()
    {
        var input = new InputAccumulator();
        using var owner = new NativeGestureRegistration(7, new Registration(), input, () => true);
        var sample = NativeGestureNormalization.Wheel(new Vector2(2, 3), new Vector2(10, 20), true);
        owner.Invoke(() => owner.Deliver(7, sample));
        owner.OnSilkScroll(new Vector2(2, 3), new Vector2(99, 99), false);
        var frame = input.Snapshot(Vector2.Zero, true, 800, 600);
        Assert.Equal(sample, Assert.Single(frame.Gestures.Samples));
        Assert.Equal(2, frame.HorizontalScrollDelta);
        Assert.Equal(3, frame.ScrollDelta);
    }

    [Fact]
    public void Callback_exceptions_are_contained_and_release_still_waits_for_unwind()
    {
        var backend = new Registration();
        using var owner = new NativeGestureRegistration(7, backend, new InputAccumulator(), () => true);
        owner.Invoke(() =>
        {
            owner.Dispose();
            throw new InvalidOperationException("callback failure");
        });
        Assert.True(backend.StorageReleased);
    }

    [Theory]
    [InlineData(true, NativeGestureFailureStage.Install)]
    [InlineData(false, NativeGestureFailureStage.Remove)]
    public void Diagnostic_false_results_remain_visible_without_releasing_live_storage(bool install, NativeGestureFailureStage stage)
    {
        var backend = new Registration { InstallResult = !install, RemoveResult = install };
        var input = new InputAccumulator();
        var owner = new NativeGestureRegistration(7, backend, input, () => true);
        try
        {
            if (!install) owner.Dispose();
            var failure = owner.Failure;
            Assert.NotNull(failure);
            Assert.Equal(stage, failure.Stage);
            Assert.False(string.IsNullOrEmpty(failure.Message));
            Assert.Null(failure.Exception);
            Assert.Equal(install, backend.StorageReleased);
        }
        finally { backend.RemoveResult = true; owner.Dispose(); }
        Assert.True(backend.StorageReleased);
    }

    [Theory]
    [InlineData(NativeGestureFailureStage.Install)]
    [InlineData(NativeGestureFailureStage.Remove)]
    [InlineData(NativeGestureFailureStage.Release)]
    [InlineData(NativeGestureFailureStage.Callback)]
    public void Diagnostic_exceptions_preserve_the_original_failure_and_stage(NativeGestureFailureStage stage)
    {
        var error = new InvalidOperationException("native failure");
        var backend = new Registration
        {
            InstallError = stage == NativeGestureFailureStage.Install ? error : null,
            RemoveError = stage == NativeGestureFailureStage.Remove ? error : null,
            ReleaseError = stage == NativeGestureFailureStage.Release ? error : null,
        };
        var owner = new NativeGestureRegistration(7, backend, new InputAccumulator(), () => true);
        try
        {
            if (stage == NativeGestureFailureStage.Callback) owner.Invoke(() => throw error);
            else if (stage != NativeGestureFailureStage.Install) owner.Dispose();
            var failure = owner.Failure;
            Assert.NotNull(failure);
            Assert.Equal(stage, failure.Stage);
            Assert.Same(error, failure.Exception);
            Assert.Equal(stage == NativeGestureFailureStage.Install, backend.StorageReleased);
        }
        finally
        {
            backend.RemoveError = null;
            backend.ReleaseError = null;
            owner.Dispose();
        }
        Assert.True(backend.StorageReleased);
    }

    [Fact]
    public void Diagnostic_keeps_the_first_failure_across_callback_and_cleanup_errors()
    {
        var backend = new Registration { InstallResult = false, RemoveResult = false };
        var owner = new NativeGestureRegistration(7, backend, new InputAccumulator(), () => true);
        try
        {
            var first = owner.Failure;
            Assert.NotNull(first);
            owner.Invoke(() => throw new InvalidOperationException("later failure"));
            owner.Dispose();
            Assert.Same(first, owner.Failure);
            Assert.Equal(NativeGestureFailureStage.Install, first.Stage);
            Assert.False(backend.StorageReleased);
        }
        finally { backend.RemoveResult = true; owner.Dispose(); }
        Assert.True(backend.StorageReleased);
    }

    [Fact]
    public void Diagnostic_unsupported_backend_is_normal_wheel_fallback()
    {
        var input = new InputAccumulator();
        using var owner = new NativeGestureRegistration(7, null, input, () => true);
        owner.OnSilkScroll(new Vector2(1, 2), Vector2.Zero, false);
        Assert.Null(owner.Failure);
        Assert.Equal(PointerGestureSource.UnknownWheel,
            Assert.Single(input.Snapshot(Vector2.Zero, true, 800, 600).Gestures.Samples).Source);
    }

    sealed class Registration : INativeGestureRegistration
    {
        public bool InstallResult { get; init; } = true;
        public bool ThrowDuringInstall { get; init; }
        public Exception? InstallError { get; init; }
        public Exception? RemoveError { get; set; }
        public Exception? ReleaseError { get; set; }
        public bool RemoveResult { get; set; } = true;
        public bool StorageReleased { get; private set; }
        public int RemovalCount { get; private set; }
        public bool Install(NativeGestureRegistration owner)
        {
            if (ThrowDuringInstall) throw new InvalidOperationException("partial install");
            if (InstallError != null) throw InstallError;
            return InstallResult;
        }
        public bool Remove()
        {
            RemovalCount++;
            if (RemoveError != null) throw RemoveError;
            return RemoveResult;
        }
        public void Release()
        {
            if (ReleaseError != null) throw ReleaseError;
            StorageReleased = true;
        }
    }
}
