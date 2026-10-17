using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class ReportQuotaGateTests
{
    const string Subject = "discord:42";
    const string Hash = "bca71894e3a88fbd2411b7d5061a2d8b";
    const string HourKey = "ah-" + Hash + "-2026101710";
    const string DayKey = "ad-" + Hash + "-20261017";
    const string GlobalKey = "gd-20261017";
    static readonly DateTimeOffset Start = new(2026, 10, 17, 10, 15, 0, TimeSpan.Zero);
    static readonly DateTimeOffset DayExpiry = new(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Consume_SixthInSameHour_IsDeniedWithRetryAfterToHourEnd()
    {
        using var fixture = new Fixture();
        for (int i = 0; i < 5; i++)
            Assert.Equal(new QuotaDecision(true, TimeSpan.Zero), await fixture.Consume());

        QuotaDecision decision = await fixture.Consume();

        Assert.False(decision.Allowed);
        Assert.Equal(TimeSpan.FromMinutes(45), decision.RetryAfter);
        Assert.Equal(5, (await fixture.Store.ReadAsync(HourKey, CancellationToken.None))!.Count);
    }

    [Fact]
    public async Task Consume_NextHour_AllowsAgain()
    {
        using var fixture = new Fixture();
        for (int i = 0; i < 5; i++)
            Assert.True((await fixture.Consume()).Allowed);
        fixture.Clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(new QuotaDecision(true, TimeSpan.Zero), await fixture.Consume());
        Assert.Equal(1, (await fixture.Store.ReadAsync("ah-" + Hash + "-2026101711", CancellationToken.None))!.Count);
        Assert.Equal(6, (await fixture.Store.ReadAsync(DayKey, CancellationToken.None))!.Count);
    }

    [Fact]
    public async Task Consume_TwentyFirstInDay_IsDeniedUntilMidnightUtc()
    {
        using var fixture = new Fixture();
        for (int hour = 0; hour < 4; hour++)
        {
            for (int i = 0; i < 5; i++)
                Assert.True((await fixture.Consume()).Allowed);
            fixture.Clock.Advance(TimeSpan.FromHours(1));
        }
        fixture.Clock.Advance(TimeSpan.FromMinutes(15));

        QuotaDecision decision = await fixture.Consume();

        Assert.False(decision.Allowed);
        Assert.Equal(TimeSpan.FromHours(9.5), decision.RetryAfter);
        Assert.Equal(20, (await fixture.Store.ReadAsync(DayKey, CancellationToken.None))!.Count);
    }

    [Fact]
    public async Task Consume_GlobalHundred_DeniesAnyNextSubject()
    {
        using var fixture = new Fixture();
        for (int subject = 0; subject < 20; subject++)
            for (int i = 0; i < 5; i++)
                Assert.True((await fixture.Consume($"discord:{subject}")).Allowed);

        QuotaDecision decision = await fixture.Consume("discord:new");

        Assert.False(decision.Allowed);
        Assert.Equal(TimeSpan.FromHours(13.75), decision.RetryAfter);
        Assert.Equal(100, (await fixture.Store.ReadAsync(GlobalKey, CancellationToken.None))!.Count);
    }

    [Fact]
    public async Task Consume_DeniedByDayWindow_DoesNotIncrementHourWindow()
    {
        var store = new ConflictingQuotaStore(0);
        using var fixture = new Fixture(store);
        Assert.True(await store.Inner.TryWriteAsync(HourKey, 2, null, DayExpiry, CancellationToken.None));
        Assert.True(await store.Inner.TryWriteAsync(DayKey, 20, null, DayExpiry, CancellationToken.None));
        QuotaCounter? before = await store.Inner.ReadAsync(HourKey, CancellationToken.None);

        QuotaDecision decision = await fixture.Consume();

        Assert.False(decision.Allowed);
        Assert.Equal(TimeSpan.FromHours(13.75), decision.RetryAfter);
        Assert.Equal(before, await store.Inner.ReadAsync(HourKey, CancellationToken.None));
        Assert.Empty(store.Attempts);
        Assert.Equal(1, store.ReadCount(GlobalKey));
    }

    [Fact]
    public async Task Consume_LastInstantOfDay_CountsInOldDay_RetryAfterAtLeastOneSecond()
    {
        var store = new ConflictingQuotaStore(0);
        // The day key ends in 20261031 and the hour key ends in 2026103123.
        using var fixture = new Fixture(store, new DateTimeOffset(2026, 10, 31, 23, 59, 59, 900, TimeSpan.Zero));
        Assert.True(await store.Inner.TryWriteAsync("gd-20261031", 100, null,
            new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), CancellationToken.None));

        QuotaDecision decision = await fixture.Consume();

        Assert.False(decision.Allowed);
        Assert.Equal(TimeSpan.FromSeconds(1), decision.RetryAfter);
        Assert.Equal(1, store.ReadCount("ah-" + Hash + "-2026103123"));
        Assert.Equal(1, store.ReadCount("ad-" + Hash + "-20261031"));
        Assert.Equal(1, store.ReadCount("gd-20261031"));
        Assert.Empty(store.Attempts);
    }

    [Fact]
    public async Task Consume_VersionConflictTwice_RetriesAndSucceeds()
    {
        var store = new ConflictingQuotaStore(2);
        using var fixture = new Fixture(store);

        Assert.Equal(new QuotaDecision(true, TimeSpan.Zero), await fixture.Consume());

        Assert.Equal(3, store.WriteCount(HourKey));
        Assert.Equal(1, store.WriteCount(DayKey));
        Assert.Equal(1, store.WriteCount(GlobalKey));
        Assert.Equal(1, (await store.Inner.ReadAsync(HourKey, CancellationToken.None))!.Count);
        Assert.Empty(fixture.Sink.Entries);
    }

    [Fact]
    public async Task Consume_ConflictOnEveryAttempt_DeniesWithFiveSecondRetryAfter()
    {
        var store = new ConflictingQuotaStore(int.MaxValue);
        using var fixture = new Fixture(store);

        QuotaDecision decision = await fixture.Consume();

        Assert.False(decision.Allowed);
        Assert.Equal(TimeSpan.FromSeconds(5), decision.RetryAfter);
        Assert.Equal(5, store.WriteCount(HourKey));
        Assert.Equal(0, store.WriteCount(DayKey));
        Assert.Equal(0, store.WriteCount(GlobalKey));
        Assert.Null(await store.Inner.ReadAsync(HourKey, CancellationToken.None));
        AssertContentionWarning(fixture.Sink, "ah", HourKey);
    }

    [Fact]
    public void Keys_HashTheSubject_AndNeverContainItVerbatim()
    {
        var windows = QuotaWindows.For(Subject, Start, Options());

        Assert.Equal(Hash, QuotaWindows.SubjectHash(Subject));
        Assert.Collection(windows,
            window => Assert.Equal(HourKey, window.Key),
            window => Assert.Equal(DayKey, window.Key),
            window => Assert.Equal(GlobalKey, window.Key));
        Assert.All(windows, window =>
        {
            Assert.DoesNotContain("discord", window.Key);
            Assert.Matches("^((ah|ad)-[0-9a-f]{32}-[0-9]+|gd-[0-9]{8})$", window.Key);
        });
    }

    [Theory]
    [InlineData("2026-11-01T01:59:59.900+02:00")]
    [InlineData("2026-10-31T19:59:59.900-04:00")]
    public void Windows_NonUtcOffset_UsesUtcKeysAndWindowEnds(string timestamp)
    {
        DateTimeOffset now = DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture);

        var windows = QuotaWindows.For(Subject, now, Options());

        DateTimeOffset end = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(new QuotaWindow("ah-" + Hash + "-2026103123", 5, end), windows[0]);
        Assert.Equal(new QuotaWindow("ad-" + Hash + "-20261031", 20, end), windows[1]);
        Assert.Equal(new QuotaWindow("gd-20261031", 100, end), windows[2]);
        Assert.All(windows, window => Assert.Equal(TimeSpan.Zero, window.End.Offset));
    }

    [Fact]
    public async Task Consume_MultipleFullWindows_ReadsAllAndUsesLongestRetryWithoutWriting()
    {
        var store = new ConflictingQuotaStore(0);
        using var fixture = new Fixture(store);
        Assert.True(await store.Inner.TryWriteAsync(HourKey, 5, null, DayExpiry, CancellationToken.None));
        Assert.True(await store.Inner.TryWriteAsync(DayKey, 20, null, DayExpiry, CancellationToken.None));
        Assert.True(await store.Inner.TryWriteAsync(GlobalKey, 100, null, DayExpiry, CancellationToken.None));

        QuotaDecision decision = await fixture.Consume();

        Assert.False(decision.Allowed);
        Assert.Equal(TimeSpan.FromHours(13.75), decision.RetryAfter);
        Assert.Equal(1, store.ReadCount(HourKey));
        Assert.Equal(1, store.ReadCount(DayKey));
        Assert.Equal(1, store.ReadCount(GlobalKey));
        Assert.Empty(store.Attempts);
    }

    [Fact]
    public async Task Consume_WritesInWindowOrder_WithExpiryOneDayAfterEachEnd()
    {
        var store = new ConflictingQuotaStore(0);
        using var fixture = new Fixture(store);

        Assert.True((await fixture.Consume()).Allowed);

        Assert.Collection(store.Attempts,
            write => Assert.Equal((HourKey, 1L, (string?)null,
                new DateTimeOffset(2026, 10, 18, 11, 0, 0, TimeSpan.Zero)), write),
            write => Assert.Equal((DayKey, 1L, (string?)null, DayExpiry), write),
            write => Assert.Equal((GlobalKey, 1L, (string?)null, DayExpiry), write));
    }

    [Fact]
    public async Task Consume_Conflict_RereadsUpdatedCountAndVersion()
    {
        var store = new ConflictingQuotaStore(1);
        store.BeforeRead = async (key, read, ct) =>
        {
            if (key == HourKey && read == 3)
                Assert.True(await store.Inner.TryWriteAsync(key, 2, null, DayExpiry, ct));
        };
        using var fixture = new Fixture(store);

        Assert.True((await fixture.Consume()).Allowed);

        Assert.Equal(2, store.WriteCount(HourKey));
        Assert.Equal(3, (await store.Inner.ReadAsync(HourKey, CancellationToken.None))!.Count);
        Assert.NotNull(store.Attempts[1].ExpectedVersion);
    }

    [Theory]
    [InlineData(HourKey, 5, 45)]
    [InlineData(DayKey, 20, 825)]
    [InlineData(GlobalKey, 100, 825)]
    public async Task Consume_WindowBecomesFullBeforeWrite_DeniesWithoutIncrementingIt(string key, int limit,
        int retryMinutes)
    {
        var store = new ConflictingQuotaStore(0);
        store.BeforeRead = async (readKey, read, ct) =>
        {
            if (readKey == key && read == 2)
                Assert.True(await store.Inner.TryWriteAsync(key, limit, null, DayExpiry, ct));
        };
        using var fixture = new Fixture(store);

        QuotaDecision decision = await fixture.Consume();

        Assert.False(decision.Allowed);
        Assert.Equal(TimeSpan.FromMinutes(retryMinutes), decision.RetryAfter);
        Assert.Equal(0, store.WriteCount(key));
        Assert.Equal(limit, (await store.Inner.ReadAsync(key, CancellationToken.None))!.Count);
        if (key != HourKey)
            Assert.Equal(1, (await store.Inner.ReadAsync(HourKey, CancellationToken.None))!.Count);
        if (key == GlobalKey)
            Assert.Equal(1, (await store.Inner.ReadAsync(DayKey, CancellationToken.None))!.Count);
        Assert.Empty(fixture.Sink.Entries);
    }

    [Theory]
    [InlineData(DayKey, "ad")]
    [InlineData(GlobalKey, "gd")]
    public async Task Consume_LaterWindowConflicts_KeepsEarlierIncrements(string key, string kind)
    {
        var store = new ConflictingQuotaStore(int.MaxValue, key);
        using var fixture = new Fixture(store);

        QuotaDecision decision = await fixture.Consume();

        Assert.False(decision.Allowed);
        Assert.Equal(TimeSpan.FromSeconds(5), decision.RetryAfter);
        Assert.Equal(5, store.WriteCount(key));
        Assert.Equal(1, (await store.Inner.ReadAsync(HourKey, CancellationToken.None))!.Count);
        Assert.Null(await store.Inner.ReadAsync(key, CancellationToken.None));
        if (key == GlobalKey)
            Assert.Equal(1, (await store.Inner.ReadAsync(DayKey, CancellationToken.None))!.Count);
        AssertContentionWarning(fixture.Sink, kind, key);
    }

    [Fact]
    public async Task Consume_FifthWriteAttemptSucceeds_Allows()
    {
        var store = new ConflictingQuotaStore(4);
        using var fixture = new Fixture(store);

        Assert.Equal(new QuotaDecision(true, TimeSpan.Zero), await fixture.Consume());

        Assert.Equal(5, store.WriteCount(HourKey));
        Assert.Empty(fixture.Sink.Entries);
    }

    [Fact]
    public async Task Consume_WindowFillsAfterConflict_DeniesWithWindowRetry()
    {
        var store = new ConflictingQuotaStore(1);
        store.BeforeRead = async (key, read, ct) =>
        {
            if (key == HourKey && read == 3)
                Assert.True(await store.Inner.TryWriteAsync(key, 5, null, DayExpiry, ct));
        };
        using var fixture = new Fixture(store);

        QuotaDecision decision = await fixture.Consume();

        Assert.False(decision.Allowed);
        Assert.Equal(TimeSpan.FromMinutes(45), decision.RetryAfter);
        Assert.Equal(1, store.WriteCount(HourKey));
        Assert.Empty(fixture.Sink.Entries);
    }

    [Fact]
    public async Task Consume_NextUtcDay_AllowsAfterGlobalDenial()
    {
        using var fixture = new Fixture();
        Assert.True(await fixture.Store.TryWriteAsync(GlobalKey, 100, null, DayExpiry, CancellationToken.None));
        Assert.False((await fixture.Consume()).Allowed);
        fixture.Clock.Advance(TimeSpan.FromDays(1));

        Assert.Equal(new QuotaDecision(true, TimeSpan.Zero), await fixture.Consume());
        Assert.Equal(1, (await fixture.Store.ReadAsync("gd-20261018", CancellationToken.None))!.Count);
    }

    [Theory]
    [InlineData(1, 20, 100, 45)]
    [InlineData(5, 1, 100, 825)]
    [InlineData(5, 20, 1, 825)]
    public async Task Consume_CustomLimits_AppliesEachConfiguredLimit(int hourly, int daily, int global,
        int retryMinutes)
    {
        var options = Options() with { PerAccountPerHour = hourly, PerAccountPerDay = daily, GlobalPerDay = global };
        using var fixture = new Fixture(options: options);
        Assert.True((await fixture.Consume()).Allowed);

        QuotaDecision decision = await fixture.Consume();

        Assert.False(decision.Allowed);
        Assert.Equal(TimeSpan.FromMinutes(retryMinutes), decision.RetryAfter);
    }

    [Fact]
    public async Task Consume_Cancelled_PropagatesCancellationWithoutWriting()
    {
        var store = new ConflictingQuotaStore(0);
        using var fixture = new Fixture(store);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Gate.TryConsumeAsync(Subject, cancellation.Token));

        Assert.Empty(store.Attempts);
    }

    static void AssertContentionWarning(InMemorySink sink, string kind, string key)
    {
        LogEntry warning = Assert.Single(sink.Entries);
        Assert.Equal(LogLevel.Warn, warning.Level);
        Assert.Contains(kind, warning.Message);
        Assert.DoesNotContain(Subject, warning.Message);
        Assert.DoesNotContain(Hash, warning.Message);
        Assert.DoesNotContain(key, warning.Message);
        Assert.DoesNotContain("20261017", warning.Message);
    }

    static BugReportRelayOptions Options() => new()
    {
        GameId = "test",
        GameDisplayName = "Test",
        RepositoryOwner = "owner",
        RepositoryName = "reports",
        Labels = Array.Empty<string>(),
        PublicMediaBase = new Uri("https://example.test/reports/"),
        ServiceKey = new string('k', 32),
    };

    sealed class Fixture : IDisposable
    {
        readonly LogManager logs;
        public ManualClock Clock { get; }
        public IReportQuotaStore Store { get; }
        public InMemorySink Sink { get; } = new();
        public ReportQuotaGate Gate { get; }

        public Fixture(IReportQuotaStore? store = null, DateTimeOffset? start = null,
            BugReportRelayOptions? options = null)
        {
            Clock = new ManualClock(start ?? Start);
            Store = store ?? new InMemoryReportQuotaStore();
            logs = new LogManager(new LoggerOptions
            {
                Synchronous = true,
                Clock = new LogClock(Clock),
                Sinks = { Sink },
            });
            Gate = new ReportQuotaGate(Store, options ?? Options(), Clock, logs.GetLogger("BugReports.Relay"));
        }

        public Task<QuotaDecision> Consume(string subject = Subject) => Gate.TryConsumeAsync(subject, CancellationToken.None);

        public void Dispose() => logs.Dispose();
    }

    sealed class LogClock(ManualClock clock) : IClock
    {
        public DateTimeOffset Now => clock.GetUtcNow();
    }
}
