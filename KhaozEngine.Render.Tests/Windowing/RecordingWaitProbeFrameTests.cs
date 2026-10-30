using System;
using System.Runtime.CompilerServices;
using KhaozEngine.Gpu;
using KhaozEngine.Windowing;
using Xunit;
using static KhaozEngine.Tests.Windowing.RecordingWaitProbeTestData;

namespace KhaozEngine.Tests.Windowing;

public sealed class RecordingWaitProbeFrameTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Protected_open_copies_values_and_zero_generation_is_available(bool wrap)
    {
        using var f = new RecordingWaitProbeFixture();
        if (wrap) f.MainSource.Attempt = ulong.MaxValue;
        f.Frame.DeclareRecordingWaitPrefix(false); f.Observe(RecordingWaitRole.Main);
        Assert.Equal(0, f.Frame.CompletedRecordingWaitCallback.AcknowledgementCount);
        f.Frame.CompleteRecordingWaitCallback();
        var saved = f.Frame.CompletedRecordingWaitCallback;
        Assert.Equal(RecordingWaitProbeStatus.Valid, RecordingWaitProbeAccounting.Validate(Request(), saved, false));
        Assert.Equal(wrap ? 0UL : 1UL, saved.Main.Receipt!.Value.Attempt);
        Assert.Equal(0, saved.Main.Receipt.Value.Ticks);
        f.Frame.BeginRecordingWaitCallback(Request(2));
        Assert.Equal(1, saved.MainObservations); Assert.Equal(0, f.Frame.CompletedRecordingWaitCallback.MainObservations);
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<RecordingWaitCallbackResult>());
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("sequence")]
    [InlineData("device")]
    [InlineData("list")]
    [InlineData("attempt")]
    [InlineData("late")]
    [InlineData("stale")]
    [InlineData("duplicate")]
    public void Stale_wrong_or_duplicate_tickets_invalidate_the_callback(string failure)
    {
        using var f = new RecordingWaitProbeFixture(); f.Frame.DeclareRecordingWaitPrefix(false);
        var ticket = f.Frame.BeginRecordingWaitObservation(RecordingWaitRole.Main, f.Device, f.Main);
        using (GpuRecording.Open(f.Device, f.Main, "ticket test"))
        {
            ticket = failure switch
            {
                "epoch" => ticket with { Request = ticket.Request with { Epoch = Guid.NewGuid() } },
                "sequence" => ticket with { Request = ticket.Request with { Sequence = 2 } },
                "device" => ticket with { DeviceIdentity = Guid.NewGuid() },
                "list" => ticket with { List = f.Prefix },
                "attempt" => ticket with { PriorAttempt = 2 },
                _ => ticket,
            };
            if (failure == "late") f.Frame.CompleteRecordingWaitCallback();
            if (failure == "stale") f.Frame.BeginRecordingWaitCallback(Request(2));
            f.Frame.CompleteRecordingWaitObservation(ticket);
            if (failure == "duplicate") f.Frame.CompleteRecordingWaitObservation(ticket);
        }
        f.Frame.CompleteRecordingWaitCallback();
        Assert.NotEqual(RecordingWaitProbeStatus.Valid,
            RecordingWaitProbeAccounting.Validate(failure == "stale" ? Request(2) : Request(), f.Frame.CompletedRecordingWaitCallback, false));
    }

    [Theory]
    [InlineData("attempt")]
    [InlineData("receipt")]
    [InlineData("device")]
    [InlineData("list")]
    public void Diagnostic_faults_preserve_application_failure_and_recording_cleanup(string point)
    {
        using var f = new RecordingWaitProbeFixture();
        f.MainSource.ThrowAt = point;
        f.Frame.DeclareRecordingWaitPrefix(false);
        var original = new InvalidOperationException("application");
        var error = Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            var ticket = f.Frame.BeginRecordingWaitObservation(RecordingWaitRole.Main, f.Device, f.Main);
            using (GpuRecording.Open(f.Device, f.Main, "fault test"))
            {
                f.Frame.CompleteRecordingWaitObservation(ticket);
                throw original;
            }
        }));
        Assert.Same(original, error); Assert.False(f.MainSource.Recording);
        f.MainSource.ThrowAt = null;
        using var next = GpuRecording.Open(f.Device, f.Main, "register released");
        Assert.Equal(0, f.Frame.CompletedRecordingWaitCallback.AcknowledgementCount);
        Assert.Equal(RecordingWaitObservationStatus.Fault, f.Frame.CompletedRecordingWaitCallback.Main.Status);
    }

    [Fact]
    public void Off_callback_performs_no_capability_access_and_absence_is_not_zero()
    {
        using var f = new RecordingWaitProbeFixture(); f.Frame.BeginRecordingWaitCallback(default);
        f.Frame.DeclareRecordingWaitPrefix(false); f.Observe(RecordingWaitRole.Main); f.Frame.CompleteRecordingWaitCallback();
        Assert.Equal(0, f.MainSource.Reads);
        f.Frame.BeginRecordingWaitCallback(Request()); f.Frame.DeclareRecordingWaitPrefix(false);
        var raw = f.Gpu.Factory.CreateCommandList();
        try
        {
            var ticket = f.Frame.BeginRecordingWaitObservation(RecordingWaitRole.Main, f.Gpu, raw);
            using (GpuRecording.Open(f.Gpu, raw, "unsupported")) f.Frame.CompleteRecordingWaitObservation(ticket);
            f.Frame.CompleteRecordingWaitCallback();
            Assert.Equal(RecordingWaitObservationStatus.Unsupported, f.Frame.CompletedRecordingWaitCallback.Main.Status);
            Assert.Null(f.Frame.CompletedRecordingWaitCallback.Main.Receipt);
        }
        finally { raw.Dispose(); }
    }

    [Fact]
    public void Duplicate_declaration_roles_and_acknowledgements_remain_visible()
    {
        using var f = new RecordingWaitProbeFixture();
        f.Frame.DeclareRecordingWaitPrefix(false); f.Frame.DeclareRecordingWaitPrefix(false);
        f.Observe(RecordingWaitRole.Main); f.Observe(RecordingWaitRole.Main);
        f.Frame.CompleteRecordingWaitCallback(); f.Frame.CompleteRecordingWaitCallback();
        var c = f.Frame.CompletedRecordingWaitCallback;
        Assert.Equal(2, c.DeclarationCount); Assert.Equal(2, c.MainObservations); Assert.Equal(2, c.AcknowledgementCount);
    }
}
