using System;
using System.Collections.Generic;
using KhaozEngine.Diagnostics;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests;

public class MigrationChainTests
{
    // Plain POCO exercised through the delegate form (no interface).
    private sealed class Poco
    {
        public int Ver { get; set; }
        public List<int> Steps { get; } = new();
    }

    // Implements the opt-in interface for the zero-config form (used later).
    private sealed class Doc : ISchemaVersioned
    {
        public int SchemaVersion { get; set; }
        public List<int> Steps { get; } = new();
    }

    [Fact]
    public void Migrate_RunsStepsInOrder_AndStampsVersionEachStep()
    {
        var chain = MigrationChain.For<Poco>(p => p.Ver, (p, v) => p.Ver = v)
            .Step(1, p => { p.Steps.Add(1); return p; })   // v1 -> v2
            .Step(2, p => { p.Steps.Add(2); return p; })   // v2 -> v3
            .Build(currentVersion: 3);

        var result = chain.Migrate(new Poco { Ver = 1 });

        Assert.Equal(3, result.Ver);
        Assert.Equal(new[] { 1, 2 }, result.Steps);
        Assert.Equal(3, chain.CurrentVersion);
    }

    [Theory]
    [InlineData(2)] // Already current.
    [InlineData(5)] // A file from a newer build.
    public void Migrate_AtOrAboveCurrent_IsNoOp(int version)
    {
        var chain = MigrationChain.For<Poco>(p => p.Ver, (p, v) => p.Ver = v)
            .Step(1, p => { p.Steps.Add(1); return p; })
            .Build(2);

        var result = chain.Migrate(new Poco { Ver = version });

        Assert.Equal(version, result.Ver);
        Assert.Empty(result.Steps);
    }

    [Theory]
    [InlineData(3, 4)] // Missing step from 2 to 3.
    [InlineData(2, 2)] // Step from 2 targets a version beyond current.
    public void Build_NoncontiguousOrOutOfRangeSteps_Throws(int secondStep, int currentVersion)
    {
        var builder = MigrationChain.For<Poco>(p => p.Ver, (p, v) => p.Ver = v)
            .Step(1, p => p)
            .Step(secondStep, p => p);
        Assert.Throws<ArgumentException>(() => builder.Build(currentVersion));
    }

    [Fact]
    public void Step_DuplicateFromVersion_Throws()
    {
        var builder = MigrationChain.For<Poco>(p => p.Ver, (p, v) => p.Ver = v).Step(1, p => p);
        Assert.Throws<ArgumentException>(() => builder.Step(1, p => p));
    }

    [Fact]
    public void Build_EmptyChain_IsAllowed_AndIsSilentNoOp()
    {
        var logger = new FakeLogger();
        var chain = MigrationChain.For<Poco>(p => p.Ver, (p, v) => p.Ver = v).Build(3);
        var result = chain.Migrate(new Poco { Ver = 1 }, logger);
        Assert.Equal(1, result.Ver);   // no steps, nothing to do
        Assert.Empty(result.Steps);
        Assert.Empty(logger.Entries);  // empty chain warns about nothing
    }

    [Fact]
    public void Step_NullMigrate_Throws()
    {
        var builder = MigrationChain.For<Poco>(p => p.Ver, (p, v) => p.Ver = v);
        Assert.Throws<ArgumentNullException>(() => builder.Step(1, null!));
    }

    [Fact]
    public void Migrate_VersionBelowOldestStep_LeavesValueAndWarns()
    {
        var logger = new FakeLogger();
        var chain = MigrationChain.For<Poco>(p => p.Ver, (p, v) => p.Ver = v)
            .Step(2, p => { p.Steps.Add(2); return p; })   // oldest step is from v2
            .Build(3);

        var result = chain.Migrate(new Poco { Ver = 1 }, logger);   // file is older than any step

        Assert.Equal(1, result.Ver);
        Assert.Empty(result.Steps);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warn);
    }

    [Fact]
    public void Migrate_StepThrows_HaltsAndKeepsCompletedSteps_AndLogsError()
    {
        var logger = new FakeLogger();
        var chain = MigrationChain.For<Poco>(p => p.Ver, (p, v) => p.Ver = v)
            .Step(1, p => { p.Steps.Add(1); return p; })                       // 1 -> 2 ok
            .Step(2, p => throw new InvalidOperationException("boom"))          // 2 -> 3 throws
            .Step(3, p => { p.Steps.Add(3); return p; })                       // never reached
            .Build(4);

        var result = chain.Migrate(new Poco { Ver = 1 }, logger);

        Assert.Equal(2, result.Ver);                  // only the first step's bump stuck
        Assert.Equal(new[] { 1 }, result.Steps);      // step 3 never ran
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void Migrate_GetVersionDelegateThrows_Swallowed_ReturnsValue_AndLogsError()
    {
        var logger = new FakeLogger();
        var chain = MigrationChain.For<Poco>(_ => throw new InvalidOperationException("bad get"), (p, v) => p.Ver = v)
            .Step(1, p => p)
            .Build(2);
        var value = new Poco { Ver = 1 };

        var result = chain.Migrate(value, logger);

        Assert.Same(value, result);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void Migrate_NullValue_ReturnsNull()
    {
        var chain = MigrationChain.For<Poco>(p => p.Ver, (p, v) => p.Ver = v).Step(1, p => p).Build(2);
        Assert.Null(chain.Migrate(null!));
    }

    [Fact]
    public void For_InterfaceForm_ReadsAndWritesSchemaVersion()
    {
        var chain = MigrationChain.For<Doc>()                       // zero-config: uses ISchemaVersioned
            .Step(1, d => { d.Steps.Add(1); return d; })
            .Step(2, d => { d.Steps.Add(2); return d; })
            .Build(3);

        var result = chain.Migrate(new Doc { SchemaVersion = 1 });

        Assert.Equal(3, result.SchemaVersion);
        Assert.Equal(new[] { 1, 2 }, result.Steps);
    }

    [Fact]
    public void StampCurrent_SetsCurrentVersion()
    {
        var chain = MigrationChain.For<Doc>()
            .Step(1, d => { d.Steps.Add(1); return d; })
            .Step(2, d => { d.Steps.Add(2); return d; })
            .Build(3);

        var doc = new Doc { SchemaVersion = 0 };
        var result = chain.StampCurrent(doc);

        Assert.Same(doc, result);
        Assert.Equal(3, result.SchemaVersion);
        Assert.Empty(result.Steps);   // no step ran
    }

    [Fact]
    public void StampCurrent_Null_ReturnsNull()
    {
        var chain = MigrationChain.For<Doc>()
            .Step(1, d => d)
            .Build(2);

        var result = chain.StampCurrent(null!);

        Assert.Null(result);
    }
}
