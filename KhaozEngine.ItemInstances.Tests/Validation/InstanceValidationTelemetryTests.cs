using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.Catalog;
using KhaozEngine.Diagnostics;
using KhaozEngine.ItemInstances;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Validation;

/// <summary>
/// Spec 12.6's counter and log line, and the three placeholder string keys of 12.3. The validator itself is
/// pure, so everything with a side effect is here, in the one named member the load path calls.
/// </summary>
public class InstanceValidationTelemetryTests
{
    const string StreamKey = "player/07/bag";

    [Fact]
    public void A_wholesale_failure_emits_exactly_one_line_and_a_hundred_counter_increments()
    {
        ContentTypeRegistry types = InstanceValidationFixtures.Types();
        ContentSnapshot snapshot = InstanceValidationFixtures.Snapshot(types);
        var slots = new PageSlotInput[InstanceValidationFixtures.PageSlots];
        for (int slot = 0; slot < slots.Length; slot++)
        {
            slots[slot] = InstanceValidationFixtures.Slot(
                slot, InstanceValidationFixtures.MissingId, instanceId: 1000 + slot);
        }

        InstanceValidationReport report = InstanceValidationFixtures.Sweep(slots, types, snapshot);
        var logger = new RecordingLogger();
        var counted = new List<(int Type, string Reason)>();

        InstanceValidationTelemetry.Report(report, StreamKey, logger, (type, reason) => counted.Add((type, reason)));

        // One line per CONTAINER, because a hundred identical lines is how an operator learns to filter the
        // category out. The counter is still per record, because that is what a dashboard reads.
        Assert.Single(logger.Entries);
        Assert.Equal(InstanceValidationFixtures.PageSlots, counted.Count);
        Assert.All(counted, c => Assert.Equal((int)EngineContentTypes.ItemTypeId, c.Type));
        Assert.All(counted, c => Assert.Equal(InstanceQuarantineReason.UnknownDefinition, c.Reason));
    }

    [Fact]
    public void The_one_line_names_the_reason_the_two_versions_and_the_stream_key_and_no_bytes()
    {
        ContentTypeRegistry types = InstanceValidationFixtures.Types();
        ContentSnapshot snapshot = InstanceValidationFixtures.Snapshot(types);
        byte[] payload = InstanceValidationFixtures.AffixPayload();
        InstanceValidationReport report = InstanceValidationFixtures.Sweep(
            [
                InstanceValidationFixtures.Slot(0, InstanceValidationFixtures.MissingId, instanceId: 1),
                InstanceValidationFixtures.Slot(1, InstanceValidationFixtures.MissingId, instanceId: 2),
                InstanceValidationFixtures.Slot(2, InstanceValidationFixtures.LiveItem, instanceId: 3, payload: payload),
            ],
            types,
            snapshot,
            stamp: 4);

        var logger = new RecordingLogger();
        InstanceValidationTelemetry.Report(report, StreamKey, logger, null);

        (LogLevel level, string message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warn, level);
        Assert.Equal(InstanceValidationTelemetry.LogCategory, logger.Category);
        Assert.Contains(InstanceQuarantineReason.UnknownDefinition, message, StringComparison.Ordinal);
        Assert.Contains("4", message, StringComparison.Ordinal);
        Assert.Contains(
            InstanceValidationFixtures.ActiveVersion.ToString(CultureInfo.InvariantCulture),
            message,
            StringComparison.Ordinal);
        Assert.Contains(StreamKey, message, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(payload), message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_report_with_nothing_quarantined_logs_nothing_and_counts_nothing()
    {
        ContentTypeRegistry types = InstanceValidationFixtures.Types();
        ContentSnapshot snapshot = InstanceValidationFixtures.Snapshot(types);
        InstanceValidationReport report = InstanceValidationFixtures.Sweep(
            [
                InstanceValidationFixtures.Slot(0, InstanceValidationFixtures.RetiredItem),
                InstanceValidationFixtures.Slot(1, InstanceValidationFixtures.LiveItem, count: 99),
            ],
            types,
            snapshot);

        var logger = new RecordingLogger();
        int counted = 0;
        InstanceValidationTelemetry.Report(report, StreamKey, logger, (_, _) => counted++);

        // Checks 12 and 13 are the two findings with no reason ordinal, so neither is an alert.
        Assert.Equal(2, report.Findings.Count);
        Assert.Empty(logger.Entries);
        Assert.Equal(0, counted);
    }

    [Fact]
    public void The_dominant_reason_is_the_one_with_the_highest_count()
    {
        ContentTypeRegistry types = InstanceValidationFixtures.Types();
        ContentSnapshot snapshot = InstanceValidationFixtures.Snapshot(types);
        InstanceValidationReport report = InstanceValidationFixtures.Sweep(
            [
                InstanceValidationFixtures.Slot(0, InstanceValidationFixtures.MissingId, instanceId: 1),
                InstanceValidationFixtures.Slot(1, InstanceValidationFixtures.MissingId, instanceId: 2),
                InstanceValidationFixtures.Slot(2, InstanceValidationFixtures.LiveItem, instanceId: 3, payload: [2, 1, 0x2A, 1, 1, 0x01]),
            ],
            types,
            snapshot);

        Assert.Equal(InstanceQuarantineReason.UnknownDefinition, report.DominantReason);
        Assert.Equal(2, report.ReasonCounts.Count);
        Assert.Equal(2, report.ReasonCounts[0].Value);
        Assert.Equal(InstancePayloadReason.KindOutOfOrder, report.ReasonCounts[1].Key);
    }

    [Fact]
    public void The_three_placeholder_presentations_are_engine_prefixed_keys_and_no_literal()
    {
        Assert.Equal("khaoz.item.quarantined", InstanceValidationStrings.Quarantined);
        Assert.Equal("khaoz.item.retired", InstanceValidationStrings.Retired);
        Assert.Equal("khaoz.item.unidentified", InstanceValidationStrings.Unidentified);

        // The khaoz. prefix keeps them out of contracts 12.1's derived <type key>.<content key>.<field>
        // grammar, which names content rows and never an engine string.
        Assert.Equal(3, InstanceValidationStrings.All.Count);
        Assert.All(InstanceValidationStrings.All, key => Assert.StartsWith("khaoz.", key, StringComparison.Ordinal));
    }

    [Fact]
    public void A_whole_CONTAINER_emits_ONE_line_over_every_page_and_counts_every_reason()
    {
        // The list shaped door, which is what a paged load calls once (spec 5.5). Ten pages each emitting
        // their own line is the same failure one line per entry would be, one order of magnitude down.
        ContentTypeRegistry types = InstanceValidationFixtures.Types();
        ContentSnapshot snapshot = InstanceValidationFixtures.Snapshot(types);
        InstanceValidationReport first = InstanceValidationFixtures.Sweep(
            [InstanceValidationFixtures.Slot(0, InstanceValidationFixtures.MissingId, instanceId: 1)],
            types,
            snapshot,
            stamp: 4);
        InstanceValidationReport second = InstanceValidationFixtures.Sweep(
            [InstanceValidationFixtures.Slot(0, InstanceValidationFixtures.MissingId, instanceId: 2)],
            types,
            snapshot,
            stamp: 6);
        var logger = new RecordingLogger();
        var counted = new List<(int Type, string Reason)>();

        InstanceValidationTelemetry.Report(
            [first, second],
            ["page-truncated", InstanceQuarantineReason.UnknownDefinition],
            StreamKey,
            InstanceValidationFixtures.ActiveVersion,
            logger,
            (type, reason) => counted.Add((type, reason)));

        // Two swept records plus two the sweeps never saw, each counted once, under its own token.
        Assert.Equal(4, counted.Count);
        Assert.Equal(3, CountOf(counted, InstanceQuarantineReason.UnknownDefinition));
        Assert.Equal(1, CountOf(counted, "page-truncated"));

        (LogLevel level, string message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warn, level);
        Assert.Contains("4 records quarantined across 2 pages", message, StringComparison.Ordinal);
        Assert.Contains("reason unknown-definition", message, StringComparison.Ordinal);
        Assert.Contains("unknown-definition=3 page-truncated=1", message, StringComparison.Ordinal);

        // The OLDEST page stamp in the sweep, which is the version the load is bringing the container
        // forward from, against the one active version it is being brought forward to.
        Assert.Contains("stamped version 4", message, StringComparison.Ordinal);
        Assert.Contains(
            FormattableString.Invariant($"active version {InstanceValidationFixtures.ActiveVersion}"),
            message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_container_with_nothing_quarantined_emits_NOTHING()
    {
        ContentTypeRegistry types = InstanceValidationFixtures.Types();
        ContentSnapshot snapshot = InstanceValidationFixtures.Snapshot(types);
        InstanceValidationReport clean = InstanceValidationFixtures.Sweep(
            [InstanceValidationFixtures.Slot(0, InstanceValidationFixtures.LiveItem, count: 3)], types, snapshot);
        var logger = new RecordingLogger();
        var counted = new List<(int Type, string Reason)>();

        InstanceValidationTelemetry.Report(
            [clean], [], StreamKey, InstanceValidationFixtures.ActiveVersion, logger,
            (type, reason) => counted.Add((type, reason)));

        Assert.Empty(logger.Entries);
        Assert.Empty(counted);
    }

    static int CountOf(List<(int Type, string Reason)> counted, string reason)
    {
        int count = 0;
        foreach ((int _, string held) in counted)
        {
            if (string.Equals(held, reason, StringComparison.Ordinal)) count++;
        }

        return count;
    }

    sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public string Category => InstanceValidationTelemetry.LogCategory;

        public bool IsEnabled(LogLevel level) => true;

        public void Log(LogLevel level, string message, Exception? exception = null) => Entries.Add((level, message));

        public void Trace(string message, Exception? exception = null) => Log(LogLevel.Trace, message, exception);

        public void Debug(string message, Exception? exception = null) => Log(LogLevel.Debug, message, exception);

        public void Info(string message, Exception? exception = null) => Log(LogLevel.Info, message, exception);

        public void Warn(string message, Exception? exception = null) => Log(LogLevel.Warn, message, exception);

        public void Error(string message, Exception? exception = null) => Log(LogLevel.Error, message, exception);

        public void Fatal(string message, Exception? exception = null) => Log(LogLevel.Fatal, message, exception);
    }
}
