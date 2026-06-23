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

    [Fact]
    public void Migrate_AlreadyCurrent_IsNoOp()
    {
        var chain = MigrationChain.For<Poco>(p => p.Ver, (p, v) => p.Ver = v)
            .Step(1, p => { p.Steps.Add(1); return p; })
            .Build(2);

        var result = chain.Migrate(new Poco { Ver = 2 });

        Assert.Equal(2, result.Ver);
        Assert.Empty(result.Steps);   // no step ran
    }

    [Fact]
    public void Migrate_NewerFileThanCurrent_IsNoOp()
    {
        var chain = MigrationChain.For<Poco>(p => p.Ver, (p, v) => p.Ver = v)
            .Step(1, p => { p.Steps.Add(1); return p; })
            .Build(2);

        var result = chain.Migrate(new Poco { Ver = 5 });   // file from a newer build

        Assert.Equal(5, result.Ver);
        Assert.Empty(result.Steps);
    }
}
